using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace MyAiGen;

public sealed class AgentSession
{
    public enum TodoStatus { Pending, Done }

    public class TodoItem
    {
        public int Id { get; set; }
        public string Text { get; set; } = "";
        public TodoStatus Status { get; set; } = TodoStatus.Pending;
    }
    public List<string> Notes = new();
    public int WritesSinceLastNotesUpdate = 0;
    // Relative paths of every write_file/delete_file/move_file/copy_file since the last
    // update_notes call. Paired with WritesSinceLastNotesUpdate: a todo_complete id is
    // honored only if its item text names one of these files, or a separate budget token
    // pays for it (see UpdateNotes). Reset together with the counter when closures land.
    public List<string> MutatedPathsSinceNotesUpdate = new();
    public int EditsSinceLastRun { get; set; }        // incremented on every successful write_file
    public int ConsecutiveSingleEditBuilds { get; set; } // incremented/reset by RunCommand after each real build/test
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Title { get; set; } = "New Session";
    public string ProjectPath { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public List<ChatMessage> Messages { get; set; } = new();
    public bool IsAgentic { get; set; } = true;

    // Runtime-only agentic state (not serialized)
    [System.Text.Json.Serialization.JsonIgnore]
    public ReadLedger ReadLedger { get; } = new();
    [System.Text.Json.Serialization.JsonIgnore]
    public ProjectIndex? Index { get; set; }
    [System.Text.Json.Serialization.JsonIgnore]
    public DiscoveredPathsCollection DiscoveredPaths { get; } = new(maxSize: 300);
    [System.Text.Json.Serialization.JsonIgnore]
    public HashSet<string> KnownProcesses { get; } = new(System.StringComparer.OrdinalIgnoreCase);
    // Maps process name -> the actual PID(s) this session started via run_command.
    // task_kill uses this instead of killing by image name, so it can never touch a
    // same-named process this session didn't launch (e.g. the user's IDE, another
    // terminal, unrelated background tooling).
    [System.Text.Json.Serialization.JsonIgnore]
    public Dictionary<string, HashSet<int>> KnownProcessIds { get; } = new(System.StringComparer.OrdinalIgnoreCase);
    [System.Text.Json.Serialization.JsonIgnore]
    public string UserIntent { get; set; } = "";
    [System.Text.Json.Serialization.JsonIgnore]
    public List<TodoItem> TodoList { get; } = new();
    // Id counter for TodoItem entries. Reset to 1 together with TodoList.Clear() when a new
    // task starts (see MainWindow session reset) — list and counter always reset as a pair,
    // so an id the model saw in a previous task can never match a new item.
    [System.Text.Json.Serialization.JsonIgnore]
    public int NextTodoId { get; set; }
    // Tracks whether the most recent build/test run_command came back clean, and how
    // many write_file calls have happened since — lets run_command tell the model
    // "you already confirmed this, nothing changed" instead of relying on the model
    // to remember its own build history across the conversation.
    [System.Text.Json.Serialization.JsonIgnore]
    public bool LastBuildWasClean { get; set; } = false;
    [System.Text.Json.Serialization.JsonIgnore]
    public int WritesSinceLastCleanBuild { get; set; } = 0;
    // Tracks repeat search_files({content, pattern}) calls — search_files has no
    // re-read gate at all (unlike read_file's ReadLedger), so a model can otherwise
    // spam the identical grep query forever, including using a broad content regex
    // as a full-file-dump workaround for the read_file re-read gate.
    [System.Text.Json.Serialization.JsonIgnore]
    public Dictionary<string, int> SearchQueryCounts { get; } = new(System.StringComparer.OrdinalIgnoreCase);
    // Backing state for the blind-read cap in ReadFile: counts read_file calls made
    // while nothing has been committed to yet this task (no checklist, no build run).
    // HasRunCommandThisTask flips true the moment a real command actually executes
    // (see ExecuteRealShellCommand) and lifts the cap immediately.
    [System.Text.Json.Serialization.JsonIgnore]
    public int BlindReadCount { get; set; } = 0;
    [System.Text.Json.Serialization.JsonIgnore]
    public bool HasRunCommandThisTask { get; set; } = false;

    public void ResetAgenticState() { Index = null; DiscoveredPaths.Clear(); KnownProcesses.Clear(); KnownProcessIds.Clear(); UserIntent = ""; TodoList.Clear(); NextTodoId = 1; LastBuildWasClean = false; WritesSinceLastCleanBuild = 0; SearchQueryCounts.Clear(); BlindReadCount = 0; HasRunCommandThisTask = false; WritesSinceLastNotesUpdate = 0; MutatedPathsSinceNotesUpdate.Clear(); }
}

public sealed class AppSettings
{
    // KoboldCpp process
    public string KoboldExePath { get; set; } = "";
    public int KoboldPort { get; set; } = 5001;
    public string KoboldExtraArgs { get; set; } = "";

    // Model paths
    public string ModelPath { get; set; } = "";
    public string TextModelPath { get; set; } = "";
    public string ClipLPath { get; set; } = "";
    public string TextEncoderPath { get; set; } = "";
    public string ImageVaePath { get; set; } = "";
    public string TextLoraPath { get; set; } = "";
    public float TextLoraMult { get; set; } = 1.0f;
    public string VideoLoraPath { get; set; } = "";
    public float VideoLoraMult { get; set; } = 1.0f;
    public string AudioLoraPath { get; set; } = "";
    public float AudioLoraMult { get; set; } = 1.0f;

    // SD extras
    public string SdLoraPath { get; set; } = "";
    public float SdLoraMult { get; set; } = 1.0f;
    public bool SdFlashAttention { get; set; }
    public int SdTiledVae { get; set; } = 640;
    public string SdConvDirect { get; set; } = "off";  // off, vaeonly, full
    public string RuntimeLora { get; set; } = "disabled"; // disabled, file, directory

    // Backend
    public string Backend { get; set; } = "cuda";

    // Hardware
    public string GpuId { get; set; } = "Auto";
    public int GpuLayers { get; set; } = -1;
    public int Threads { get; set; } = 7;
    public int ContextSize { get; set; } = 4096;
    public int BatchSize { get; set; } = 512;
    public int BlasBatchSize { get; set; } = 512;
    public bool NoKvOffload { get; set; }
    public string UseMlock { get; set; } = "disable";
    public bool UseMmap { get; set; }
    public bool KeepClipOnCpu { get; set; }
    public bool SdClipOnCpu { get; set; }
    public bool SdVaeOnCpu { get; set; }
    public bool ImageUseExternalVae { get; set; }
    public bool VideoUseExternalVae { get; set; }
    public string FlashAttention { get; set; } = "enable";
    public string ContextShift { get; set; } = "enable";
    public bool LaunchBrowser { get; set; }
    public string UseMmq { get; set; } = "enable";
    public string FastForwarding { get; set; } = "enable";
    public string AllowSwa { get; set; } = "disable";
    public string AgenticNoShift { get; set; } = "enable";

    // General advanced
    public string SmartContext { get; set; } = "disable";
    public int OverrideNativeContext { get; set; }
    public string TensorSplit { get; set; } = "";
    public string NoAvx2 { get; set; } = "disable";
    public string Failsafe { get; set; } = "disable";
    public int DebugMode { get; set; }
    public string OverrideTensors { get; set; } = "";
    public string OverrideKv { get; set; } = "";
    public int CacheSlots { get; set; } = 5;
    public int DefaultGenAmt { get; set; } = 1536;
    public string EnableGuidance { get; set; } = "disable";
    public string ThinkEffort { get; set; } = "default";
    public int SwaPadding { get; set; }

    // Image defaults
    public int ImageWidth { get; set; } = 1024;
    public int ImageHeight { get; set; } = 1024;
    public int ImageSteps { get; set; } = 20;
    public float ImageCfgScale { get; set; } = 7f;
    public float ImageDenoisingStrength { get; set; } = 0.75f;
    public int ViewMode { get; set; } = 1; // 0=Free Mode, 1=Fixed

    // Last used
    public string Prompt { get; set; } = "";
    public string NegativePrompt { get; set; } = "";
    public string VideoPrompt { get; set; } = "";
    public string VideoNegativePrompt { get; set; } = "";
    public string KoboldCppVersion { get; set; } = "";

    // Audio
    public string AudioModelPath { get; set; } = "";
    public string VoiceModelPath { get; set; } = "";
    public string VoiceTokenizerPath { get; set; } = "";
    public string VoiceTtsDir { get; set; } = Path.Combine(Path.GetTempPath(), "MyAiGen");
    public string MusicLlmPath { get; set; } = "";
    public string MusicDiffusionPath { get; set; } = "";
    public string MusicEmbeddingsPath { get; set; } = "";
    public string MusicVaePath { get; set; } = "";
    public bool MusicVaeOnCpu { get; set; } = false;
    public string VisionModelPath { get; set; } = "";
    public string VisionMmprojPath { get; set; } = "";
    public bool VisionMmprojCpu { get; set; }
    public string TextMmprojPath { get; set; } = "";
    public bool TextMmprojCpu { get; set; }

    // MoE (Mixture of Experts) — Text
    public int TextMoeExpertsOverride { get; set; } = -1;
    public string TextMoeCpuMode { get; set; } = "disabled";
    public int TextMoeCpuLayers { get; set; } = 999;

    // Text model advanced
    public string TextQuantizedKvCache { get; set; } = "f16";
    public double TextRopeScale { get; set; } = 1.0;
    public double TextRopeBase { get; set; } = 10000.0;

    // Draft model
    public string DraftModelPath { get; set; } = "";
    public int DraftAmount { get; set; } = 4;
    public string UseMtp { get; set; } = "disable";
    public int DraftGpuLayers { get; set; } = -1;

    // Embeddings model
    public string EmbedsModelPath { get; set; } = "";
    public int EmbedsMaxCtx { get; set; } = 4096;
    public string EmbedsGpu { get; set; } = "disable";

    // Tool calling
    public string AutoFit { get; set; } = "disable";
    public string AgenticWorkflowMode { get; set; } = "disable";
    // "auto" = fully autonomous agent (default); "manual" = agent may pause and ask the user
    // for confirmation between options or for context via the ask_user tool.
    public string ConfirmMode { get; set; } = "auto";
    // When true, write_file runs an instant tree-sitter syntax pass on every .cs file
    // via the embedded interpreter at ./Python/python.exe — no MSBuild, no restore.
    // Silently no-ops if that interpreter/script isn't present. See TreeSitterChecker.
    public bool EnableTreeSitterCheck { get; set; } = true;
    // Same write_file pass also flags TODO/placeholder comments, NotImplementedException
    // throws, and empty method bodies.
    public bool EnableTreeSitterPlaceholderCheck { get; set; } = true;
    // Same pass also flags private fields/methods/properties never referenced elsewhere
    // in that file — same-file scope only (blind to other files of a partial class).
    public bool EnableTreeSitterDeadCodeCheck { get; set; } = true;
    public bool SendToolsToLocalBackend { get; set; } = true;
    // When false (default), block warnings like "Read loop guard triggered — reading file blocked",
    // "FileNotFound Guard Triggered", and other "BLOCKED:" tool results are hidden from the agent
    // transcript UI. The raw tool result is still sent to the model so agent behavior is unchanged.
    public bool DebugShowBlockWarnings { get; set; }

    // MoE (Mixture of Experts) — Vision
    public int VisionMoeExpertsOverride { get; set; } = -1;
    public string VisionMoeCpuMode { get; set; } = "disabled";
    public int VisionMoeCpuLayers { get; set; } = 999;

    // Vision model advanced
    public string VisionQuantizedKvCache { get; set; } = "f16";
    public double VisionRopeScale { get; set; } = 1.0;
    public double VisionRopeBase { get; set; } = 10000.0;

    // Video
    public bool VideoEnabled { get; set; }
    public string VideoModelPath { get; set; } = "";
    public string VideoVaePath { get; set; } = "";
    public string VideoT5Path { get; set; } = "";
    public int VideoFrames { get; set; } = 50;
    public int VideoFps { get; set; } = 16;
    public string VideoOutputFormat { get; set; } = "webm";
    public int VideoWidth { get; set; } = 512;
    public int VideoHeight { get; set; } = 512;
    public int VideoSteps { get; set; } = 15;
    public float VideoCfgScale { get; set; } = 1f;

    // Backend selection
    public string BackendMode { get; set; } = "local";
    public string OpenRouterModel { get; set; } = "google/gemma-2-9b-it:free";
    public string OpenRouterApiKey { get; set; } = "";
    public string ExternalProvider { get; set; } = "OpenRouter";
    public string CustomApiUrl { get; set; } = "";

    // Text mode features
    public string TextSystemPrompt { get; set; } = "";
    public float TextTemperature { get; set; } = 0.7f;
    public float TextTopP { get; set; } = 0.9f;
    public int TextTopK { get; set; } = 100;
    public float TextRepeatPenalty { get; set; } = 1.0f;
    public int TextTimeoutSeconds { get; set; } = 0;
    public string PlannerModelPath { get; set; } = "";
    public string PlannerTemplatePath { get; set; } = "";
    public bool PlannerEnabled { get; set; }
    public int PlannerContextSize { get; set; } = 2048;
    public float PlannerTemperature { get; set; } = 0.3f;
    public int PlannerTopK { get; set; } = 40;
    public float PlannerTopP { get; set; } = 0.9f;
    public float PlannerRepeatPenalty { get; set; } = 1.0f;
    public string PlannerOffload { get; set; } = "cpu";
    public bool EnableThinking { get; set; }
    public bool CompactPrompt { get; set; } = true;
    public string ThinkingEffort { get; set; } = "medium";
    public bool EnableWebSearch { get; set; }
    public bool NoCertify { get; set; }
    public AgenticWorkflow AgenticWorkflow { get; set; } = new();
    public bool ThumbnailPreview { get; set; } = true;
    public bool ShowTps { get; set; } = true;
    public string TokenSafetyMargin { get; set; } = "balance";
    public int MaxIterations { get; set; } = 30;
    public int StallNudgeThreshold { get; set; } = 6;
    public int StallLockoutThreshold { get; set; } = 10;
    public int ReadFileNudgeThreshold { get; set; } = 4;
    public int ReadFileHardStopThreshold { get; set; } = 8;
    public int ImageSeed { get; set; } = -1;
    public bool ImageRandomSeed { get; set; } = true;
    public int ImageBatchCount { get; set; } = 1;
    public long VideoSeed { get; set; } = -1;
    public bool VideoRandomSeed { get; set; } = true;
    public int AudioMode { get; set; }
    public int AudioSource { get; set; }
    public bool AudioOverlay { get; set; }
    public bool AudioToPrompt { get; set; }
    public bool AudioTranslate { get; set; }
    public double AudioFontSize { get; set; } = 48;
    public double AudioOpacity { get; set; } = 0.8;
    public string AudioFontName { get; set; } = "";
    public string AudioTextColor { get; set; } = "";
    public string VisionTargetLang { get; set; } = "";
    public double VisionFontSize { get; set; } = 48;
    public double VisionOpacity { get; set; } = 0.8;
    public string VisionFontName { get; set; } = "";
    public string VisionTextColor { get; set; } = "";
    public string VisionBgColor { get; set; } = "";
    public int MaxHistoryCount { get; set; } = 50;

    // Sessions
    public List<AgentSession> Sessions { get; set; } = new();
    public string ActiveSessionId { get; set; } = "";

    // Server features
    public string MCPFilePath { get; set; } = "";
    public string TextChatTemplate { get; set; } = "";

    // Output
    public bool LogToFile { get; set; }

    // Output path
    public string OutputPath { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        "PromptWhizz", "cache");

    public void Save(string path)
    {
        var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(path, json);
    }

    public static AppSettings Load(string path)
    {
        if (!File.Exists(path)) return new AppSettings();
        try
        {
            var json = File.ReadAllText(path);
            var s = JsonSerializer.Deserialize<AppSettings>(json);
            if (s == null) return new AppSettings();
            if (s.ImageCfgScale <= 0) s.ImageCfgScale = 7;
            if (string.IsNullOrWhiteSpace(s.VoiceTtsDir))
                s.VoiceTtsDir = Path.Combine(Path.GetTempPath(), "MyAiGen");
            if (s.Sessions == null) s.Sessions = new List<AgentSession>();
            return s;
        }
        catch
        {
            return new AppSettings();
        }
    }
}

/// <summary>Ordered set of discovered file paths with a fixed max size.
/// Newest entries are at the end; when the set is full, the oldest entry
/// is evicted. This prevents a single broad search_files from flooding
/// the discovery set and causing the model to read every file.</summary>
public sealed class DiscoveredPathsCollection
{
    private readonly int _maxSize;
    private readonly List<string> _ordered = new();
    private readonly HashSet<string> _set = new(System.StringComparer.OrdinalIgnoreCase);

    public DiscoveredPathsCollection(int maxSize = 100) => _maxSize = maxSize;

    public bool Contains(string path) => _set.Contains(path);
    public int Count => _set.Count;

    public void Add(string path)
    {
        if (_set.Add(path))
        {
            _ordered.Add(path);
            Trim();
        }
    }

    public bool Remove(string path)
    {
        if (!_set.Remove(path)) return false;
        _ordered.Remove(path);
        return true;
    }

    public void Clear()
    {
        _set.Clear();
        _ordered.Clear();
    }

    private void Trim()
    {
        while (_ordered.Count > _maxSize)
        {
            var oldest = _ordered[0];
            _ordered.RemoveAt(0);
            _set.Remove(oldest);
        }
    }
}