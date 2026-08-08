using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;

namespace MyAiGen;

public enum KoboldMode { Text, Image, Video, Audio, Vision }

public sealed class KoboldCppProcess : IDisposable
{
    private static readonly int JobExtendedLimitInfoSize = Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
    private static readonly System.Net.Http.HttpClient AbortHttp = new() { Timeout = TimeSpan.FromSeconds(3) };

    private Process? _process;
    private readonly object _lifecycleLock = new();
    private IntPtr _jobHandle;
    private readonly string _exePath;
    private readonly int _port;
    private readonly string _modelPath;
    private readonly string? _textModelPath;
    private readonly string? _clipLPath;
    private readonly string? _t5Path;
    private readonly string? _vaePath;
    private readonly int _gpuLayers;
    private readonly bool _useCuda;
    private readonly bool _useVulkan;
    private readonly int _threads;
    private readonly int _contextSize;
    private readonly int _batchSize;
    private readonly int _blasBatchSize;
    private readonly bool _noKvOffload;
    private readonly string _useMlock;
    private readonly bool _useMmap;
    private readonly bool _keepClipOnCpu;
    private readonly bool _sdClipOnCpu;
    private readonly bool _sdVaeOnCpu;
    private readonly string _flashAttention;
    private readonly string _contextShift;
    private readonly bool _launchBrowser;
    private readonly string _useMmq;
    private readonly string _fastForwarding;
    private readonly string _allowSwa;
    private readonly string? _sdLoraPath;
    private readonly float _sdLoraMult;
    private readonly string? _textLoraPath;
    private readonly float _textLoraMult;
    private readonly string? _videoLoraPath;
    private readonly float _videoLoraMult;
    private readonly string? _audioLoraPath;
    private readonly float _audioLoraMult;
    private readonly bool _sdFlashAttention;
    private readonly int _sdTiledVae;
    private readonly string _sdConvDirect;
    private readonly string _runtimeLora;
    private readonly bool _videoEnabled;
    private readonly string? _videoModelPath;
    private readonly string? _videoVaePath;
    private readonly string? _videoT5Path;
    private readonly string? _audioModelPath;
    private readonly string? _voiceModelPath;
    private readonly string? _voiceTokenizerPath;
    private readonly string? _voiceTtsDir;
    private readonly string? _musicLlmPath;
    private readonly string? _musicDiffusionPath;
    private readonly string? _musicEmbeddingsPath;
    private readonly string? _musicVaePath;
    private readonly bool _musicVaeOnCpu;
    private readonly string? _visionModelPath;
    private readonly string? _visionMmprojPath;
    private readonly bool _visionMmprojCpu;
    private readonly string? _textMmprojPath;
    private readonly bool _textMmprojCpu;
    private readonly int _textMoeExpertsOverride;
    private readonly string _textMoeCpuMode;
    private readonly int _textMoeCpuLayers;
    private readonly int _visionMoeExpertsOverride;
    private readonly string _visionMoeCpuMode;
    private readonly int _visionMoeCpuLayers;
    private readonly string? _extraArgs;
    private readonly bool _enableWebSearch;
    private readonly bool _noCertify;
    private readonly string? _mcpFilePath;
    private readonly string? _chatTemplate;
    private readonly string _textQuantKv;
    private readonly double _textRopeScale;
    private readonly double _textRopeBase;
    private readonly string _visionQuantKv;
    private readonly double _visionRopeScale;
    private readonly double _visionRopeBase;
    private readonly string _smartContext;
    private readonly int _overrideNativeContext;
    private readonly string _tensorSplit;
    private readonly string _noAvx2;
    private readonly string _failsafe;
    private readonly int _debugMode;
    private readonly string _overrideTensors;
    private readonly string _overrideKv;
    private readonly int _cacheSlots;
    private readonly int _defaultGenAmt;
    private readonly string _enableGuidance;
    private readonly string _thinkEffort;
    private readonly int _swaPadding;
    private readonly string? _draftModelPath;
    private readonly int _draftAmount;
    private readonly string _useMtp;
    private readonly int _draftGpuLayers;
    private readonly string? _embedsModelPath;
    private readonly int _embedsMaxCtx;
    private readonly string _embedsGpu;
    private readonly string _autoFit;
    private readonly KoboldMode _mode;
    private bool _disposed;
    /// <summary>Session-level cache: once detected, shared across all process instances.</summary>
    private static bool? _hasBuiltInTemplate;

    /// <summary>null=unknown, true=built-in template detected, false=using default template.</summary>
    public static bool? HasBuiltInTemplate => _hasBuiltInTemplate;

    /// <summary>True when Jinja tool calling should be used: either a chat template was explicitly
    /// provided, or the model has a built-in template (or startup detection hasn't run yet).</summary>
    public bool UseJinjaTools =>
        !string.IsNullOrWhiteSpace(_chatTemplate) || _hasBuiltInTemplate != false;

    private readonly DataReceivedEventHandler _outputHandler;
    private readonly DataReceivedEventHandler _errorHandler;
    private readonly EventHandler _exitedHandler;

    public int Port => _port;
    public bool IsRunning => _process is { HasExited: false };

    public event Action<string>? OutputReceived;
    public event Action<string>? ErrorReceived;
    public event Action? ProcessExited;

    public KoboldCppProcess(
        string exePath, int port, string modelPath,
        string? textModelPath = null, string? clipLPath = null, string? t5Path = null, string? vaePath = null,
        int gpuLayers = -1, int threads = 7, int contextSize = 4096, int batchSize = 512, int blasBatchSize = 512,
        bool noKvOffload = false, string useMlock = "disable", bool useMmap = false,
        bool keepClipOnCpu = false, string? backend = null, string? extraArgs = null,
        string flashAttention = "enable", string contextShift = "enable", bool launchBrowser = false, string useMmq = "enable",
        string? sdLoraPath = null, float sdLoraMult = 1.0f, bool sdFlashAttention = false,
        int sdTiledVae = 640, string sdConvDirect = "off", string runtimeLora = "disabled",
        string fastForwarding = "enable", string allowSwa = "enable",
        bool sdClipOnCpu = false, bool sdVaeOnCpu = false,
        bool videoEnabled = false, string? videoModelPath = null, string? videoVaePath = null, string? videoT5Path = null,
        string? audioModelPath = null, string? voiceModelPath = null, string? voiceTokenizerPath = null, string? voiceTtsDir = null,
        string? visionModelPath = null, string? visionMmprojPath = null, bool visionMmprojCpu = false,
        string? textMmprojPath = null, bool textMmprojCpu = false,
        int textMoeExpertsOverride = -1, string textMoeCpuMode = "disabled", int textMoeCpuLayers = 999,
        int visionMoeExpertsOverride = -1, string visionMoeCpuMode = "disabled", int visionMoeCpuLayers = 999,
        string? textLoraPath = null, float textLoraMult = 1.0f,
        string? videoLoraPath = null, float videoLoraMult = 1.0f,
        string? audioLoraPath = null, float audioLoraMult = 1.0f,
        bool enableWebSearch = false, bool noCertify = false, string? mcpFilePath = null,
        string? chatTemplate = null,
        string? musicLlmPath = null, string? musicDiffusionPath = null,
        string? musicEmbeddingsPath = null, string? musicVaePath = null, bool musicVaeOnCpu = false,
        string textQuantKv = "f16", double textRopeScale = 1.0, double textRopeBase = 10000.0,
        string visionQuantKv = "f16", double visionRopeScale = 1.0, double visionRopeBase = 10000.0,
        string smartContext = "disable", int overrideNativeContext = 0, string tensorSplit = "",
        string noAvx2 = "disable", string failsafe = "disable", int debugMode = 0,
        string overrideTensors = "", string overrideKv = "",
        int cacheSlots = 5, int defaultGenAmt = 1536,
        string enableGuidance = "disable", string thinkEffort = "default", int swaPadding = 0,
        string? draftModelPath = null, int draftAmount = 4, string useMtp = "disable", int draftGpuLayers = -1,
        string? embedsModelPath = null, int embedsMaxCtx = 4096, string embedsGpu = "disable",
        string autoFit = "disable",
        KoboldMode mode = KoboldMode.Image)
    {
        _exePath = exePath;
        _port = port;
        _modelPath = modelPath;
        _textModelPath = textModelPath;
        _clipLPath = clipLPath;
        _t5Path = t5Path;
        _vaePath = vaePath;
        _gpuLayers = gpuLayers;
        _useCuda = string.Equals(backend, "cuda", StringComparison.OrdinalIgnoreCase);
        _useVulkan = string.Equals(backend, "vulkan", StringComparison.OrdinalIgnoreCase);
        _threads = threads;
        _contextSize = contextSize;
        _batchSize = batchSize;
        _blasBatchSize = blasBatchSize;
        _noKvOffload = noKvOffload;
        _useMlock = useMlock;
        _useMmap = useMmap;
        _keepClipOnCpu = keepClipOnCpu;
        _flashAttention = flashAttention;
        _contextShift = contextShift;
        _launchBrowser = launchBrowser;
        _useMmq = useMmq;
        _fastForwarding = fastForwarding;
        _allowSwa = allowSwa;
        _sdLoraPath = sdLoraPath;
        _sdLoraMult = sdLoraMult;
        _sdFlashAttention = sdFlashAttention;
        _sdTiledVae = sdTiledVae;
        _sdConvDirect = sdConvDirect;
        _runtimeLora = runtimeLora;
        _sdClipOnCpu = sdClipOnCpu;
        _sdVaeOnCpu = sdVaeOnCpu;
        _videoEnabled = videoEnabled;
        _videoModelPath = videoModelPath;
        _videoVaePath = videoVaePath;
        _videoT5Path = videoT5Path;
        _audioModelPath = audioModelPath;
        _voiceModelPath = voiceModelPath;
        _voiceTokenizerPath = voiceTokenizerPath;
        _voiceTtsDir = voiceTtsDir;
        _musicLlmPath = musicLlmPath;
        _musicDiffusionPath = musicDiffusionPath;
        _musicEmbeddingsPath = musicEmbeddingsPath;
        _musicVaePath = musicVaePath;
        _musicVaeOnCpu = musicVaeOnCpu;
        _textQuantKv = textQuantKv;
        _textRopeScale = textRopeScale;
        _textRopeBase = textRopeBase;
        _visionQuantKv = visionQuantKv;
        _visionRopeScale = visionRopeScale;
        _visionRopeBase = visionRopeBase;
        _smartContext = smartContext;
        _overrideNativeContext = overrideNativeContext;
        _tensorSplit = tensorSplit;
        _noAvx2 = noAvx2;
        _failsafe = failsafe;
        _debugMode = debugMode;
        _overrideTensors = overrideTensors;
        _overrideKv = overrideKv;
        _cacheSlots = cacheSlots;
        _defaultGenAmt = defaultGenAmt;
        _enableGuidance = enableGuidance;
        _thinkEffort = thinkEffort;
        _swaPadding = swaPadding;
        _draftModelPath = draftModelPath;
        _draftAmount = draftAmount;
        _useMtp = useMtp;
        _draftGpuLayers = draftGpuLayers;
        _embedsModelPath = embedsModelPath;
        _embedsMaxCtx = embedsMaxCtx;
        _embedsGpu = embedsGpu;
        _autoFit = autoFit;
        _visionModelPath = visionModelPath;
        _visionMmprojPath = visionMmprojPath;
        _visionMmprojCpu = visionMmprojCpu;
        _textMmprojPath = textMmprojPath;
        _textMmprojCpu = textMmprojCpu;
        _textMoeExpertsOverride = textMoeExpertsOverride;
        _textMoeCpuMode = textMoeCpuMode;
        _textMoeCpuLayers = textMoeCpuLayers;
        _visionMoeExpertsOverride = visionMoeExpertsOverride;
        _visionMoeCpuMode = visionMoeCpuMode;
        _visionMoeCpuLayers = visionMoeCpuLayers;
        _textLoraPath = textLoraPath;
        _textLoraMult = textLoraMult;
        _videoLoraPath = videoLoraPath;
        _videoLoraMult = videoLoraMult;
        _audioLoraPath = audioLoraPath;
        _audioLoraMult = audioLoraMult;
        _enableWebSearch = enableWebSearch;
        _noCertify = noCertify;
        _mcpFilePath = mcpFilePath;
        _chatTemplate = chatTemplate;
        _mode = mode;
        _extraArgs = extraArgs;
        _outputHandler = (_, e) =>
        {
            if (e.Data == null) return;
            if (e.Data.Contains("chat_template: Using built-in chat template", StringComparison.OrdinalIgnoreCase))
                _hasBuiltInTemplate = true;
            else if (e.Data.Contains("chat_template: No chat template", StringComparison.OrdinalIgnoreCase))
                _hasBuiltInTemplate = false;
            OutputReceived?.Invoke(e.Data);
        };
        _errorHandler = (_, e) => { if (e.Data != null) ErrorReceived?.Invoke(e.Data); };
        _exitedHandler = (_, _) => ProcessExited?.Invoke();
    }

    private static void RequireFile(string? path, string label)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            throw new FileNotFoundException($"{label} file not found", path);
    }

    private static void AppendIfExists(System.Text.StringBuilder sb, string flag, string? path)
    {
        if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
        {
            sb.Append(flag).Append(" \"").Append(path).Append("\" ");
        }
    }

    private static void AppendLora(System.Text.StringBuilder sb, string? loraPath, float loraMult, string flag, string multFlag)
    {
        if (!string.IsNullOrWhiteSpace(loraPath) && File.Exists(loraPath))
        {
            sb.Append(flag).Append(" \"").Append(loraPath).Append("\" ");
            if (loraMult != 1.0f)
                sb.Append(multFlag).Append(' ').Append(loraMult).Append(' ');
        }
    }

    // Appends --moeexperts / --moecpu for MoE (Mixture of Experts) models.
    // Note: per koboldcpp docs, --moecpu is not compatible with --autofit, manual
    // tensor overrides, or tensor splits — caller is responsible for not combining these.
    private static void AppendMoeFlags(System.Text.StringBuilder sb, int expertsOverride, string cpuMode, int cpuLayers)
    {
        if (expertsOverride > 0)
            sb.Append("--moeexperts ").Append(expertsOverride).Append(' ');

        if (string.Equals(cpuMode, "all", StringComparison.OrdinalIgnoreCase))
            sb.Append("--moecpu ");
        else if (string.Equals(cpuMode, "custom", StringComparison.OrdinalIgnoreCase) && cpuLayers > 0)
            sb.Append("--moecpu ").Append(cpuLayers).Append(' ');
    }

    private string BuildArgs()
    {
        var sb = new System.Text.StringBuilder(2048);

        if (_useCuda)
            sb.Append("--usecuda ");
        else if (_useVulkan)
            sb.Append("--usevulkan ");

        sb.Append("--highpriority ");

        if (string.Equals(_flashAttention, "enable", StringComparison.OrdinalIgnoreCase) &&
            (_mode is KoboldMode.Text or KoboldMode.Vision))
            sb.Append("--flashattention ");

        switch (_mode)
        {
            case KoboldMode.Text:
                RequireFile(_textModelPath, "Text model");
                sb.Append("--model \"").Append(_textModelPath).Append("\" ");
                AppendIfExists(sb, "--mmproj", _textMmprojPath);
                if (_textMmprojCpu && !string.IsNullOrWhiteSpace(_textMmprojPath))
                    sb.Append("--mmprojcpu ");
                AppendLora(sb, _textLoraPath, _textLoraMult, "--lora", "--loramult");
                AppendMoeFlags(sb, _textMoeExpertsOverride, _textMoeCpuMode, _textMoeCpuLayers);
                if (!string.Equals(_textQuantKv, "f16", StringComparison.OrdinalIgnoreCase))
                    sb.Append("--quantkv ").Append(_textQuantKv).Append(' ');
                if (Math.Abs(_textRopeScale - 1.0) > 0.001 || Math.Abs(_textRopeBase - 10000.0) > 0.001)
                    sb.Append("--ropeconfig ").Append(_textRopeScale.ToString("F4")).Append(' ').Append(_textRopeBase.ToString("F4")).Append(' ');
                // Always enabled: --websearch just registers the /api/extra/websearch proxy,
                // it doesn't load a model or use resources until a query actually hits it.
                sb.Append("--websearch ");
                if (_noCertify)
                    sb.Append("--nocertify ");
                if (!string.IsNullOrWhiteSpace(_mcpFilePath))
                    sb.Append("--mcpfile \"").Append(_mcpFilePath).Append("\" ");
                if (!string.IsNullOrWhiteSpace(_chatTemplate))
                {
                    if (File.Exists(_chatTemplate))
                        sb.Append("--chat-template-file \"").Append(_chatTemplate).Append("\" ");
                    else
                        sb.Append("--chat-template \"").Append(_chatTemplate).Append("\" ");
                }
                AppendIfExists(sb, "--draftmodel", _draftModelPath);
                if (_draftAmount > 0)
                    sb.Append("--draftamount ").Append(_draftAmount).Append(' ');
                if (string.Equals(_useMtp, "enable", StringComparison.OrdinalIgnoreCase))
                    sb.Append("--usemtp ");
                if (_draftGpuLayers > 0)
                    sb.Append("--draftgpulayers ").Append(_draftGpuLayers).Append(' ');
                AppendIfExists(sb, "--embeddingsmodel", _embedsModelPath);
                if (_embedsMaxCtx > 0)
                    sb.Append("--embeddingsmaxctx ").Append(_embedsMaxCtx).Append(' ');
                if (string.Equals(_embedsGpu, "enable", StringComparison.OrdinalIgnoreCase))
                    sb.Append("--embeddingsgpu ");
                // Auto-detect Jinja vs Universal tool calling:
                // - If user provided a chat template → --jinjatools with that template
                // - If no user template but model has a built-in one (or undetected/null) → --jinjatools
                // - If model confirmed no built-in template → no --jinjatools (Universal fallback)
                if (!string.IsNullOrWhiteSpace(_chatTemplate))
                    sb.Append("--jinjatools ");
                else if (_hasBuiltInTemplate != false)
                    sb.Append("--jinjatools ");
                break;

            case KoboldMode.Image:
                RequireFile(_modelPath, "Image model");
                sb.Append("--sdmodel \"").Append(_modelPath).Append("\" ");
                AppendIfExists(sb, "--sdclip1", _clipLPath);
                AppendIfExists(sb, "--sdt5xxl", _t5Path);
                AppendIfExists(sb, "--sdvae", _vaePath);
                break;

            case KoboldMode.Video:
                RequireFile(_videoModelPath, "Video model");
                sb.Append("--sdmodel \"").Append(_videoModelPath).Append("\" ");
                AppendIfExists(sb, "--sdvae", _videoVaePath);
                AppendIfExists(sb, "--sdt5xxl", _videoT5Path);
                AppendLora(sb, _videoLoraPath, _videoLoraMult, "--sdlora", "--sdloramult");
                break;

            case KoboldMode.Audio:
                RequireFile(_audioModelPath, "Audio model");
                sb.Append("--whispermodel \"").Append(_audioModelPath).Append("\" ");
                if (!string.IsNullOrWhiteSpace(_voiceModelPath) && File.Exists(_voiceModelPath))
                {
                    AppendIfExists(sb, "--ttsmodel", _voiceModelPath);
                    AppendIfExists(sb, "--ttswavtokenizer", _voiceTokenizerPath);
                    sb.Append("--ttsgpu ");
                    if (!string.IsNullOrWhiteSpace(_voiceTtsDir))
                    {
                        Directory.CreateDirectory(_voiceTtsDir);
                        sb.Append("--ttsdir \"").Append(_voiceTtsDir).Append("\" ");
                    }
                }
                AppendIfExists(sb, "--musicllm", _musicLlmPath);
                AppendIfExists(sb, "--musicdiffusion", _musicDiffusionPath);
                AppendIfExists(sb, "--musicembeddings", _musicEmbeddingsPath);
                AppendIfExists(sb, "--musicvae", _musicVaePath);
                if (_musicVaeOnCpu) sb.Append("--musiclowvram ");
                break;

            case KoboldMode.Vision:
                RequireFile(_visionModelPath, "Vision model");
                sb.Append("--model \"").Append(_visionModelPath).Append("\" ");
                AppendIfExists(sb, "--mmproj", _visionMmprojPath);
                if (_visionMmprojCpu && !string.IsNullOrWhiteSpace(_visionMmprojPath))
                    sb.Append("--mmprojcpu ");
                AppendMoeFlags(sb, _visionMoeExpertsOverride, _visionMoeCpuMode, _visionMoeCpuLayers);
                if (!string.Equals(_visionQuantKv, "f16", StringComparison.OrdinalIgnoreCase))
                    sb.Append("--quantkv ").Append(_visionQuantKv).Append(' ');
                if (Math.Abs(_visionRopeScale - 1.0) > 0.001 || Math.Abs(_visionRopeBase - 10000.0) > 0.001)
                    sb.Append("--ropeconfig ").Append(_visionRopeScale.ToString("F4")).Append(' ').Append(_visionRopeBase.ToString("F4")).Append(' ');
                break;
        }

        if (_mode is KoboldMode.Image or KoboldMode.Video)
        {
            if (_sdTiledVae > 0) { sb.Append("--sdtiledvae ").Append(_sdTiledVae).Append(' '); }
            if (_sdVaeOnCpu) sb.Append("--sdvaecpu ");
            if (_sdFlashAttention) sb.Append("--sdflashattention ");
        }

        if (_mode is KoboldMode.Image or KoboldMode.Video)
        {
            if (!string.IsNullOrWhiteSpace(_sdLoraPath) && _runtimeLora != "disabled")
            {
                if (_runtimeLora == "directory" && Directory.Exists(_sdLoraPath))
                    sb.Append("--sdlora \"").Append(_sdLoraPath).Append("\" ");
                else if (_runtimeLora == "file" && File.Exists(_sdLoraPath))
                    sb.Append("--sdlora \"").Append(_sdLoraPath).Append("\" ");
            }
            if (_sdLoraMult != 1.0f)
                sb.Append("--sdloramult ").Append(_sdLoraMult).Append(' ');
        }

        if (_mode is KoboldMode.Image or KoboldMode.Video &&
            (string.Equals(_sdConvDirect, "vaeonly", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(_sdConvDirect, "full", StringComparison.OrdinalIgnoreCase)))
            sb.Append("--sdconvdirect ").Append(_sdConvDirect).Append(' ');

        sb.Append("--port ").Append(_port).Append(' ');
        if (_gpuLayers >= 0)
            sb.Append("--gpulayers ").Append(_gpuLayers).Append(' ');
        if (_threads > 0)
            sb.Append("--threads ").Append(_threads).Append(' ');
        if (_contextSize > 0)
            sb.Append("--contextsize ").Append(_contextSize).Append(' ');
        if (_batchSize > 0)
            sb.Append("--batchsize ").Append(_batchSize).Append(' ');
        sb.Append("--blasbatchsize ").Append(_blasBatchSize).Append(' ');

        if (_noKvOffload)
            sb.Append("--lowvram ");
        if (string.Equals(_useMlock, "enable", StringComparison.OrdinalIgnoreCase))
            sb.Append("--usemlock ");
        if (!_useMmap)
            sb.Append("--nommap ");
        if (_mode is KoboldMode.Image or KoboldMode.Video)
        {
            if (_keepClipOnCpu)
                sb.Append("--sdoffloadcpu ");
            if (_sdClipOnCpu)
                sb.Append("--sdclipdevice -1 ");
            if (_sdVaeOnCpu)
                sb.Append("--sdvaedevice -1 ");
        }
        if (!string.Equals(_contextShift, "enable", StringComparison.OrdinalIgnoreCase))
            sb.Append("--noshift ");
        if (_launchBrowser)
            sb.Append("--launch ");
        if (!string.Equals(_useMmq, "enable", StringComparison.OrdinalIgnoreCase))
            sb.Append("--nommq ");
        if (!string.Equals(_fastForwarding, "enable", StringComparison.OrdinalIgnoreCase))
            sb.Append("--nofastforward ");
        if (string.Equals(_allowSwa, "enable", StringComparison.OrdinalIgnoreCase))
            sb.Append("--useswa ");

        if (string.Equals(_smartContext, "enable", StringComparison.OrdinalIgnoreCase))
            sb.Append("--smartcontext ");
        if (_overrideNativeContext > 0)
            sb.Append("--overridenativecontext ").Append(_overrideNativeContext).Append(' ');
        if (!string.IsNullOrWhiteSpace(_tensorSplit))
            sb.Append("--tensor_split ").Append(_tensorSplit).Append(' ');
        if (string.Equals(_noAvx2, "enable", StringComparison.OrdinalIgnoreCase))
            sb.Append("--noavx2 ");
        if (string.Equals(_failsafe, "enable", StringComparison.OrdinalIgnoreCase))
            sb.Append("--failsafe ");
        if (string.Equals(_autoFit, "enable", StringComparison.OrdinalIgnoreCase))
            sb.Append("--autofit ");
        if (_debugMode > 0)
            sb.Append("--debugmode ").Append(_debugMode).Append(' ');
        if (!string.IsNullOrWhiteSpace(_overrideTensors))
            sb.Append("--overridetensors \"").Append(_overrideTensors).Append("\" ");
        if (!string.IsNullOrWhiteSpace(_overrideKv))
            sb.Append("--overridekv \"").Append(_overrideKv).Append("\" ");
        if (_cacheSlots > 0)
            sb.Append("--smartcache ").Append(_cacheSlots).Append(' ');
        if (_defaultGenAmt > 0)
            sb.Append("--defaultgenamt ").Append(_defaultGenAmt).Append(' ');
        if (string.Equals(_enableGuidance, "enable", StringComparison.OrdinalIgnoreCase))
            sb.Append("--enableguidance ");
        if (!string.Equals(_thinkEffort, "default", StringComparison.OrdinalIgnoreCase))
            sb.Append("--reasoningeffort ").Append(_thinkEffort).Append(' ');
        if (_swaPadding > 0)
            sb.Append("--swapadding ").Append(_swaPadding).Append(' ');

        if (!string.IsNullOrWhiteSpace(_extraArgs))
            sb.Append(_extraArgs!.TrimEnd()).Append(' ');

        if (sb.Length > 0 && sb[^1] == ' ') sb.Length--;
        return sb.ToString();
    }

    public void Start()
    {
        lock (_lifecycleLock)
        {
            if (IsRunning) return;
            if (!File.Exists(_exePath))
                throw new FileNotFoundException("KoboldCpp executable not found", _exePath);

            var args = BuildArgs();
            var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = _exePath,
                    Arguments = args,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                },
                EnableRaisingEvents = true
            };

            process.OutputDataReceived += _outputHandler;
            process.ErrorDataReceived += _errorHandler;
            process.Exited += _exitedHandler;

            process.Start();
            _process = process;
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            AssignToJob();
        }
    }

    private void AssignToJob()
    {
        try
        {
            const int JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x2000;
            _jobHandle = CreateJobObject(IntPtr.Zero, null);
            if (_jobHandle == IntPtr.Zero) return;

            var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION
            {
                BasicLimitInformation = new JOBOBJECT_BASIC_LIMIT_INFORMATION
                {
                    LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
                }
            };
            int size = JobExtendedLimitInfoSize;
            IntPtr ptr = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.StructureToPtr(info, ptr, false);
                SetInformationJobObject(_jobHandle, 9, ptr, size);
            }
            finally
            {
                Marshal.FreeHGlobal(ptr);
            }

            if (!AssignProcessToJobObject(_jobHandle, _process!.Handle))
            {
                CloseHandle(_jobHandle);
                _jobHandle = IntPtr.Zero;
            }
        }
        catch
        {
            if (_jobHandle != IntPtr.Zero) { CloseHandle(_jobHandle); _jobHandle = IntPtr.Zero; }
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateJobObject(IntPtr security, string? name);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(IntPtr job, int infoType, IntPtr info, int size);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public int LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public int ActiveProcessLimit;
        public UIntPtr Affinity;
        public int PriorityClass;
        public int SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IO_COUNTERS
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    public void Stop()
    {
        lock (_lifecycleLock)
        {
            if (_process is not { HasExited: false })
            {
                UnsubscribeProcessEvents();
                CloseJobHandle();
                _process = null;
                return;
            }
            try
            {
                try
                {
                    using var req = new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Post,
                        $"http://localhost:{_port}/api/extra/abort");
                    AbortHttp.Send(req, HttpCompletionOption.ResponseHeadersRead);
                }
                catch { }

                if (!_process.WaitForExit(5000))
                {
                    _process.Kill(entireProcessTree: true);
                    _process.WaitForExit(5000);
                }
            }
            catch { }
            finally
            {
                UnsubscribeProcessEvents();
                _process?.Close();
                _process = null;
            }
        }
    }

    private void UnsubscribeProcessEvents()
    {
        if (_process == null) return;
        _process.OutputDataReceived -= _outputHandler;
        _process.ErrorDataReceived -= _errorHandler;
        _process.Exited -= _exitedHandler;
        try { _process.CancelOutputRead(); } catch { }
        try { _process.CancelErrorRead(); } catch { }
    }

    private void CloseJobHandle()
    {
        if (_jobHandle != IntPtr.Zero)
        {
            CloseHandle(_jobHandle);
            _jobHandle = IntPtr.Zero;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
        _process?.Dispose();
        CloseJobHandle();
    }
}