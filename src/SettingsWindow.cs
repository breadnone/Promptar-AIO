using System.Diagnostics;
using System.IO;
using System.Management;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Path = System.IO.Path;
using static MyAiGen.AppTheme;

namespace MyAiGen;

public sealed class SettingsWindow : Window
{
    private readonly AppSettings _settings;
    private readonly string _configPath;
    public event Action? SettingsApplied;
    public event Action? UpdateRequested;

    private TextBox _exePathBox = null!;
    private TextBox _modelPathBox = null!;
    private TextBox _textModelBox = null!;
    private TextBox _clipLBox = null!;
    private TextBox _t5Box = null!;
    private TextBox _vaeBox = null!;
    private ComboBox _imageVaeCombo = null!;
    private ComboBox _videoVaeCombo = null!;
    private TextBox _extraArgsBox = null!;
    private TextBox _portBox = null!;
    private TextBox _threadsBox = null!;
    private TextBox _contextSizeBox = null!;
    private TextBox _batchSizeBox = null!;
    private TextBox _blasBatchSizeBox = null!;
    private TextBox _gpuLayersBox = null!;
    private ComboBox _noKvOffloadCombo = null!;
    private ComboBox _mmapCombo = null!;
    private ComboBox _keepClipOnCpuCombo = null!;
    private ComboBox _sdClipOnCpuCombo = null!;
    private ComboBox _sdVaeOnCpuCombo = null!;
    private ComboBox _launchBrowserCombo = null!;
    private ComboBox _sdFlashAttnCombo = null!;
    private ComboBox _useMlockCombo = null!;
    private ComboBox _flashAttnCombo = null!;
    private ComboBox _contextShiftCombo = null!;
    private ComboBox _useMmqCombo = null!;
    private ComboBox _fastForwardingCombo = null!;
    private ComboBox _allowSwaCombo = null!;
    private ComboBox _tpsCombo = null!;
    private ComboBox _safetyMarginCombo = null!;
    private TextBox _sdLoraBox = null!;
    private TextBox _sdLoraMultBox = null!;
    private TextBox _sdTiledVaeBox = null!;
    private ComboBox _runtimeLoraCombo = null!;
    private ComboBox _conv2dCombo = null!;
    private TextBox _videoModelBox = null!;
    private TextBox _videoVaeBox = null!;
    private TextBox _videoT5Box = null!;
    private TextBox _audioModelBox = null!;
    private TextBox _voiceModelBox = null!;
    private TextBox _voiceTokenizerBox = null!;
    private TextBox _voiceTtsDirBox = null!;
    private TextBox _visionModelBox = null!;
    private TextBox _visionMmprojBox = null!;
    private ComboBox _visionMmprojCpuCombo = null!;
    private TextBox _textMmprojBox = null!;
    private TextBox _musicLlmBox = null!;
    private TextBox _musicDiffusionBox = null!;
    private TextBox _musicEmbeddingsBox = null!;
    private TextBox _musicVaeBox = null!;
    private ComboBox _musicVaeCpuCombo = null!;
    private TextBox _mcpFileBox = null!;
    private TextBox _chatTemplateBox = null!;
    private ComboBox _textMmprojCpuCombo = null!;
    private TextBox _textLoraBox = null!;
    private TextBox _textLoraMultBox = null!;
    private TextBox _textMoeExpertsBox = null!;
    private ComboBox _textMoeCpuModeCombo = null!;
    private TextBox _textMoeCpuLayersBox = null!;
    private TextBox _visionMoeExpertsBox = null!;
    private ComboBox _visionMoeCpuModeCombo = null!;
    private TextBox _visionMoeCpuLayersBox = null!;
    private TextBox _videoLoraBox = null!;
    private TextBox _videoLoraMultBox = null!;
    private TextBox _audioLoraBox = null!;
    private TextBox _audioLoraMultBox = null!;
    private TextBox _plannerTemplateBox = null!;
    private TextBox _outputDirBox = null!;
    private CheckBox _logToFileCheck = null!;
    private ComboBox _gpuCombo = null!;
    private ComboBox _backendCombo = null!;
    private Slider _widthSlider = null!;
    private Slider _heightSlider = null!;
    private Slider _stepsSlider = null!;
    private Slider _cfgSlider = null!;
    private ComboBox _textQuantKvCombo = null!;
    private TextBox _textRopeScaleBox = null!;
    private TextBox _textRopeBaseBox = null!;
    private ComboBox _visionQuantKvCombo = null!;
    private TextBox _visionRopeScaleBox = null!;
    private TextBox _visionRopeBaseBox = null!;
    private ComboBox _smartContextCombo = null!;
    private TextBox _overrideNativeContextBox = null!;
    private TextBox _tensorSplitBox = null!;
    private ComboBox _noAvx2Combo = null!;
    private ComboBox _failsafeCombo = null!;
    private ComboBox _debugModeCombo = null!;
    private TextBox _overrideTensorsBox = null!;
    private TextBox _overrideKvBox = null!;
    private TextBox _cacheSlotsBox = null!;
    private TextBox _defaultGenAmtBox = null!;
    private ComboBox _enableGuidanceCombo = null!;
    private ComboBox _thinkEffortCombo = null!;
    private TextBox _swaPaddingBox = null!;

    private TextBox _draftModelBox = null!;
    private TextBox _draftAmountBox = null!;
    private ComboBox _useMtpCombo = null!;
    private TextBox _draftGpuLayersBox = null!;
    private TextBox _embedsModelBox = null!;
    private TextBox _ectxBox = null!;
    private Slider _txtTempSlider = null!;
    private Slider _txtTopPSlider = null!;
    private Slider _txtTopKSlider = null!;
    private Slider _txtRepPenSlider = null!;
    private TextBox _txtTimeoutBox = null!;
    private Slider _maxIterSlider = null!;
    private Slider _stallNudgeSlider = null!;
    private Slider _stallLockoutSlider = null!;
    private Slider _readNudgeSlider = null!;
    private Slider _readHardStopSlider = null!;
    private ComboBox _embedsGpuCombo = null!;
    private ComboBox _autoFitCombo = null!;

    private static readonly Brush Bg = AppTheme.F(new SolidColorBrush(Color.FromRgb(28, 28, 32)));
    private static readonly Brush Fg = AppTheme.F(new SolidColorBrush(Color.FromRgb(225, 225, 230)));
    private static readonly Brush FgDim = AppTheme.F(new SolidColorBrush(Color.FromRgb(160, 160, 170)));
    private static readonly Brush InputBg = AppTheme.F(new SolidColorBrush(Color.FromRgb(22, 22, 26)));

    public SettingsWindow(AppSettings settings, Window owner, string configPath)
    {
        _settings = settings;
        _configPath = configPath;
        Owner = owner;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Title = "Settings";
        Width = 680;
        Height = 680;
        MinWidth = 520;
        MinHeight = 480;
        Background = Bg;
        Foreground = Fg;
        FontFamily = new FontFamily("Consolas");
        FontSize = 13;
        ResizeMode = ResizeMode.CanResizeWithGrip;
        WindowStyle = WindowStyle.SingleBorderWindow;

        Loaded += OnLoaded;
        ApplyDarkScrollbarResources();
        Content = BuildUI();
    }

    private void ApplyDarkScrollbarResources()
    {
        var trackBg = CardBg;
        var border = AppTheme.F(new SolidColorBrush(Color.FromRgb(35, 35, 42)));
        var thumbBg = ThumbBg;
        var thumbOver = ThumbHover;
        var thumbPressed = ThumbPressed;
        var btnBg = ButtonBg;

        var dict = this.Resources;
        dict["ScrollBar.Static.Background"] = trackBg;
        dict["ScrollBar.Static.Border"] = Brushes.Transparent;
        dict["ScrollBar.Thumb.Static.Background"] = thumbBg;
        dict["ScrollBar.Thumb.Static.Border"] = Brushes.Transparent;
        dict["ScrollBar.Thumb.MouseOver.Background"] = thumbOver;
        dict["ScrollBar.Thumb.Pressed.Background"] = thumbPressed;
        dict["ScrollBar.RepeatButton.Static.Background"] = btnBg;
        dict["ScrollBar.RepeatButton.Static.Border"] = Brushes.Transparent;

        try
        {
            var xaml = @"<Style xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'
       xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'
       TargetType='ScrollBar'>
  <Setter Property='OverridesDefaultStyle' Value='True'/>
  <Setter Property='Width' Value='10'/>
  <Setter Property='Template'>
    <Setter.Value>
      <ControlTemplate TargetType='ScrollBar'>
        <Border Background='#1A1A20' BorderThickness='0' SnapsToDevicePixels='True'>
          <Track Name='PART_Track' IsDirectionReversed='True'>
            <Track.DecreaseRepeatButton>
              <RepeatButton Command='{x:Static ScrollBar.PageUpCommand}'
                            Background='#1A1A20' BorderThickness='0'/>
            </Track.DecreaseRepeatButton>
            <Track.IncreaseRepeatButton>
              <RepeatButton Command='{x:Static ScrollBar.PageDownCommand}'
                            Background='#1A1A20' BorderThickness='0'/>
            </Track.IncreaseRepeatButton>
            <Track.Thumb>
              <Thumb Background='#373745' BorderThickness='0' MinWidth='6' MinHeight='20'
                     SnapsToDevicePixels='True'>
                <Thumb.Style>
                  <Style TargetType='Thumb'>
                    <Style.Triggers>
                      <Trigger Property='IsMouseOver' Value='True'>
                        <Setter Property='Background' Value='#4B4B5A'/>
                      </Trigger>
                      <Trigger Property='IsDragging' Value='True'>
                        <Setter Property='Background' Value='#5F5F73'/>
                      </Trigger>
                    </Style.Triggers>
                  </Style>
                </Thumb.Style>
              </Thumb>
            </Track.Thumb>
          </Track>
        </Border>
      </ControlTemplate>
    </Setter.Value>
  </Setter>
</Style>";
            var style = (Style)System.Windows.Markup.XamlReader.Parse(xaml);
            dict[typeof(System.Windows.Controls.Primitives.ScrollBar)] = style;
        }
        catch { }
    }

    private void OnLoaded(object _, RoutedEventArgs _2)
    {
        EnableDarkTitleBar();
        LoadIcon();
        PopulateFromSettings();
        DetectGpus();
        _gpuCombo.SelectedValue = _settings.GpuId;
        if (_gpuCombo.SelectedItem == null)
            _gpuCombo.SelectedIndex = 0;

        bool hasCuda = HasNvidiaGpu() || HasNvidiaSmi() ||
            File.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "nvcuda.dll"));
        if (!hasCuda)
        {
            _gpuCombo.IsEnabled = false;
            _gpuLayersBox.IsEnabled = false;
            _noKvOffloadCombo.IsEnabled = false;
            _flashAttnCombo.IsEnabled = false;
            _sdFlashAttnCombo.IsEnabled = false;
            _allowSwaCombo.IsEnabled = false;
            if (_settings.Backend == "cuda")
            {
                _settings.Backend = "auto";
                _backendCombo.Text = "Auto";
            }
        }
    }

    private void EnableDarkTitleBar()
    {
        if (Environment.OSVersion.Version.Major < 10) return;
        try
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero) return;
            int useDark = 1;
            DwmSetWindowAttribute(hwnd, 20, ref useDark, sizeof(int));
        }
        catch { }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    private FrameworkElement BuildUI()
    {
        var root = new Grid();

        root.RowDefinitions.Add(new RowDefinition());
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(60) });

        // Content
        var content = BuildContent();
        Grid.SetRow(content, 0);
        root.Children.Add(content);

        // Footer
        var footer = new Border
        {
            Background = SurfaceAlt,
            BorderBrush = BorderAlt,
            BorderThickness = new Thickness(0, 1, 0, 0)
        };
        var btnRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(20, 0, 20, 0)
        };
        var loadBtn = new Button
        {
            Content = "Load Config",
            Width = 110,
            Height = 34,
            FontSize = 13,
            Cursor = Cursors.Hand,
            Background = SurfaceAlt,
            Foreground = Fg,
            BorderBrush = BorderAlt
        };
        loadBtn.Click += (_, _) => { LoadConfigFromFile(); };

        var saveCfgBtn = new Button
        {
            Content = "Save Config",
            Width = 110,
            Height = 34,
            FontSize = 13,
            Cursor = Cursors.Hand,
            Background = SurfaceAlt,
            Foreground = Fg,
            BorderBrush = BorderAlt
        };
        saveCfgBtn.Click += (_, _) => { SaveConfigToFile(); };

        var cancelBtn = new Button
        {
            Content = "Cancel",
            Width = 100,
            Height = 34,
            FontSize = 13,
            Cursor = Cursors.Hand,
            Background = SurfaceAlt,
            Foreground = Fg,
            BorderBrush = BorderAlt
        };
        cancelBtn.Click += (_, _) => DialogResult = false;

        var saveBtn = new Button
        {
            Content = "Save & Close",
            Width = 140,
            Height = 34,
            FontSize = 13,
            FontWeight = FontWeight.FromOpenTypeWeight(600),
            Cursor = Cursors.Hand,
            Background = Accent,
            Foreground = Brushes.White,
            BorderBrush = Accent
        };
        saveBtn.Click += (_, _) => SaveAndClose();

        btnRow.Children.Add(loadBtn);
        btnRow.Children.Add(new Rectangle { Width = 8, Fill = Brushes.Transparent });
        btnRow.Children.Add(saveCfgBtn);
        btnRow.Children.Add(new Rectangle { Width = 16, Fill = Brushes.Transparent });
        btnRow.Children.Add(cancelBtn);
        btnRow.Children.Add(new Rectangle { Width = 8, Fill = Brushes.Transparent });
        btnRow.Children.Add(saveBtn);
        footer.Child = btnRow;
        Grid.SetRow(footer, 1);
        root.Children.Add(footer);

        return root;
    }

    private TabControl BuildContent()
    {
        var tc = new TabControl
        {
            Background = Bg,
            BorderThickness = new Thickness(0),
            FontSize = 13,
            Margin = new Thickness(20, 0, 20, 0)
        };

        ApplyTabStyle(tc);

        // General tab
        var genPanel = new StackPanel { Margin = new Thickness(0) };

        genPanel.Children.Add(SectionHeader("KoboldCpp"));
        _exePathBox = FileRow(genPanel, "Executable path:", "koboldcpp.exe",
            "Path to the koboldcpp executable file");
        _extraArgsBox = InputRow(genPanel, "Extra args (optional):", "--flashattention",
            tooltip: "Additional command-line arguments passed to koboldcpp on startup");
        _portBox = InputRow(genPanel, "Port:", "5001", small: true,
            tooltip: "Port to run the KoboldCpp server on (default: 5001)");

        genPanel.Children.Add(SectionHeader("Hardware"));
        _gpuCombo = ComboRow(genPanel, "GPU:", new[] { "Auto", "0", "1", "2", "3" },
            "GPU device ID to use (-1 for auto-detect, 0-3 for specific GPU)");
        _backendCombo = ComboRow(genPanel, "Backend:", new[] { "CUDA", "Vulkan", "CPU", "Auto" },
            "Compute backend: CUDA (Nvidia), Vulkan (AMD/Nvidia), CPU, or Auto-detect");
        _gpuLayersBox = InputRow(genPanel, "GPU layers:", "-1 (auto)", small: true,
            tooltip: "Number of layers to offload to GPU (-1 for auto). More layers = faster but uses more VRAM");
        _threadsBox = InputRow(genPanel, "CPU threads:", Environment.ProcessorCount.ToString(), small: true,
            tooltip: "Number of CPU threads for inference, typically set to number of physical CPU cores");
        _contextSizeBox = InputRow(genPanel, "Context size:", "4096", small: true,
            tooltip: "Maximum context size in tokens (e.g. 4096 = 4K). Higher values use more RAM");
        _safetyMarginCombo = ComboRow(genPanel, "Context Safety :", new[] { "none", "strict", "balance", "safe" },
            "Token safety margin: none=full context used, strict=trim aggressively, balance=moderate, safe=conservative");
        _batchSizeBox = InputRow(genPanel, "Batch size:", "512", small: true,
            tooltip: "Tokens processed per batch during prompt processing. Higher = faster but more memory");
        _blasBatchSizeBox = InputRow(genPanel, "BLAS Batch size:", "512", small: true,
            tooltip: "Batch size for BLAS matrix multiplication (-1 to disable). Controls prompt processing speed");

        genPanel.Children.Add(SectionHeader("Flags"));
        _noKvOffloadCombo = ComboRow(genPanel, "No KV offload:", new[] { "enable", "disable" },
            "Prevent KV cache offload to GPU. Enable this to save VRAM at the cost of speed (--nokvoffload)");
        _useMlockCombo = ComboRow(genPanel, "Lock model in RAM:", new[] { "enable", "disable" },
            "Force model to stay in RAM to prevent swapping (--usemlock). Uses more RAM but prevents slowdowns");
        _mmapCombo = ComboRow(genPanel, "MMAP:", new[] { "enable", "disable" },
            "Memory-map the model file for faster loading and reduced RAM usage (--usemmap)");
        _keepClipOnCpuCombo = ComboRow(genPanel, "Model Offload (CPU):", new[] { "enable", "disable" },
            "Keep model layers on CPU instead of offloading to GPU (--nooffload)");
        _sdClipOnCpuCombo = ComboRow(genPanel, "Clip on CPU:", new[] { "enable", "disable" },
            "Run CLIP text encoder on CPU for Stable Diffusion to save VRAM (--sdclipcpu)");
        _sdVaeOnCpuCombo = ComboRow(genPanel, "VAE on CPU:", new[] { "enable", "disable" },
            "Run VAE decoder on CPU for Stable Diffusion to save VRAM (--sdvaecpu)");
        _flashAttnCombo = ComboRow(genPanel, "Flash Attention:", new[] { "enable", "disable" },
            "Use flash attention algorithm for faster inference and lower memory usage (--flashattention)");
        _contextShiftCombo = ComboRow(genPanel, "Context Shift:", new[] { "enable", "disable" },
            "KV cache shifting to avoid reprocessing old tokens between consecutive generations (--contextshift)");
        _launchBrowserCombo = ComboRow(genPanel, "Launch Browser:", new[] { "enable", "disable" },
            "Automatically open the default browser when the server starts (--nobrowser to disable)");
        _useMmqCombo = ComboRow(genPanel, "Use MMQ:", new[] { "enable", "disable" },
            "Use quantized matrix multiplication in CUDA. Slightly less memory, may be faster for Q4_0 (--mmq / --nommq)");
        _fastForwardingCombo = ComboRow(genPanel, "Fast Forwarding:", new[] { "enable", "disable" },
            "Skip reused tokens in context that were already processed in the previous turn (--fastforward / --nofastforward)");
        _allowSwaCombo = ComboRow(genPanel, "Allow SWA:", new[] { "enable", "disable" },
            "Sliding Window Attention support for models that use it (--useswa)");
        _tpsCombo = ComboRow(genPanel, "Show Tokens/s:", new[] { "enable", "disable" },
            "Display tokens-per-second generation speed in the UI");

        genPanel.Children.Add(SectionHeader("Advanced"));
        _smartContextCombo = ComboRow(genPanel, "Smart Cache:", new[] { "enable", "disable" },
            "Reserve ~50% of context as a spare buffer to reduce prompt reprocessing between consecutive turns (--smartcontext)");
        _overrideNativeContextBox = InputRow(genPanel, "Override Native Ctx:", "0", small: true,
            tooltip: "Override the model's native/trusted context length (e.g., 8192). RoPE scaling is auto-adjusted based on this and --contextsize (--overridenativecontext). 0 = disabled");
        _tensorSplitBox = InputRow(genPanel, "Tensor Split:", "", small: true,
            tooltip: "Multi-GPU split ratios separated by spaces, e.g. '3 1' for 75%/25% split across 2 GPUs (--tensor_split). Only works with CUDA.");
        _noAvx2Combo = ComboRow(genPanel, "No AVX2:", new[] { "enable", "disable" },
            "Fallback mode for older CPUs without AVX2 support (--noavx2). Significantly slower, use only if crashing without it.");
        _failsafeCombo = ComboRow(genPanel, "Failsafe:", new[] { "enable", "disable" },
            "Ultra-compatibility mode combining --noavx2, --noblas, and --nommap for very old or problematic CPUs (--failsafe). Very slow.");
        _debugModeCombo = ComboRow(genPanel, "Debug Logging:", new[] { "0 (off)", "1 (basic)", "2 (verbose)" },
            "Debug logging verbosity level for koboldcpp (--debugmode). 0=off, 1=basic, 2=verbose.");
        _overrideTensorsBox = InputRow(genPanel, "Override Tensors:", "regex=CPU", small: true,
            tooltip: "Advanced: route specific tensors to CPU/GPU via regex, e.g. 'blk\\.[0-9]+\\.ffn.*=CPU' (--overridetensors)");
        _overrideKvBox = InputRow(genPanel, "Override KV:", "key=type:value", small: true,
            tooltip: "Advanced: override model metadata key-value pairs, e.g. 'tokenizer.ggml.add_bos_token=bool:false' (--overridekv). Multiple overrides separated by comma.");
        _cacheSlotsBox = InputRow(genPanel, "Cache Slots:", "5", small: true,
            tooltip: "Number of KV cache slots for SmartCache context switching. Saves KV cache snapshots to RAM for fast reprocessing between turns (--smartcache). 0 = disabled");
        _defaultGenAmtBox = InputRow(genPanel, "Default Gen Amt:", "1536", small: true,
            tooltip: "Default number of tokens to generate per request if the client does not specify max_length (--defaultgenamt). Must be smaller than context size.");
        _enableGuidanceCombo = ComboRow(genPanel, "Enable Guidance:", new[] { "enable", "disable" },
            "Enable Classifier-Free-Guidance (CFG) for negative prompts. Has performance and memory impact (--enableguidance).");
        _thinkEffortCombo = ComboRow(genPanel, "Think Effort:", new[] { "default", "high", "medium", "low", "minimal", "none" },
            "Default reasoning effort for thinking/CoT models. API per-request values override this (--reasoningeffort).");
        _swaPaddingBox = InputRow(genPanel, "SWA Padding Tokens:", "0", small: true,
            tooltip: "Extends the SWA (Sliding Window Attention) context by N tokens. Affects the rewind limit before reprocessing is forced (--swapadding). 0 = disabled");

        genPanel.Children.Add(SectionHeader("SD Options"));
        _sdTiledVaeBox = InputRow(genPanel, "VAE Tiling:", "640", small: true,
            tooltip: "Tile size for tiled VAE processing in Stable Diffusion. Lower = less VRAM usage (--sdtiledvae)");

        genPanel.Children.Add(SectionHeader("Image Defaults"));
        AddSliderRow(genPanel, "Width:", 64, 2048, 1024, 64, out _widthSlider, "Default image width for generation (64-2048 pixels)");
        AddSliderRow(genPanel, "Height:", 64, 2048, 1024, 64, out _heightSlider, "Default image height for generation (64-2048 pixels)");
        AddSliderRow(genPanel, "Steps:", 1, 100, 20, 1, out _stepsSlider, "Default number of sampling steps for image generation");
        AddSliderRow(genPanel, "CFG scale:", 1f, 30f, 7f, 0.5f, out _cfgSlider, "Default CFG scale for image generation (classifier-free guidance)");

        genPanel.Children.Add(SectionHeader("Output"));
        _logToFileCheck = CheckRow(genPanel, "Log to file:", "Mirror all log output to Documents\\PromptWhizz\\app.log (never truncated). Disabled by default to avoid unnecessary disk writes.",
            tooltip: $"Log file location: {System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "PromptWhizz", "app.log")}");
        _outputDirBox = FolderRow(genPanel, "Output folder:", "Default folder for saving generated files");

        genPanel.Children.Add(Separator());
        var updateBtn = new Button
        {
            Content = "Check for Updates",
            Height = 34, FontSize = 12, Cursor = Cursors.Hand,
            Background = SurfaceAlt, Foreground = Fg,
            BorderBrush = BorderAlt, BorderThickness = new Thickness(1),
            HorizontalAlignment = HorizontalAlignment.Center,
            Padding = new Thickness(20, 0, 20, 0),
            Margin = new Thickness(0, 4, 0, 4)
        };
        updateBtn.Click += (_, _) => UpdateRequested?.Invoke();
        genPanel.Children.Add(updateBtn);

        var genTab = new TabItem { Header = " General " };
        genTab.Content = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = genPanel,
            Padding = new Thickness(12, 0, 12, 12)
        };

        // Models tab
        var modPanel = new StackPanel { Margin = new Thickness(0) };

        modPanel.Children.Add(new Rectangle { Height = 4, Fill = Brushes.Transparent });

        _autoFitCombo = ComboRow(modPanel, "AutoFit:", new[] { "disable", "enable" },
            "Enable --autofit flag to remove all context limits except --contextsize (may cause crashes if out of memory)");
        modPanel.Children.Add(SectionHeader("Text"));
        _textModelBox = FileRow(modPanel, "Text Model:", "*.gguf", "Path to the GGUF text generation model");
        _textModelBox.TextChanged += (_, _) => UpdateMmprojEnabled(_textModelBox, _textMmprojBox);
        _textMmprojBox = FileRow(modPanel, "Text MMProj:", "*.gguf;*.bin", "Path to the multimodal projection model for vision/LLaVA capabilities");
        _textMmprojCpuCombo = ComboRow(modPanel, "MMProj CPU:", new[] { "enable", "disable" },
            "Run multimodal projection on CPU to save VRAM (--mmprojcpu)");
        _textLoraBox = FileRow(modPanel, "Text LoRa:", "*.safetensors;*.gguf", "Path to LoRA adapter file applied on top of the text model");
        _textLoraMultBox = InputRow(modPanel, "LoRa Mult:", "1.0", small: true,
            tooltip: "LoRA adapter multiplier/scale factor (--loramult)");
        _textMoeExpertsBox = InputRow(modPanel, "MoE Experts:", "-1", small: true,
            tooltip: "Overwrite the number of active experts used in MoE models (--moeexperts). Set to -1 (koboldcpp's own default) to use the model's built-in expert count instead of overriding it, e.g. 2 or 8.");
        _textMoeCpuModeCombo = ComboRow(modPanel, "MoE CPU Layers:", new[] { "Disabled", "All Layers", "Custom" },
            "Keep Mixture-of-Experts weights on CPU instead of GPU (--moecpu). 'All Layers' keeps every MoE layer on CPU; 'Custom' lets you set a specific layer count. Improves speed when partially offloading MoE models, since the heavily-used shared tensors stay on GPU while less-used MoE tensors stay in RAM. Not compatible with Autofit (-1 GPU layers), manual tensor overrides, or tensor splits.");
        _textMoeCpuLayersBox = InputRow(modPanel, "  Layer Count:", "999", small: true,
            tooltip: "Number of leading layers to keep on CPU when MoE CPU Layers is set to Custom, e.g. 999 with --gpulayers 999 keeps all shared layers on GPU while MoE-only tensors stay on CPU.");
        _chatTemplateBox = FileRow(modPanel, "Chat Template:", "*.jinja;*.txt;*.json",
            "Custom Jinja2 chat template file for instruct-tuned models (--chat-template)");
        _textQuantKvCombo = ComboRow(modPanel, "Quantized KV:", new[] { "f16", "bf16", "q8_0", "q5_1", "q4_0" },
            "KV cache quantization level for text model (--quantkv). f16=full precision, bf16=bfloat16, q8_0=8-bit, q5_1=5-bit, q4_0=4-bit. Lower = less VRAM but may reduce quality. Full quantization requires --flashattention.");
        _textRopeScaleBox = InputRow(modPanel, "RoPE Scale:", "1.0", small: true,
            tooltip: "RoPE frequency scale factor for context extension (first --ropeconfig param). 1.0=unscaled, 0.5=2x extension, 0.25=4x. Leave at 1.0 for automatic NTK-aware scaling.");
        _textRopeBaseBox = InputRow(modPanel, "RoPE Base:", "10000", small: true,
            tooltip: "RoPE frequency base value (second --ropeconfig param). Default 10000. Increase for NTK-aware scaling, e.g. 32000 ≈ 2x, 82000 ≈ 4x. Leave at 10000 for automatic scaling.");
        modPanel.Children.Add(SectionHeader("Image"));
        _modelPathBox = FileRow(modPanel, "SD Model:", "*.gguf", "Path to Stable Diffusion model (GGUF or safetensors)");
        _clipLBox = FileRow(modPanel, "CLIP-L:", "*.safetensors", "Path to CLIP-L text encoder for Stable Diffusion (--clipmodel)");
        _t5Box = FileRow(modPanel, "T5 XXL:", "*.gguf", "Path to T5-XXL encoder for SD3/Flux models");
        _vaeBox = FileRow(modPanel, "VAE:", "*.safetensors", "Path to external VAE model for Stable Diffusion");
        _imageVaeCombo = ComboRow(modPanel, "Use External VAE:", new[] { "enable", "disable" },
            "Enable external VAE model instead of the built-in one");
        _sdLoraBox = FileRow(modPanel, "Image LoRa:", "*.safetensors;*.gguf", "Path to LoRA adapter for image generation (--sdlora)");
        _sdLoraMultBox = InputRow(modPanel, "Multiplier:", "1.0", small: true,
            tooltip: "LoRA multiplier/scale factor for image generation (--sdlporascale)");
        _runtimeLoraCombo = ComboRow(modPanel, "Runtime LoRa:", new[] { "Disabled", "File", "Directory" },
            "Runtime LoRA loading mode: Disabled=off, File=single file, Directory=watch folder");
        _sdFlashAttnCombo = ComboRow(modPanel, "SD Flash Attention:", new[] { "enable", "disable" },
            "Enable flash attention for SD models to reduce VRAM usage and speed up generation (--sdflashattention)");
        _conv2dCombo = ComboRow(modPanel, "Conv2D:", new[] { "off", "vaeonly", "full" },
            "Convolution mode for SD: off=no conv2d, vaeonly=VAE only, full=all conv2d layers (--conv2d)");

        modPanel.Children.Add(SectionHeader("Video"));
        _videoModelBox = FileRow(modPanel, "Video Model:", "*.safetensors;*.gguf", "Path to video generation model");
        _videoVaeBox = FileRow(modPanel, "Video VAE:", "*.safetensors", "Path to VAE for video generation model");
        _videoT5Box = FileRow(modPanel, "Video T5:", "*.gguf", "Path to T5 encoder for video generation");
        _videoLoraBox = FileRow(modPanel, "Video LoRa:", "*.safetensors;*.gguf", "Path to LoRA adapter for video generation");
        _videoLoraMultBox = InputRow(modPanel, "LoRa Mult:", "1.0", small: true,
            tooltip: "LoRA multiplier for video generation models");
        _videoVaeCombo = ComboRow(modPanel, "Use External VAE:", new[] { "enable", "disable" },
            "Enable external VAE for video generation");

        modPanel.Children.Add(SectionHeader("Audio"));
        _audioModelBox = FileRow(modPanel, "Audio Model:", "*.gguf;*.bin", "Path to audio/speech model (Whisper GGUF for speech-to-text)");
        _audioLoraBox = FileRow(modPanel, "Audio LoRa:", "*.safetensors;*.gguf", "Path to LoRA adapter for audio model");
        _audioLoraMultBox = InputRow(modPanel, "LoRa Mult:", "1.0", small: true,
            tooltip: "LoRA multiplier for audio models");

        modPanel.Children.Add(SectionHeader("Voice"));
        _voiceModelBox = FileRow(modPanel, "Voice Model:", "*.gguf;*.bin", "Path to voice/TTS (text-to-speech) model");
        _voiceTokenizerBox = FileRow(modPanel, "Voice Tokenizer:", "*.gguf;*.bin;*.json", "Path to voice tokenizer model file");
        _voiceTtsDirBox = FolderRow(modPanel, "Voice TTS Dir:", "Output directory for generated TTS audio files");

        modPanel.Children.Add(SectionHeader("Music"));
        _musicLlmBox = FileRow(modPanel, "Music LLM:", "*.gguf", "Path to music language model for text-to-music generation");
        _musicDiffusionBox = FileRow(modPanel, "Music Diffuser:", "*.gguf", "Path to music diffusion model for audio generation");
        _musicEmbeddingsBox = FileRow(modPanel, "Music Embeddings:", "*.gguf", "Path to music embeddings/CLAP model");
        _musicVaeBox = FileRow(modPanel, "Music VAE:", "*.gguf", "Path to music VAE decoder model");
        _musicVaeCpuCombo = ComboRow(modPanel, "VAE on CPU:", new[] { "enable", "disable" },
            "Run music VAE on CPU to save VRAM (--musiclowvram)");

        modPanel.Children.Add(SectionHeader("Vision"));
        _visionModelBox = FileRow(modPanel, "Vision Model:", "*.gguf;*.bin", "Path to vision model for image captioning/VQA");
        _visionModelBox.TextChanged += (_, _) => UpdateMmprojEnabled(_visionModelBox, _visionMmprojBox);
        _visionMmprojBox = FileRow(modPanel, "MMProj:", "*.gguf;*.bin", "Path to multimodal projection model for vision tasks");
        _visionMmprojCpuCombo = ComboRow(modPanel, "MMProj CPU:", new[] { "enable", "disable" },
            "Run vision MMProj on CPU to save VRAM (--mmprojcpu)");
        _visionMoeExpertsBox = InputRow(modPanel, "MoE Experts:", "-1", small: true,
            tooltip: "Overwrite the number of active experts used in MoE models (--moeexperts). Set to -1 (koboldcpp's own default) to use the model's built-in expert count instead of overriding it, e.g. 2 or 8.");
        _visionMoeCpuModeCombo = ComboRow(modPanel, "MoE CPU Layers:", new[] { "Disabled", "All Layers", "Custom" },
            "Keep Mixture-of-Experts weights on CPU instead of GPU (--moecpu). 'All Layers' keeps every MoE layer on CPU; 'Custom' lets you set a specific layer count. Not compatible with Autofit (-1 GPU layers), manual tensor overrides, or tensor splits.");
        _visionMoeCpuLayersBox = InputRow(modPanel, "  Layer Count:", "999", small: true,
            tooltip: "Number of leading layers to keep on CPU when MoE CPU Layers is set to Custom.");
        _visionQuantKvCombo = ComboRow(modPanel, "Quantized KV:", new[] { "f16", "bf16", "q8_0", "q5_1", "q4_0" },
            "KV cache quantization level for vision model (--quantkv). f16=full precision, bf16=bfloat16, q8_0=8-bit, q5_1=5-bit, q4_0=4-bit. Lower = less VRAM but may reduce quality. Full quantization requires --flashattention.");
        _visionRopeScaleBox = InputRow(modPanel, "RoPE Scale:", "1.0", small: true,
            tooltip: "RoPE frequency scale factor for context extension (first --ropeconfig param). 1.0=unscaled, 0.5=2x extension, 0.25=4x. Leave at 1.0 for automatic NTK-aware scaling.");
        _visionRopeBaseBox = InputRow(modPanel, "RoPE Base:", "10000", small: true,
            tooltip: "RoPE frequency base value (second --ropeconfig param). Default 10000. Increase for NTK-aware scaling, e.g. 32000 ≈ 2x, 82000 ≈ 4x. Leave at 10000 for automatic scaling.");

        modPanel.Children.Add(SectionHeader("Server Features"));
        _mcpFileBox = FileRow(modPanel, "MCP Config:", "*.json", "MCP (Model Context Protocol) configuration JSON file for external tools");

        modPanel.Children.Add(SectionHeader("Embeddings"));
        var embedsRow = new Grid { Margin = new Thickness(0, 2, 0, 2) };
        embedsRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
        embedsRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        embedsRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(88) });
        embedsRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(10) });
        embedsRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(38) });
        embedsRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(80) });
        embedsRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
        embedsRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(35) });
        embedsRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var emLabel = new TextBlock { Text = "Embeds Model:", FontSize = 13, Foreground = Fg, VerticalAlignment = VerticalAlignment.Center };
        embedsRow.Children.Add(emLabel);
        _embedsModelBox = new TextBox
        {
            Height = 32, FontSize = 13, Background = InputBg, Foreground = Fg,
            BorderBrush = BorderAlt, CaretBrush = Fg, Padding = new Thickness(10, 0, 10, 0),
            VerticalContentAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(_embedsModelBox, 1);
        embedsRow.Children.Add(_embedsModelBox);
        var embedsBrowse = new Button
        {
            Content = "Browse", Width = 80, Height = 32, FontSize = 12, Cursor = Cursors.Hand,
            Background = SurfaceAlt, Foreground = Fg, BorderBrush = BorderAlt
        };
        embedsBrowse.Click += (_, _) =>
        {
            var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "GGUF models (*.gguf)|*.gguf|All files (*.*)|*.*" };
            if (dlg.ShowDialog(this) == true) _embedsModelBox.Text = dlg.FileName;
        };
        Grid.SetColumn(embedsBrowse, 2);
        embedsRow.Children.Add(embedsBrowse);
        var ectxLabel = new TextBlock { Text = "ECtx:", FontSize = 13, Foreground = Fg, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(ectxLabel, 4);
        embedsRow.Children.Add(ectxLabel);
        _ectxBox = new TextBox
        {
            Height = 30, FontSize = 13, Background = InputBg, Foreground = Fg,
            BorderBrush = BorderAlt, CaretBrush = Fg, Padding = new Thickness(10, 0, 10, 0),
            VerticalContentAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(_ectxBox, 5);
        embedsRow.Children.Add(_ectxBox);
        var gpuLabel = new TextBlock { Text = "GPU:", FontSize = 13, Foreground = Fg, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(gpuLabel, 7);
        embedsRow.Children.Add(gpuLabel);
        _embedsGpuCombo = new ComboBox
        {
            Template = DarkComboTemplate, Height = 30, FontSize = 13, Background = InputBg, Foreground = Fg,
            BorderBrush = BorderAlt, ItemsSource = new[] { "enable", "disable" }, IsEditable = false,
            HorizontalContentAlignment = HorizontalAlignment.Left, VerticalContentAlignment = VerticalAlignment.Center,
            Padding = new Thickness(8, 0, 8, 0), BorderThickness = new Thickness(1),
        };
        ApplyComboItemStyle(_embedsGpuCombo);
        Grid.SetColumn(_embedsGpuCombo, 8);
        embedsRow.Children.Add(_embedsGpuCombo);
        modPanel.Children.Add(embedsRow);

        // ── RAG section ──
        modPanel.Children.Add(SectionHeader("RAG (Retrieval-Augmented Generation)"));
        modPanel.Children.Add(new TextBlock
        {
            Text = "Requires an embeddings GGUF model loaded above. Indexes project files and injects relevant context.",
            FontSize = 12, Foreground = FgDim, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 6)
        });


        var modTab = new TabItem { Header = " Models " };
        modTab.Content = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = modPanel,
            Padding = new Thickness(12, 0, 12, 12)
        };

        tc.Items.Add(genTab);

        // Text tab
        var textPanel = new StackPanel { Margin = new Thickness(0) };
        textPanel.Children.Add(new Rectangle { Height = 4, Fill = Brushes.Transparent });

        textPanel.Children.Add(SectionHeader("Sampling"));

        Slider tempSlider, topPSlider, topKSlider, repPenSlider;
        AddSliderRow(textPanel, "Temperature:", 0f, 2f, _settings.TextTemperature, 0.05f, out tempSlider,
            "Sampling temperature. Higher = more random, Lower = more deterministic");
        AddSliderRow(textPanel, "Top P:", 0f, 1f, _settings.TextTopP, 0.05f, out topPSlider,
            "Nucleus sampling threshold. 1.0 = disabled, lower = more focused");
        AddSliderRow(textPanel, "Top K:", 0f, 200f, _settings.TextTopK, 1f, out topKSlider,
            "Top-K sampling. Higher = more diverse, lower = more focused. 0 = disabled");
        AddSliderRow(textPanel, "Repeat Penalty:", 1f, 2f, _settings.TextRepeatPenalty, 0.01f, out repPenSlider,
            "Repetition penalty. 1.0 = disabled, higher = less repetition");
        _txtTempSlider = tempSlider;
        _txtTopPSlider = topPSlider;
        _txtTopKSlider = topKSlider;
        _txtRepPenSlider = repPenSlider;

        _txtTimeoutBox = InputRow(textPanel, "Timeout (seconds):", "0 (no timeout)", small: true,
            tooltip: "HTTP request timeout in seconds. 0 = no timeout (waits indefinitely).");

        textPanel.Children.Add(Separator());
        textPanel.Children.Add(SectionHeader("Guardrails"));

        Slider maxIterSlider, stallNudgeSlider, stallLockoutSlider, readNudgeSlider, readHardStopSlider;
        AddSliderRow(textPanel, "Max Iterations:", 1, 200, _settings.MaxIterations, 1, out maxIterSlider,
            "Maximum number of agent iterations before forced stop. Higher = more work possible but more token cost.");
        AddSliderRow(textPanel, "Stall Nudge After:", 1, 50, _settings.StallNudgeThreshold, 1, out stallNudgeSlider,
            "Number of non-write tool calls before the agent is nudged to write changes.");
        AddSliderRow(textPanel, "Stall Lockout After:", 1, 50, _settings.StallLockoutThreshold, 1, out stallLockoutSlider,
            "Number of non-write tool calls before all exploration is blocked. Must be >= Stall Nudge.");
        AddSliderRow(textPanel, "Read Nudge After:", 1, 50, _settings.ReadFileNudgeThreshold, 1, out readNudgeSlider,
            "Number of read_file calls without a write before the agent is nudged to stop reading.");
        AddSliderRow(textPanel, "Read Hard Stop After:", 1, 50, _settings.ReadFileHardStopThreshold, 1, out readHardStopSlider,
            "Number of read_file calls without a write before reads are blocked entirely. Must be >= Read Nudge.");
        _maxIterSlider = maxIterSlider;
        _stallNudgeSlider = stallNudgeSlider;
        _stallLockoutSlider = stallLockoutSlider;
        _readNudgeSlider = readNudgeSlider;
        _readHardStopSlider = readHardStopSlider;

        textPanel.Children.Add(Separator());
        textPanel.Children.Add(SectionHeader("Drafter"));
        _draftModelBox = FileRow(textPanel, "Draft Model:", "*.gguf",
            "Smaller fast model with same vocab as the main model for speculative decoding (--draftmodel)");

        var draftRow = new Grid { Margin = new Thickness(0, 2, 0, 2) };
        draftRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(140) });
        draftRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(100) });
        draftRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(10) });
        draftRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(125) });
        draftRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(100) });
        draftRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var daLabel = new TextBlock { Text = "Draft Amount:", FontSize = 13, Foreground = Fg, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(daLabel, 0);
        draftRow.Children.Add(daLabel);
        _draftAmountBox = new TextBox
        {
            Height = 30, FontSize = 13, Background = InputBg, Foreground = Fg,
            BorderBrush = BorderAlt, CaretBrush = Fg, Padding = new Thickness(10, 0, 10, 0),
            VerticalContentAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(_draftAmountBox, 1);
        draftRow.Children.Add(_draftAmountBox);

        var dglLabel = new TextBlock { Text = "Draft GPU Layers:", FontSize = 13, Foreground = Fg, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(dglLabel, 3);
        draftRow.Children.Add(dglLabel);
        _draftGpuLayersBox = new TextBox
        {
            Height = 30, FontSize = 13, Background = InputBg, Foreground = Fg,
            BorderBrush = BorderAlt, CaretBrush = Fg, Padding = new Thickness(10, 0, 10, 0),
            VerticalContentAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(_draftGpuLayersBox, 4);
        draftRow.Children.Add(_draftGpuLayersBox);

        textPanel.Children.Add(draftRow);

        _useMtpCombo = ComboRow(textPanel, "Use MTP:", new[] { "enable", "disable" },
            "Multi-Token Prediction layers for drafting if the model supports them (--usemtp)");

        textPanel.Children.Add(Separator());
        _plannerTemplateBox = FileRow(textPanel, "Planner Template:", "*.md", "Path to a .md file containing the skeleton/template for the planner output. The planner model uses this structure to format its analysis. If empty, a built-in default template is used.");

        var textTab = new TabItem { Header = " Text " };
        textTab.Content = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = textPanel,
            Padding = new Thickness(12, 0, 12, 12)
        };
        tc.Items.Add(textTab);
        tc.Items.Add(modTab);
        return tc;
    }

    private void ApplyTabStyle(TabControl tc)
    {
        try
        {
            var style = (Style)System.Windows.Markup.XamlReader.Parse(@"
<Style xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' TargetType='TabItem'>
    <Setter Property='Background' Value='#26262C'/>
    <Setter Property='Foreground' Value='#9696A2'/>
    <Setter Property='FontSize' Value='13'/>
    <Setter Property='Padding' Value='12,6,12,6'/>
    <Setter Property='BorderThickness' Value='0'/>
    <Setter Property='Template'>
        <Setter.Value>
            <ControlTemplate TargetType='TabItem'>
                <Border Name='Bd' Background='{TemplateBinding Background}' BorderThickness='0'
                        Padding='{TemplateBinding Padding}' SnapsToDevicePixels='True'>
                    <ContentPresenter ContentSource='Header' HorizontalAlignment='Center'
                                      VerticalAlignment='Center' RecognizesAccessKey='True'
                                      TextElement.FontWeight='Normal'/>
                </Border>
                <ControlTemplate.Triggers>
                    <Trigger Property='IsSelected' Value='True'>
                        <Setter TargetName='Bd' Property='Background' Value='#648CFF'/>
                        <Setter Property='Foreground' Value='White'/>
                        <Setter Property='TextElement.FontWeight' Value='600'/>
                    </Trigger>
                    <Trigger Property='IsMouseOver' Value='True'>
                        <Setter TargetName='Bd' Property='Background' Value='#3A3A44'/>
                    </Trigger>
                </ControlTemplate.Triggers>
            </ControlTemplate>
        </Setter.Value>
    </Setter>
</Style>");
            tc.Resources[typeof(TabItem)] = style;
        }
        catch { }
    }

    private static TextBlock SectionHeader(string text)
    {
        return new TextBlock
        {
            Text = text,
            FontSize = 14,
            FontWeight = FontWeight.FromOpenTypeWeight(600),
            Foreground = Accent,
            Margin = new Thickness(0, 16, 0, 6)
        };
    }

    private static UIElement Separator()
    {
        return new Rectangle { Height = 1, Fill = BorderAlt, Margin = new Thickness(0, 12, 0, 8) };
    }

    private void ApplyComboItemStyle(ComboBox combo)
    {
        var itemStyle = new Style(typeof(ComboBoxItem));
        itemStyle.Setters.Add(new Setter(Control.BackgroundProperty, Surface));
        itemStyle.Setters.Add(new Setter(Control.ForegroundProperty, Fg));
        itemStyle.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(0)));
        itemStyle.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(8, 4, 8, 4)));
        var hover = new Trigger { Property = ComboBoxItem.IsMouseOverProperty, Value = true };
        hover.Setters.Add(new Setter(Control.BackgroundProperty, Highlight));
        hover.Setters.Add(new Setter(Control.ForegroundProperty, Brushes.White));
        itemStyle.Triggers.Add(hover);
        var sel = new Trigger { Property = ComboBoxItem.IsSelectedProperty, Value = true };
        sel.Setters.Add(new Setter(Control.BackgroundProperty, Highlight));
        sel.Setters.Add(new Setter(Control.ForegroundProperty, Brushes.White));
        itemStyle.Triggers.Add(sel);
        combo.ItemContainerStyle = itemStyle;
    }

    private TextBox FileRow(StackPanel parent, string label, string filter, string? tooltip = null)
    {
        var box = new TextBox
        {
            Height = 32,
            FontSize = 13,
            Background = InputBg,
            Foreground = Fg,
            BorderBrush = BorderAlt,
            CaretBrush = Fg,
            Padding = new Thickness(10, 0, 10, 0),
            VerticalContentAlignment = VerticalAlignment.Center
        };

        var browse = new Button
        {
            Content = "Browse",
            Width = 80,
            Height = 32,
            FontSize = 12,
            Cursor = Cursors.Hand,
            Background = SurfaceAlt,
            Foreground = Fg,
            BorderBrush = BorderAlt
        };
        browse.Click += (_, _) =>
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Filter = BuildBrowseFilter(filter)
            };
            if (dlg.ShowDialog(this) == true)
                box.Text = dlg.FileName;
        };

        var grid = new Grid { Margin = new Thickness(0, 2, 0, 2) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(88) });

        var lbl = new TextBlock { Text = label, FontSize = 13, Foreground = Fg, VerticalAlignment = VerticalAlignment.Center };
        if (tooltip != null) ToolTipService.SetToolTip(lbl, tooltip);
        grid.Children.Add(lbl);
        Grid.SetColumn(box, 1);
        grid.Children.Add(box);
        Grid.SetColumn(browse, 2);
        grid.Children.Add(browse);

        parent.Children.Add(grid);
        return box;
    }

    private static string BuildBrowseFilter(string filter)
    {
        if (filter.Contains("koboldcpp", StringComparison.OrdinalIgnoreCase))
            return "Executable (*.exe)|*.exe|All files (*.*)|*.*";
        var parts = filter.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length > 1)
        {
            var labels = string.Join("; ", parts.Select(p => p.TrimStart('*')));
            return $"{labels} ({filter})|{filter}|All files (*.*)|*.*";
        }
        var ext = parts.Length == 1 ? parts[0].TrimStart('*') : ".gguf";
        return $"{ext} models ({filter})|{filter}|All files (*.*)|*.*";
    }

    private TextBox FolderRow(StackPanel parent, string label, string? tooltip = null)
    {
        var box = MakeInput();

        var browse = new Button
        {
            Content = "Browse",
            Width = 80,
            Height = 32,
            FontSize = 12,
            Cursor = Cursors.Hand,
            Background = SurfaceAlt,
            Foreground = Fg,
            BorderBrush = BorderAlt
        };
        browse.Click += (_, _) =>
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                ValidateNames = false,
                CheckFileExists = false,
                CheckPathExists = true,
                FileName = "Select Folder",
                Title = "Select a folder"
            };
            if (dlg.ShowDialog(this) == true)
            {
                var folder = Path.GetDirectoryName(dlg.FileName);
                if (!string.IsNullOrEmpty(folder))
                    box.Text = folder;
            }
        };

        var grid = new Grid { Margin = new Thickness(0, 2, 0, 2) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(88) });

        grid.Children.Add(new TextBlock { Text = label, FontSize = 13, Foreground = Fg, VerticalAlignment = VerticalAlignment.Center, ToolTip = tooltip });
        Grid.SetColumn(box, 1);
        grid.Children.Add(box);
        Grid.SetColumn(browse, 2);
        grid.Children.Add(browse);

        parent.Children.Add(grid);
        return box;
    }

    private TextBox InputRow(StackPanel parent, string label, string placeholder, bool small = false, string? tooltip = null)
    {
        var box = new TextBox
        {
            Height = small ? 30 : 32,
            FontSize = 13,
            Background = InputBg,
            Foreground = Fg,
            BorderBrush = BorderAlt,
            CaretBrush = Fg,
            Padding = new Thickness(10, 0, 10, 0),
            VerticalContentAlignment = VerticalAlignment.Center
        };

        var grid = new Grid { Margin = new Thickness(0, 2, 0, 2) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        grid.Children.Add(new TextBlock { Text = label, FontSize = 13, Foreground = Fg, VerticalAlignment = VerticalAlignment.Center, ToolTip = tooltip });
        Grid.SetColumn(box, 1);
        grid.Children.Add(box);

        if (!string.IsNullOrEmpty(placeholder))
        {
            // Simple watermark: an overlay TextBlock drawn on top of the (otherwise
            // opaque-background) TextBox, hidden as soon as there's real text, with
            // hit-testing disabled so clicks/typing pass straight through to the box.
            var hint = new TextBlock
            {
                Text = placeholder,
                FontSize = 13,
                Foreground = FgDim,
                Margin = new Thickness(11, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                IsHitTestVisible = false,
                Visibility = string.IsNullOrEmpty(box.Text) ? Visibility.Visible : Visibility.Collapsed
            };
            box.TextChanged += (_, _) => hint.Visibility = string.IsNullOrEmpty(box.Text) ? Visibility.Visible : Visibility.Collapsed;
            Grid.SetColumn(hint, 1);
            grid.Children.Add(hint);
        }

        parent.Children.Add(grid);
        return box;
    }

    internal static readonly ControlTemplate DarkComboTemplate = (ControlTemplate)XamlReader.Parse(@"
<ControlTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' TargetType='ComboBox'>
    <Grid>
        <Border Background='{TemplateBinding Background}' BorderBrush='{TemplateBinding BorderBrush}' BorderThickness='{TemplateBinding BorderThickness}'>
            <Grid>
                <Grid.ColumnDefinitions>
                    <ColumnDefinition Width='*'/>
                    <ColumnDefinition Width='Auto'/>
                </Grid.ColumnDefinitions>
                <ToggleButton Grid.ColumnSpan='2' IsChecked='{Binding IsDropDownOpen, Mode=TwoWay, RelativeSource={RelativeSource TemplatedParent}}' ClickMode='Press' Background='Transparent' BorderThickness='0' Cursor='Hand'>
                    <ToggleButton.Template>
                        <ControlTemplate TargetType='ToggleButton'>
                            <Border Background='Transparent'/>
                        </ControlTemplate>
                    </ToggleButton.Template>
                </ToggleButton>
                <ContentPresenter Grid.Column='0' Content='{TemplateBinding SelectionBoxItem}' ContentTemplate='{TemplateBinding SelectionBoxItemTemplate}' Margin='{TemplateBinding Padding}' VerticalAlignment='Center' HorizontalAlignment='{TemplateBinding HorizontalContentAlignment}' IsHitTestVisible='False'/>
                <TextBlock Grid.Column='1' Text=' &#x25BC;' FontSize='7' Foreground='{TemplateBinding Foreground}' VerticalAlignment='Center' HorizontalAlignment='Center' IsHitTestVisible='False' Width='18'/>
            </Grid>
        </Border>
        <Popup IsOpen='{Binding IsDropDownOpen, RelativeSource={RelativeSource TemplatedParent}}' Placement='Bottom' StaysOpen='False' AllowsTransparency='True' Focusable='False'>
            <Border Background='#1E1E24' BorderBrush='#3C3C46' BorderThickness='1' MaxHeight='{TemplateBinding MaxDropDownHeight}'>
                <ScrollViewer><ItemsPresenter/></ScrollViewer>
            </Border>
        </Popup>
    </Grid>
</ControlTemplate>");

    private ComboBox ComboRow(StackPanel parent, string label, string[] items, string? tooltip = null)
    {
        var combo = new ComboBox
        {
            Template = DarkComboTemplate,
            Height = 30,
            FontSize = 13,
            Background = InputBg,
            Foreground = Fg,
            BorderBrush = BorderAlt,
            ItemsSource = items,
            IsEditable = false,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            VerticalContentAlignment = VerticalAlignment.Center,
            Padding = new Thickness(8, 0, 8, 0),
            BorderThickness = new Thickness(1),
        };

        var itemStyle = new Style(typeof(ComboBoxItem));
        itemStyle.Setters.Add(new Setter(Control.BackgroundProperty, Surface));
        itemStyle.Setters.Add(new Setter(Control.ForegroundProperty, Fg));
        itemStyle.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(0)));
        itemStyle.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(8, 4, 8, 4)));
        var hover = new Trigger { Property = ComboBoxItem.IsMouseOverProperty, Value = true };
        hover.Setters.Add(new Setter(Control.BackgroundProperty, Highlight));
        hover.Setters.Add(new Setter(Control.ForegroundProperty, Brushes.White));
        itemStyle.Triggers.Add(hover);
        var sel = new Trigger { Property = ComboBoxItem.IsSelectedProperty, Value = true };
        sel.Setters.Add(new Setter(Control.BackgroundProperty, Highlight));
        sel.Setters.Add(new Setter(Control.ForegroundProperty, Brushes.White));
        itemStyle.Triggers.Add(sel);
        combo.ItemContainerStyle = itemStyle;

        var grid = new Grid { Margin = new Thickness(0, 2, 0, 2) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        grid.Children.Add(new TextBlock { Text = label, FontSize = 13, Foreground = Fg, VerticalAlignment = VerticalAlignment.Center, ToolTip = tooltip });
        Grid.SetColumn(combo, 1);
        grid.Children.Add(combo);

        parent.Children.Add(grid);
        return combo;
    }

    private CheckBox CheckRow(StackPanel parent, string label, string hint, string? tooltip = null)
    {
        var cb = new CheckBox
        {
            Foreground = Fg,
            FontSize = 13,
            ToolTip = hint
        };

        var grid = new Grid { Margin = new Thickness(0, 2, 0, 2) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        grid.Children.Add(new TextBlock { Text = label, FontSize = 13, Foreground = Fg, VerticalAlignment = VerticalAlignment.Center, ToolTip = tooltip });
        Grid.SetColumn(cb, 1);
        grid.Children.Add(cb);

        parent.Children.Add(grid);
        return cb;
    }

    private void AddSliderRow(StackPanel parent, string label, double min, double max, double val, double tick, out Slider slider, string? tooltip = null)
    {
        var valLbl = new Label
        {
            Content = val.ToString(tick >= 1 ? "F0" : "F1"),
            Foreground = FgDim,
            FontSize = 12,
            Padding = new Thickness(0),
            VerticalContentAlignment = VerticalAlignment.Center,
            MinWidth = 36,
            HorizontalContentAlignment = HorizontalAlignment.Right
        };

        slider = new Slider
        {
            Minimum = min,
            Maximum = max,
            Value = val,
            TickFrequency = tick,
            IsSnapToTickEnabled = tick > 0,
            Height = 24,
            Foreground = Accent,
            Background = BorderAlt,
            BorderBrush = BorderAlt,
            Cursor = Cursors.Hand,
            Style = MainWindow.SliderStyle
        };

        var captured = valLbl;
        var capturedTick = tick;
        slider.ValueChanged += (_, e) =>
        {
            captured.Content = capturedTick >= 1 ? e.NewValue.ToString("F0") : e.NewValue.ToString("F1");
        };

        var grid = new Grid { Margin = new Thickness(0, 2, 0, 2), Height = 34 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        grid.Children.Add(new TextBlock { Text = label, FontSize = 13, Foreground = Fg, VerticalAlignment = VerticalAlignment.Center, ToolTip = tooltip });
        Grid.SetColumn(slider, 1);
        grid.Children.Add(slider);
        Grid.SetColumn(valLbl, 2);
        grid.Children.Add(valLbl);

        parent.Children.Add(grid);
    }

    private static TextBox MakeInput()
    {
        return new TextBox
        {
            Height = 32,
            FontSize = 13,
            Background = InputBg,
            Foreground = Fg,
            BorderBrush = BorderAlt,
            CaretBrush = Fg,
            Padding = new Thickness(10, 0, 10, 0),
            VerticalContentAlignment = VerticalAlignment.Center
        };
    }

    private void DetectGpus()
    {
        var gpus = new List<string> { "Auto" };
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT * FROM Win32_VideoController");
            int idx = 0;
            foreach (var obj in searcher.Get())
            {
                var name = obj["Name"]?.ToString() ?? $"GPU {idx}";
                gpus.Add($"{idx}: {name}");
                idx++;
            }
        }
        catch { }
        _gpuCombo.ItemsSource = gpus;
    }

    private static string MoeCpuModeToDisplay(string mode) => mode switch
    {
        "all" => "All Layers",
        "custom" => "Custom",
        _ => "Disabled"
    };

    private static string MoeCpuModeFromDisplay(string display) => display switch
    {
        "All Layers" => "all",
        "Custom" => "custom",
        _ => "disabled"
    };

    private void PopulateFromSettings()
    {
        _exePathBox.Text = _settings.KoboldExePath;
        _modelPathBox.Text = _settings.ModelPath;
        _textModelBox.Text = _settings.TextModelPath;
        UpdateMmprojEnabled(_textModelBox, _textMmprojBox);
        _textMmprojBox.Text = _settings.TextMmprojPath;
        _textMmprojCpuCombo.Text = _settings.TextMmprojCpu ? "enable" : "disable";
        _clipLBox.Text = _settings.ClipLPath;
        _t5Box.Text = _settings.TextEncoderPath;
        _vaeBox.Text = _settings.ImageVaePath;
        _imageVaeCombo.Text = _settings.ImageUseExternalVae ? "enable" : "disable";
        _videoVaeCombo.Text = _settings.VideoUseExternalVae ? "enable" : "disable";
        _videoModelBox.Text = _settings.VideoModelPath;
        _videoVaeBox.Text = _settings.VideoVaePath;
        _videoT5Box.Text = _settings.VideoT5Path;
        _audioModelBox.Text = _settings.AudioModelPath;
        _voiceModelBox.Text = _settings.VoiceModelPath;
        _voiceTokenizerBox.Text = _settings.VoiceTokenizerPath;
        _voiceTtsDirBox.Text = _settings.VoiceTtsDir;
        _musicLlmBox.Text = _settings.MusicLlmPath;
        _musicDiffusionBox.Text = _settings.MusicDiffusionPath;
        _musicEmbeddingsBox.Text = _settings.MusicEmbeddingsPath;
        _musicVaeBox.Text = _settings.MusicVaePath;
        _musicVaeCpuCombo.Text = _settings.MusicVaeOnCpu ? "enable" : "disable";
        _visionModelBox.Text = _settings.VisionModelPath;
        UpdateMmprojEnabled(_visionModelBox, _visionMmprojBox);
        _visionMmprojBox.Text = _settings.VisionMmprojPath;
        _visionMmprojCpuCombo.Text = _settings.VisionMmprojCpu ? "enable" : "disable";
        _mcpFileBox.Text = _settings.MCPFilePath;
        _chatTemplateBox.Text = _settings.TextChatTemplate;
        _plannerTemplateBox.Text = _settings.PlannerTemplatePath;
        _textLoraBox.Text = _settings.TextLoraPath;
        _textLoraMultBox.Text = _settings.TextLoraMult.ToString("F2");
        _textMoeExpertsBox.Text = _settings.TextMoeExpertsOverride > 0 ? _settings.TextMoeExpertsOverride.ToString() : "-1";
        _textMoeCpuModeCombo.Text = MoeCpuModeToDisplay(_settings.TextMoeCpuMode);
        _textMoeCpuLayersBox.Text = _settings.TextMoeCpuLayers.ToString();
        _visionMoeExpertsBox.Text = _settings.VisionMoeExpertsOverride > 0 ? _settings.VisionMoeExpertsOverride.ToString() : "-1";
        _visionMoeCpuModeCombo.Text = MoeCpuModeToDisplay(_settings.VisionMoeCpuMode);
        _visionMoeCpuLayersBox.Text = _settings.VisionMoeCpuLayers.ToString();
        _videoLoraBox.Text = _settings.VideoLoraPath;
        _videoLoraMultBox.Text = _settings.VideoLoraMult.ToString("F2");
        _audioLoraBox.Text = _settings.AudioLoraPath;
        _audioLoraMultBox.Text = _settings.AudioLoraMult.ToString("F2");
        _extraArgsBox.Text = _settings.KoboldExtraArgs;
        _portBox.Text = _settings.KoboldPort.ToString();
        _gpuLayersBox.Text = _settings.GpuLayers.ToString();
        _threadsBox.Text = _settings.Threads.ToString();
        _contextSizeBox.Text = _settings.ContextSize.ToString();
        _safetyMarginCombo.Text = _settings.TokenSafetyMargin switch
        {
            "none" => "none",
            "strict" => "strict",
            "safe" => "safe",
            _ => "balance"
        };
        _batchSizeBox.Text = _settings.BatchSize.ToString();
        _blasBatchSizeBox.Text = _settings.BlasBatchSize.ToString();
        _noKvOffloadCombo.Text = _settings.NoKvOffload ? "enable" : "disable";
        _useMlockCombo.Text = _settings.UseMlock switch
        {
            "enable" => "enable",
            _ => "disable"
        };
        _mmapCombo.Text = _settings.UseMmap ? "enable" : "disable";
        _keepClipOnCpuCombo.Text = _settings.KeepClipOnCpu ? "enable" : "disable";
        _sdClipOnCpuCombo.Text = _settings.SdClipOnCpu ? "enable" : "disable";
        _sdVaeOnCpuCombo.Text = _settings.SdVaeOnCpu ? "enable" : "disable";
        _flashAttnCombo.Text = _settings.FlashAttention switch
        {
            "enable" => "enable",
            _ => "disable"
        };
        _contextShiftCombo.Text = _settings.ContextShift switch
        {
            "enable" => "enable",
            _ => "disable"
        };
        _launchBrowserCombo.Text = _settings.LaunchBrowser ? "enable" : "disable";
        _useMmqCombo.Text = _settings.UseMmq switch
        {
            "enable" => "enable",
            _ => "disable"
        };
        _fastForwardingCombo.Text = _settings.FastForwarding switch
        {
            "enable" => "enable",
            _ => "disable"
        };
        _allowSwaCombo.Text = _settings.AllowSwa switch
        {
            "enable" => "enable",
            _ => "disable"
        };
        _tpsCombo.Text = _settings.ShowTps ? "enable" : "disable";
        _sdFlashAttnCombo.Text = _settings.SdFlashAttention ? "enable" : "disable";
        _sdLoraBox.Text = _settings.SdLoraPath;
        _sdLoraMultBox.Text = _settings.SdLoraMult.ToString("F2");
        _sdTiledVaeBox.Text = _settings.SdTiledVae.ToString();
        _runtimeLoraCombo.Text = _settings.RuntimeLora switch
        {
            "file" => "File",
            "directory" => "Directory",
            _ => "Disabled"
        };
        _conv2dCombo.Text = _settings.SdConvDirect switch
        {
            "vaeonly" => "vaeonly",
            "full" => "full",
            _ => "off"
        };
        _gpuCombo.Text = _settings.GpuId switch
        {
            "Auto" => "Auto",
            "0" => "0",
            "1" => "1",
            "2" => "2",
            "3" => "3",
            _ => "Auto"
        };
        _backendCombo.Text = _settings.Backend switch
        {
            "cuda" => "CUDA",
            "vulkan" => "Vulkan",
            "cpu" => "CPU",
            _ => "Auto"
        };
        _outputDirBox.Text = _settings.OutputPath;
        _logToFileCheck.IsChecked = _settings.LogToFile;
        _widthSlider.Value = _settings.ImageWidth;
        _heightSlider.Value = _settings.ImageHeight;
        _stepsSlider.Value = _settings.ImageSteps;
        _cfgSlider.Value = _settings.ImageCfgScale;
        _textQuantKvCombo.Text = _settings.TextQuantizedKvCache;
        _textRopeScaleBox.Text = _settings.TextRopeScale.ToString("0.###");
        _textRopeBaseBox.Text = _settings.TextRopeBase.ToString("0.###");
        _visionQuantKvCombo.Text = _settings.VisionQuantizedKvCache;
        _visionRopeScaleBox.Text = _settings.VisionRopeScale.ToString("0.###");
        _visionRopeBaseBox.Text = _settings.VisionRopeBase.ToString("0.###");
        _smartContextCombo.Text = _settings.SmartContext switch { "enable" => "enable", _ => "disable" };
        _overrideNativeContextBox.Text = _settings.OverrideNativeContext > 0 ? _settings.OverrideNativeContext.ToString() : "0";
        _tensorSplitBox.Text = _settings.TensorSplit;
        _noAvx2Combo.Text = _settings.NoAvx2 switch { "enable" => "enable", _ => "disable" };
        _failsafeCombo.Text = _settings.Failsafe switch { "enable" => "enable", _ => "disable" };
        _debugModeCombo.Text = _settings.DebugMode switch { 1 => "1 (basic)", 2 => "2 (verbose)", _ => "0 (off)" };
        _overrideTensorsBox.Text = _settings.OverrideTensors;
        _overrideKvBox.Text = _settings.OverrideKv;
        _cacheSlotsBox.Text = _settings.CacheSlots > 0 ? _settings.CacheSlots.ToString() : "0";
        _defaultGenAmtBox.Text = _settings.DefaultGenAmt > 0 ? _settings.DefaultGenAmt.ToString() : "1536";
        _enableGuidanceCombo.Text = _settings.EnableGuidance switch { "enable" => "enable", _ => "disable" };
        _thinkEffortCombo.Text = _settings.ThinkEffort;
        _swaPaddingBox.Text = _settings.SwaPadding > 0 ? _settings.SwaPadding.ToString() : "0";
        _draftModelBox.Text = _settings.DraftModelPath;
        _draftAmountBox.Text = _settings.DraftAmount > 0 ? _settings.DraftAmount.ToString() : "4";
        _useMtpCombo.Text = _settings.UseMtp switch { "enable" => "enable", _ => "disable" };
        _draftGpuLayersBox.Text = _settings.DraftGpuLayers > 0 ? _settings.DraftGpuLayers.ToString() : "-1";
        _embedsModelBox.Text = _settings.EmbedsModelPath;
        _ectxBox.Text = _settings.EmbedsMaxCtx > 0 ? _settings.EmbedsMaxCtx.ToString() : "4096";
        _embedsGpuCombo.Text = _settings.EmbedsGpu switch { "enable" => "enable", _ => "disable" };
        _autoFitCombo.Text = _settings.AutoFit switch { "enable" => "enable", _ => "disable" };
        if (_txtTempSlider != null) _txtTempSlider.Value = _settings.TextTemperature;
        if (_txtTopPSlider != null) _txtTopPSlider.Value = _settings.TextTopP;
        if (_txtTopKSlider != null) _txtTopKSlider.Value = _settings.TextTopK;
        if (_txtRepPenSlider != null) _txtRepPenSlider.Value = _settings.TextRepeatPenalty;
        if (_txtTimeoutBox != null) _txtTimeoutBox.Text = _settings.TextTimeoutSeconds.ToString();
        if (_maxIterSlider != null) _maxIterSlider.Value = _settings.MaxIterations;
        if (_stallNudgeSlider != null) _stallNudgeSlider.Value = _settings.StallNudgeThreshold;
        if (_stallLockoutSlider != null) _stallLockoutSlider.Value = _settings.StallLockoutThreshold;
        if (_readNudgeSlider != null) _readNudgeSlider.Value = _settings.ReadFileNudgeThreshold;
        if (_readHardStopSlider != null) _readHardStopSlider.Value = _settings.ReadFileHardStopThreshold;
    }

    /// <summary>Enables or disables the mmproj field based on whether the corresponding
    /// model field has a path — mmproj is useless without a model loaded.</summary>
    private static void UpdateMmprojEnabled(TextBox modelBox, TextBox mmprojBox)
    {
        var hasModel = !string.IsNullOrWhiteSpace(modelBox.Text);
        mmprojBox.IsEnabled = hasModel;
        if (!hasModel)
        {
            mmprojBox.Text = "";
            mmprojBox.ToolTip = "Load a model first — MMProj has no effect without one.";
        }
        else
        {
            mmprojBox.ToolTip = mmprojBox.ToolTip is string t && t.Contains("Load a model") ? null : mmprojBox.ToolTip;
        }
    }

    private void SaveAndClose()
    {
        ApplyToSettings();
        DialogResult = true;
    }

    private void SaveConfigToFile()
    {
        try
        {
            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                Filter = "PromptWhizz Config (*.pwconf)|*.pwconf|All files (*.*)|*.*",
                DefaultExt = ".pwconf",
                Title = "Save config as",
                FileName = Path.GetFileNameWithoutExtension(_configPath) + ".pwconf"
            };
            if (dlg.ShowDialog(this) != true) return;

            ApplyToSettings();
            SettingsApplied?.Invoke();
            _settings.Save(dlg.FileName);
            MessageBox.Show(this, "Config saved.", "Settings", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Error saving config: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void LoadConfigFromFile()
    {
        try
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Filter = "PromptWhizz Config (*.pwconf)|*.pwconf|JSON (*.json)|*.json|All files (*.*)|*.*",
                DefaultExt = ".pwconf",
                Title = "Load config"
            };
            if (dlg.ShowDialog(this) != true) return;

            var loaded = AppSettings.Load(dlg.FileName);
            loaded.KoboldPort = Math.Max(loaded.KoboldPort, 1);
            loaded.Threads = Math.Max(loaded.Threads, 1);
            loaded.ContextSize = Math.Max(loaded.ContextSize, 1);
            loaded.BatchSize = Math.Max(loaded.BatchSize, 1);
            if (loaded.ImageCfgScale <= 0) loaded.ImageCfgScale = 7;
            _settings.KoboldExePath = loaded.KoboldExePath;
            _settings.ModelPath = loaded.ModelPath;
            _settings.TextModelPath = loaded.TextModelPath;
            _settings.TextMmprojPath = loaded.TextMmprojPath;
            _settings.TextMmprojCpu = loaded.TextMmprojCpu;
            _settings.TextMoeExpertsOverride = loaded.TextMoeExpertsOverride;
            _settings.TextMoeCpuMode = loaded.TextMoeCpuMode;
            _settings.TextMoeCpuLayers = loaded.TextMoeCpuLayers;
            _settings.VisionMoeExpertsOverride = loaded.VisionMoeExpertsOverride;
            _settings.VisionMoeCpuMode = loaded.VisionMoeCpuMode;
            _settings.VisionMoeCpuLayers = loaded.VisionMoeCpuLayers;
            _settings.ClipLPath = loaded.ClipLPath;
            _settings.TextEncoderPath = loaded.TextEncoderPath;
            _settings.ImageVaePath = loaded.ImageVaePath;
            _settings.ImageUseExternalVae = loaded.ImageUseExternalVae;
            _settings.VideoUseExternalVae = loaded.VideoUseExternalVae;
            _settings.VideoModelPath = loaded.VideoModelPath;
            _settings.VideoVaePath = loaded.VideoVaePath;
            _settings.VideoT5Path = loaded.VideoT5Path;
            _settings.AudioModelPath = loaded.AudioModelPath;
            _settings.VoiceModelPath = loaded.VoiceModelPath;
            _settings.VoiceTokenizerPath = loaded.VoiceTokenizerPath;
            if (!string.IsNullOrWhiteSpace(loaded.VoiceTtsDir))
                _settings.VoiceTtsDir = loaded.VoiceTtsDir;
            _settings.MusicLlmPath = loaded.MusicLlmPath;
            _settings.MusicDiffusionPath = loaded.MusicDiffusionPath;
            _settings.MusicEmbeddingsPath = loaded.MusicEmbeddingsPath;
            _settings.MusicVaePath = loaded.MusicVaePath;
            _settings.MusicVaeOnCpu = loaded.MusicVaeOnCpu;
            _settings.VisionModelPath = loaded.VisionModelPath;
            _settings.VisionMmprojPath = loaded.VisionMmprojPath;
            _settings.VisionMmprojCpu = loaded.VisionMmprojCpu;
            _settings.MCPFilePath = loaded.MCPFilePath;
            _settings.TextChatTemplate = loaded.TextChatTemplate;
            _settings.PlannerTemplatePath = loaded.PlannerTemplatePath;
            _settings.KoboldExtraArgs = loaded.KoboldExtraArgs;
            _settings.KoboldPort = loaded.KoboldPort;
            _settings.GpuLayers = loaded.GpuLayers;
            _settings.Threads = loaded.Threads;
            _settings.ContextSize = loaded.ContextSize;
            _settings.TokenSafetyMargin = loaded.TokenSafetyMargin;
            _settings.BatchSize = loaded.BatchSize;
            _settings.NoKvOffload = loaded.NoKvOffload;
            _settings.UseMlock = loaded.UseMlock;
            _settings.UseMmap = loaded.UseMmap;
            _settings.KeepClipOnCpu = loaded.KeepClipOnCpu;
            _settings.SdClipOnCpu = loaded.SdClipOnCpu;
            _settings.SdVaeOnCpu = loaded.SdVaeOnCpu;
            _settings.FlashAttention = loaded.FlashAttention;
            _settings.ContextShift = loaded.ContextShift;
            _settings.LaunchBrowser = loaded.LaunchBrowser;
            _settings.ShowTps = loaded.ShowTps;
            _settings.UseMmq = loaded.UseMmq;
            _settings.FastForwarding = loaded.FastForwarding;
            _settings.AllowSwa = loaded.AllowSwa;
            _settings.SdFlashAttention = loaded.SdFlashAttention;
            _settings.SdLoraPath = loaded.SdLoraPath;
            _settings.SdLoraMult = loaded.SdLoraMult;
            _settings.SdTiledVae = loaded.SdTiledVae;
            _settings.SdConvDirect = loaded.SdConvDirect;
            _settings.RuntimeLora = loaded.RuntimeLora;
            _settings.TextLoraPath = loaded.TextLoraPath;
            _settings.TextLoraMult = loaded.TextLoraMult;
            _settings.VideoLoraPath = loaded.VideoLoraPath;
            _settings.VideoLoraMult = loaded.VideoLoraMult;
            _settings.AudioLoraPath = loaded.AudioLoraPath;
            _settings.AudioLoraMult = loaded.AudioLoraMult;
            _settings.GpuId = loaded.GpuId;
            _settings.Backend = loaded.Backend;
            _settings.ImageWidth = loaded.ImageWidth;
            _settings.ImageHeight = loaded.ImageHeight;
            _settings.ImageSteps = loaded.ImageSteps;
            _settings.ImageCfgScale = loaded.ImageCfgScale;
            _settings.TextQuantizedKvCache = loaded.TextQuantizedKvCache;
            _settings.TextRopeScale = loaded.TextRopeScale;
            _settings.TextRopeBase = loaded.TextRopeBase;
            _settings.VisionQuantizedKvCache = loaded.VisionQuantizedKvCache;
            _settings.VisionRopeScale = loaded.VisionRopeScale;
            _settings.VisionRopeBase = loaded.VisionRopeBase;
            _settings.SmartContext = loaded.SmartContext;
            _settings.OverrideNativeContext = loaded.OverrideNativeContext;
            _settings.TensorSplit = loaded.TensorSplit;
            _settings.NoAvx2 = loaded.NoAvx2;
            _settings.Failsafe = loaded.Failsafe;
            _settings.DebugMode = loaded.DebugMode;
            _settings.OverrideTensors = loaded.OverrideTensors;
            _settings.OverrideKv = loaded.OverrideKv;
            _settings.CacheSlots = loaded.CacheSlots;
            _settings.DefaultGenAmt = loaded.DefaultGenAmt;
            _settings.EnableGuidance = loaded.EnableGuidance;
            _settings.ThinkEffort = loaded.ThinkEffort;
            _settings.SwaPadding = loaded.SwaPadding;
            _settings.DraftModelPath = loaded.DraftModelPath;
            _settings.DraftAmount = loaded.DraftAmount;
            _settings.UseMtp = loaded.UseMtp;
            _settings.EmbedsModelPath = loaded.EmbedsModelPath;
            _settings.EmbedsMaxCtx = loaded.EmbedsMaxCtx;
            _settings.EmbedsGpu = loaded.EmbedsGpu;
            _settings.OutputPath = loaded.OutputPath;
            PopulateFromSettings();
            MessageBox.Show(this, "Config loaded.", "Settings", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Error loading config: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ApplyToSettings()
    {
        _settings.KoboldExePath = _exePathBox.Text.Trim();
        _settings.ModelPath = _modelPathBox.Text.Trim();
        _settings.TextModelPath = _textModelBox.Text.Trim();
        _settings.ClipLPath = _clipLBox.Text.Trim();
        _settings.TextEncoderPath = _t5Box.Text.Trim();
        _settings.ImageVaePath = _vaeBox.Text.Trim();
        _settings.ImageUseExternalVae = _imageVaeCombo.Text == "enable";
        _settings.VideoUseExternalVae = _videoVaeCombo.Text == "enable";
        _settings.VideoModelPath = _videoModelBox.Text.Trim();
        _settings.VideoVaePath = _videoVaeBox.Text.Trim();
        _settings.VideoT5Path = _videoT5Box.Text.Trim();
        _settings.AudioModelPath = _audioModelBox.Text.Trim();
        _settings.VoiceModelPath = _voiceModelBox.Text.Trim();
        _settings.VoiceTokenizerPath = _voiceTokenizerBox.Text.Trim();
        _settings.VoiceTtsDir = _voiceTtsDirBox.Text.Trim();
        _settings.MusicLlmPath = _musicLlmBox.Text.Trim();
        _settings.MusicDiffusionPath = _musicDiffusionBox.Text.Trim();
        _settings.MusicEmbeddingsPath = _musicEmbeddingsBox.Text.Trim();
        _settings.MusicVaePath = _musicVaeBox.Text.Trim();
        _settings.MusicVaeOnCpu = _musicVaeCpuCombo.Text == "enable";
        _settings.VisionModelPath = _visionModelBox.Text.Trim();
        _settings.VisionMmprojPath = _visionMmprojBox.Text.Trim();
        _settings.VisionMmprojCpu = _visionMmprojCpuCombo.Text == "enable";
        int.TryParse(_visionMoeExpertsBox.Text.Trim(), out int visionMoeExperts);
        _settings.VisionMoeExpertsOverride = visionMoeExperts > 0 ? visionMoeExperts : -1;
        _settings.VisionMoeCpuMode = MoeCpuModeFromDisplay(_visionMoeCpuModeCombo.Text);
        int.TryParse(_visionMoeCpuLayersBox.Text.Trim(), out int visionMoeCpuLayers);
        _settings.VisionMoeCpuLayers = visionMoeCpuLayers > 0 ? visionMoeCpuLayers : 999;
        _settings.TextMmprojPath = _textMmprojBox.Text.Trim();
        _settings.MCPFilePath = _mcpFileBox.Text.Trim();
        _settings.TextChatTemplate = _chatTemplateBox.Text.Trim();
        _settings.PlannerTemplatePath = _plannerTemplateBox.Text.Trim();
        _settings.TextMmprojCpu = _textMmprojCpuCombo.Text == "enable";
        _settings.TextLoraPath = _textLoraBox.Text.Trim();
        float.TryParse(_textLoraMultBox.Text.Trim(), out float tlMult);
        _settings.TextLoraMult = tlMult > 0 ? tlMult : 1.0f;
        int.TryParse(_textMoeExpertsBox.Text.Trim(), out int textMoeExperts);
        _settings.TextMoeExpertsOverride = textMoeExperts > 0 ? textMoeExperts : -1;
        _settings.TextMoeCpuMode = MoeCpuModeFromDisplay(_textMoeCpuModeCombo.Text);
        int.TryParse(_textMoeCpuLayersBox.Text.Trim(), out int textMoeCpuLayers);
        _settings.TextMoeCpuLayers = textMoeCpuLayers > 0 ? textMoeCpuLayers : 999;
        _settings.VideoLoraPath = _videoLoraBox.Text.Trim();
        float.TryParse(_videoLoraMultBox.Text.Trim(), out float vlMult);
        _settings.VideoLoraMult = vlMult > 0 ? vlMult : 1.0f;
        _settings.AudioLoraPath = _audioLoraBox.Text.Trim();
        float.TryParse(_audioLoraMultBox.Text.Trim(), out float alMult);
        _settings.AudioLoraMult = alMult > 0 ? alMult : 1.0f;
        _settings.KoboldExtraArgs = _extraArgsBox.Text.Trim();
        int.TryParse(_portBox.Text.Trim(), out int port);
        _settings.KoboldPort = port > 0 ? port : 5001;
        int.TryParse(_gpuLayersBox.Text.Trim(), out int gl);
        _settings.GpuLayers = gl;
        _settings.GpuId = _gpuCombo.SelectedValue as string ?? "Auto";
        int.TryParse(_threadsBox.Text.Trim(), out int threads);
        _settings.Threads = threads > 0 ? threads : Environment.ProcessorCount;
        int.TryParse(_contextSizeBox.Text.Trim(), out int ctx);
        _settings.ContextSize = ctx > 0 ? ctx : 4096;
        _settings.TokenSafetyMargin = _safetyMarginCombo.Text switch
        {
            "none" => "none",
            "strict" => "strict",
            "safe" => "safe",
            _ => "balance"
        };
        int.TryParse(_batchSizeBox.Text.Trim(), out int bs);
        _settings.BatchSize = bs > 0 ? bs : 512;
        int.TryParse(_blasBatchSizeBox.Text.Trim(), out int bbs);
        _settings.BlasBatchSize = bbs > 0 ? bbs : 512;
        _settings.NoKvOffload = _noKvOffloadCombo.Text == "enable";
        _settings.UseMlock = _useMlockCombo.Text switch { "enable" => "enable", _ => "disable" };
        _settings.UseMmap = _mmapCombo.Text == "enable";
        _settings.KeepClipOnCpu = _keepClipOnCpuCombo.Text == "enable";
        _settings.SdClipOnCpu = _sdClipOnCpuCombo.Text == "enable";
        _settings.SdVaeOnCpu = _sdVaeOnCpuCombo.Text == "enable";
        _settings.FlashAttention = _flashAttnCombo.Text switch { "enable" => "enable", _ => "disable" };
        _settings.ContextShift = _contextShiftCombo.Text switch { "enable" => "enable", _ => "disable" };
        _settings.LaunchBrowser = _launchBrowserCombo.Text == "enable";
        _settings.ShowTps = _tpsCombo.Text == "enable";
        _settings.UseMmq = _useMmqCombo.Text switch { "enable" => "enable", _ => "disable" };
        _settings.FastForwarding = _fastForwardingCombo.Text switch { "enable" => "enable", _ => "disable" };
        _settings.AllowSwa = _allowSwaCombo.Text switch { "enable" => "enable", _ => "disable" };
        _settings.SdFlashAttention = _sdFlashAttnCombo.Text == "enable";
        _settings.SdLoraPath = _sdLoraBox.Text.Trim();
        float.TryParse(_sdLoraMultBox.Text.Trim(), out float mult);
        _settings.SdLoraMult = mult > 0 ? mult : 1.0f;
        int.TryParse(_sdTiledVaeBox.Text.Trim(), out int tiled);
        _settings.SdTiledVae = tiled > 0 ? tiled : 640;
        _settings.RuntimeLora = _runtimeLoraCombo.Text switch
        {
            "File" => "file",
            "Directory" => "directory",
            _ => "disabled"
        };
        _settings.SdConvDirect = _conv2dCombo.Text switch
        {
            "vaeonly" => "vaeonly",
            "full" => "full",
            _ => "off"
        };
        _settings.Backend = _backendCombo.Text switch
        {
            "CUDA" => "cuda",
            "Vulkan" => "vulkan",
            "CPU" => "cpu",
            _ => "auto"
        };
        _settings.ImageWidth = (int)_widthSlider.Value;
        _settings.ImageHeight = (int)_heightSlider.Value;
        _settings.ImageSteps = (int)_stepsSlider.Value;
        _settings.ImageCfgScale = (float)_cfgSlider.Value;
        _settings.TextQuantizedKvCache = _textQuantKvCombo.Text;
        double.TryParse(_textRopeScaleBox.Text.Trim(), out double trs);
        _settings.TextRopeScale = trs > 0 ? trs : 1.0;
        double.TryParse(_textRopeBaseBox.Text.Trim(), out double trb);
        _settings.TextRopeBase = trb > 0 ? trb : 10000.0;
        _settings.VisionQuantizedKvCache = _visionQuantKvCombo.Text;
        double.TryParse(_visionRopeScaleBox.Text.Trim(), out double vrs);
        _settings.VisionRopeScale = vrs > 0 ? vrs : 1.0;
        double.TryParse(_visionRopeBaseBox.Text.Trim(), out double vrb);
        _settings.VisionRopeBase = vrb > 0 ? vrb : 10000.0;
        _settings.SmartContext = _smartContextCombo.Text switch { "enable" => "enable", _ => "disable" };
        int.TryParse(_overrideNativeContextBox.Text.Trim(), out int onc);
        _settings.OverrideNativeContext = onc > 0 ? onc : 0;
        _settings.TensorSplit = _tensorSplitBox.Text.Trim();
        _settings.NoAvx2 = _noAvx2Combo.Text switch { "enable" => "enable", _ => "disable" };
        _settings.Failsafe = _failsafeCombo.Text switch { "enable" => "enable", _ => "disable" };
        _settings.DebugMode = _debugModeCombo.Text switch
        {
            "1 (basic)" => 1,
            "2 (verbose)" => 2,
            _ => 0
        };
        _settings.OverrideTensors = _overrideTensorsBox.Text.Trim();
        _settings.OverrideKv = _overrideKvBox.Text.Trim();
        int.TryParse(_cacheSlotsBox.Text.Trim(), out int cs);
        _settings.CacheSlots = cs > 0 ? cs : 0;
        int.TryParse(_defaultGenAmtBox.Text.Trim(), out int dga);
        _settings.DefaultGenAmt = dga > 0 ? dga : 1536;
        _settings.EnableGuidance = _enableGuidanceCombo.Text switch { "enable" => "enable", _ => "disable" };
        _settings.ThinkEffort = _thinkEffortCombo.Text;
        int.TryParse(_swaPaddingBox.Text.Trim(), out int sp);
        _settings.SwaPadding = sp > 0 ? sp : 0;
        _settings.DraftModelPath = _draftModelBox.Text.Trim();
        int.TryParse(_draftAmountBox.Text.Trim(), out int da);
        _settings.DraftAmount = da > 0 ? da : 0;
        _settings.UseMtp = _useMtpCombo.Text switch { "enable" => "enable", _ => "disable" };
        int.TryParse(_draftGpuLayersBox.Text.Trim(), out int dgl);
        _settings.DraftGpuLayers = dgl > 0 ? dgl : -1;
        _settings.EmbedsModelPath = _embedsModelBox.Text.Trim();
        int.TryParse(_ectxBox.Text.Trim(), out int ec);
        _settings.EmbedsMaxCtx = ec > 0 ? ec : 0;
        _settings.EmbedsGpu = _embedsGpuCombo.Text switch { "enable" => "enable", _ => "disable" };
        _settings.AutoFit = _autoFitCombo.Text switch { "enable" => "enable", _ => "disable" };
        _settings.OutputPath = _outputDirBox.Text.Trim();
        _settings.LogToFile = _logToFileCheck.IsChecked == true;
        _settings.TextTemperature = (float)_txtTempSlider.Value;
        _settings.TextTopP = (float)_txtTopPSlider.Value;
        _settings.TextTopK = (int)_txtTopKSlider.Value;
        _settings.TextRepeatPenalty = (float)_txtRepPenSlider.Value;
        int.TryParse(_txtTimeoutBox.Text.Trim(), out int toVal);
        _settings.TextTimeoutSeconds = toVal >= 0 ? toVal : 0;
        _settings.MaxIterations = (int)_maxIterSlider.Value;
        _settings.StallNudgeThreshold = (int)_stallNudgeSlider.Value;
        _settings.StallLockoutThreshold = Math.Max((int)_stallLockoutSlider.Value, _settings.StallNudgeThreshold);
        _settings.ReadFileNudgeThreshold = (int)_readNudgeSlider.Value;
        _settings.ReadFileHardStopThreshold = Math.Max((int)_readHardStopSlider.Value, _settings.ReadFileNudgeThreshold);
    }

    private static bool HasNvidiaGpu()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT * FROM Win32_VideoController");
            foreach (var obj in searcher.Get())
            {
                var name = obj["Name"]?.ToString() ?? "";
                if (name.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }
        catch { }
        return false;
    }

    private static bool HasNvidiaSmi()
    {
        try
        {
            using var p = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "nvidia-smi",
                    Arguments = "--query-gpu=name --format=csv,noheader",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                }
            };
            p.Start();
            return p.StandardOutput.ReadToEnd().Contains("NVIDIA", StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    private void LoadIcon()
    {
        try
        {
            var pngPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "assets", "promptar_logo_512.png");
            if (!File.Exists(pngPath))
                pngPath = Path.Combine(Environment.CurrentDirectory, "assets", "promptar_logo_512.png");
            if (!File.Exists(pngPath))
                pngPath = Path.Combine(Path.GetDirectoryName(GetType().Assembly.Location)!, "assets", "promptar_logo_512.png");
            if (File.Exists(pngPath))
            {
                var pngBytes = File.ReadAllBytes(pngPath);
                using var ms = new MemoryStream();
                var bw = new BinaryWriter(ms);
                bw.Write((short)0);   // reserved
                bw.Write((short)1);   // ICO type
                bw.Write((short)1);   // count
                bw.Write((byte)0);    // width (0=256)
                bw.Write((byte)0);    // height (0=256)
                bw.Write((byte)0);    // colors
                bw.Write((byte)0);    // reserved
                bw.Write((short)1);   // planes
                bw.Write((short)32);  // bpp
                bw.Write(pngBytes.Length); // size
                bw.Write(22);         // offset (6+16 header)
                bw.Write(pngBytes);
                bw.Flush();
                ms.Position = 0;
                Icon = BitmapFrame.Create(ms, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            }
        }
        catch { }
    }
}