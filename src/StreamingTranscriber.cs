using NAudio.Wave;
using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace MyAiGen;

    public sealed class StreamingTranscriber : IDisposable
{
    private static readonly byte[] RiffId = [(byte)'R', (byte)'I', (byte)'F', (byte)'F'];
    private static readonly byte[] WaveId = [(byte)'W', (byte)'A', (byte)'V', (byte)'E'];
    private static readonly byte[] FmtId = [(byte)'f', (byte)'m', (byte)'t', (byte)' '];
    private static readonly byte[] DataId = [(byte)'d', (byte)'a', (byte)'t', (byte)'a'];

    private readonly KoboldCppClient _client;
    private readonly Action<string>? _onChunk;
    private readonly Action<string>? _onError;
    private readonly string _tempDir = Path.GetTempPath();
    private readonly bool _useMicrophone;

    // VAD tuning
    private readonly double _silenceRmsThreshold;   // amplitude (0..1 float scale) below which we call it silence
    private readonly int _silenceHangoverMs;         // how much silence before we cut a chunk
    private readonly int _minChunkMs;                // don't emit chunks shorter than this (avoids junk cuts)
    private readonly int _maxChunkMs;                // force-cut even mid-speech so we don't stall forever
    private readonly int _preRollMs;                 // audio kept from before speech onset, prevents clipped word starts

    private WaveInEvent? _waveInCapture;
    private WasapiLoopbackCapture? _loopbackCapture;
    private MemoryStream? _buffer;
    private readonly object _lock = new();
    private CancellationTokenSource? _cts;
    private bool _running;
    private WaveFormat? _waveFormat;
    private bool _disposed;
    private string? _tempPath;

    // VAD state (touched only from the capture callback thread)
    private long _msSinceChunkStart;
    private long _msSinceLastVoice;
    private bool _hasVoiceInChunk;

    private readonly SemaphoreSlim _chunkSignal = new(0);
    private readonly ConcurrentQueue<(byte[] data, int len)> _chunkQueue = new();

    public bool IsRunning => _running;

    public bool TranslateToEnglish { get; set; }

    public StreamingTranscriber(KoboldCppClient client, bool useMicrophone = false,
        int chunkDurationMs = 3000,
        Action<string>? onChunk = null, Action<string>? onError = null,
        double silenceRmsThreshold = 0.010,
        int silenceHangoverMs = 450,
        int minChunkMs = 700,
        int maxChunkMs = 9000,
        int preRollMs = 200)
    {
        _client = client;
        _useMicrophone = useMicrophone;
        _onChunk = onChunk;
        _onError = onError;
        _silenceRmsThreshold = silenceRmsThreshold;
        _silenceHangoverMs = Math.Min(silenceHangoverMs, chunkDurationMs > 0 ? chunkDurationMs : silenceHangoverMs);
        _minChunkMs = Math.Min(minChunkMs, chunkDurationMs > 0 ? chunkDurationMs : minChunkMs);
        _maxChunkMs = Math.Max(maxChunkMs, silenceHangoverMs + minChunkMs);
        _preRollMs = preRollMs;
    }

    public void Start()
    {
        if (_running) return;
        _running = true;
        _cts = new CancellationTokenSource();
        _buffer = new MemoryStream();
        _tempPath = Path.Combine(_tempDir, "stt_chunk.wav");

        if (_useMicrophone)
        {
            _waveInCapture = new WaveInEvent();
            _waveFormat = _waveInCapture.WaveFormat;
            _waveInCapture.DataAvailable += OnDataAvailable;
            _waveInCapture.RecordingStopped += (_, _) => { };
            _waveInCapture.StartRecording();
        }
        else
        {
            _loopbackCapture = new WasapiLoopbackCapture();
            _waveFormat = _loopbackCapture.WaveFormat;
            _loopbackCapture.DataAvailable += OnDataAvailable;
            _loopbackCapture.RecordingStopped += (_, _) => { };
            _loopbackCapture.StartRecording();
        }

        _msSinceChunkStart = 0;
        _msSinceLastVoice = 0;
        _hasVoiceInChunk = false;

        _ = RunWorkerLoopAsync(_cts.Token);
    }

    public void Stop()
    {
        if (!_running) return;
        _running = false;
        _cts?.Cancel();
        if (_waveInCapture != null)
        {
            try { _waveInCapture.DataAvailable -= OnDataAvailable; } catch { }
            try { _waveInCapture.StopRecording(); } catch { }
        }
        if (_loopbackCapture != null)
        {
            try { _loopbackCapture.DataAvailable -= OnDataAvailable; } catch { }
            try { _loopbackCapture.StopRecording(); } catch { }
        }

        // flush whatever's left as a final chunk
        lock (_lock)
        {
            if (_buffer != null && _buffer.Length > 0)
            {
                EnqueueCurrentBufferLocked();
            }
            _buffer?.Dispose();
            _buffer = null;
        }
        _chunkSignal.Release();
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        if (!_running || _waveFormat == null) return;

        var bytesPerMs = _waveFormat.AverageBytesPerSecond / 1000.0;
        var frameMs = bytesPerMs > 0 ? e.BytesRecorded / bytesPerMs : 0;

        var rms = ComputeRms(e.Buffer, e.BytesRecorded, _waveFormat);
        var isVoice = rms >= _silenceRmsThreshold;

        lock (_lock)
        {
            _buffer?.Write(e.Buffer, 0, e.BytesRecorded);
            _msSinceChunkStart += (long)frameMs;

            if (isVoice)
            {
                _hasVoiceInChunk = true;
                _msSinceLastVoice = 0;
            }
            else
            {
                _msSinceLastVoice += (long)frameMs;
            }

            var haveMinDuration = _msSinceChunkStart >= _minChunkMs;
            var trailingSilenceHit = _hasVoiceInChunk && haveMinDuration && _msSinceLastVoice >= _silenceHangoverMs;
            var maxDurationHit = _msSinceChunkStart >= _maxChunkMs;

            if (trailingSilenceHit || maxDurationHit)
            {
                // Only bother transcribing chunks that actually contained voice at some point.
                if (_hasVoiceInChunk)
                    EnqueueCurrentBufferLocked();
                else
                    ResetChunkStateLocked(discard: true);
            }
        }
    }

    // Must be called with _lock held. Cuts the current buffer into the processing queue
    // and re-arms a fresh buffer, carrying a small pre-roll tail forward so the next
    // chunk doesn't start with a clipped word.
    private void EnqueueCurrentBufferLocked()
    {
        if (_buffer == null || _buffer.Length == 0)
        {
            ResetChunkStateLocked(discard: true);
            return;
        }

        var len = (int)_buffer.Length;
        var data = ArrayPool<byte>.Shared.Rent(len);
        _buffer.Position = 0;
        _buffer.Read(data, 0, len);
        _chunkQueue.Enqueue((data, len));
        _chunkSignal.Release();

        // carry a short pre-roll tail into the next chunk so speech that started
        // right at the cut boundary isn't lost / clipped at the front
        _buffer.SetLength(0);
        _buffer.Position = 0;
        if (_waveFormat != null && _preRollMs > 0)
        {
            var bytesPerMs = _waveFormat.AverageBytesPerSecond / 1000.0;
            var preRollBytes = (int)(bytesPerMs * _preRollMs);
            preRollBytes -= preRollBytes % Math.Max(1, _waveFormat.BlockAlign);
            if (preRollBytes > 0 && preRollBytes < len)
            {
                _buffer.Write(data, len - preRollBytes, preRollBytes);
            }
        }

        ResetChunkStateLocked(discard: false);
    }

    private void ResetChunkStateLocked(bool discard)
    {
        if (discard)
        {
            _buffer?.SetLength(0);
            _buffer?.Seek(0, SeekOrigin.Begin);
        }
        _msSinceChunkStart = _buffer != null && _waveFormat != null
            ? (long)(_buffer.Length / (_waveFormat.AverageBytesPerSecond / 1000.0))
            : 0;
        _msSinceLastVoice = 0;
        _hasVoiceInChunk = false;
    }

    private static double ComputeRms(byte[] buffer, int bytesRecorded, WaveFormat format)
    {
        if (bytesRecorded <= 0) return 0;

        double sumSquares = 0;
        int sampleCount = 0;

        if (format.Encoding == WaveFormatEncoding.IeeeFloat && format.BitsPerSample == 32)
        {
            for (int i = 0; i + 4 <= bytesRecorded; i += 4)
            {
                var sample = BitConverter.ToSingle(buffer, i);
                sumSquares += sample * sample;
                sampleCount++;
            }
        }
        else if (format.BitsPerSample == 16)
        {
            for (int i = 0; i + 2 <= bytesRecorded; i += 2)
            {
                var sample = BitConverter.ToInt16(buffer, i) / 32768.0;
                sumSquares += sample * sample;
                sampleCount++;
            }
        }
        else
        {
            // fallback: treat as bytes centered at 128 (8-bit PCM) or just skip VAD (assume voice)
            return 1.0;
        }

        if (sampleCount == 0) return 0;
        return Math.Sqrt(sumSquares / sampleCount);
    }

    private async Task RunWorkerLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await _chunkSignal.WaitAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { break; }

            while (_chunkQueue.TryDequeue(out var item))
            {
                var (rentedBuf, pcmLen) = item;
                try
                {
                    if (pcmLen < 1024) continue; // too short to be meaningful audio

                    var tempPath = _tempPath!;
                    WriteWavToFile(tempPath, rentedBuf, pcmLen, _waveFormat!);
                    var text = await _client.TranscribeAudioAsync(tempPath, TranslateToEnglish, ct).ConfigureAwait(false);
                    if (!string.IsNullOrWhiteSpace(text) && text != "(no transcription)")
                        _onChunk?.Invoke(text);
                }
                catch (OperationCanceledException) { }
                catch (Exception ex)
                {
                    _onError?.Invoke(ex.Message);
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(rentedBuf);
                }
            }

            if (!_running && _chunkQueue.IsEmpty) break;
        }
    }

    private static void WriteWavToFile(string path, byte[] pcmData, int pcmLength, WaveFormat format)
    {
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        using var writer = new BinaryWriter(fs);
        var dataSize = pcmLength;
        var fileSize = 36 + dataSize;
        writer.Write(RiffId);
        writer.Write(fileSize);
        writer.Write(WaveId);
        writer.Write(FmtId);
        writer.Write(16);
        writer.Write((short)format.Encoding);
        writer.Write((short)format.Channels);
        writer.Write(format.SampleRate);
        writer.Write(format.AverageBytesPerSecond);
        writer.Write((short)format.BlockAlign);
        writer.Write((short)format.BitsPerSample);
        writer.Write(DataId);
        writer.Write(dataSize);
        writer.Write(pcmData, 0, pcmLength);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
        _waveInCapture?.Dispose();
        _loopbackCapture?.Dispose();
        _cts?.Dispose();
        _chunkSignal.Dispose();
    }
}