using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using static MyAiGen.AppTheme;

namespace MyAiGen;

public sealed class HelpWindow : Window
{
    public HelpWindow()
    {
        Title = "Help — Getting Started";
        Width = 700;
        Height = 600;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = new SolidColorBrush(Color.FromRgb(26, 26, 34));
        Foreground = Brushes.LightGray;
        FontSize = 13;
        FontFamily = new FontFamily("Segoe UI");

        Loaded += (_, _) => EnableDarkTitleBar();
        LoadIcon();

        var scroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Padding = new Thickness(20, 16, 20, 16)
        };
        var stack = new StackPanel { Margin = new Thickness(0) };
        scroll.Content = stack;
        Content = scroll;

        void H1(string t) => stack.Children.Add(new TextBlock
        {
            Text = t, FontSize = 22, FontWeight = FontWeights.Bold,
            Foreground = Brushes.White, Margin = new Thickness(0, 12, 0, 6)
        });

        void H2(string t) => stack.Children.Add(new TextBlock
        {
            Text = t, FontSize = 16, FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromRgb(200, 200, 220)),
            Margin = new Thickness(0, 10, 0, 4)
        });

        void P(string t) => stack.Children.Add(new TextBlock
        {
            Text = t, TextWrapping = TextWrapping.Wrap,
            Foreground = new SolidColorBrush(Color.FromRgb(180, 180, 190)),
            Margin = new Thickness(0, 0, 0, 8),
            LineHeight = 22
        });

        void Bullet(string t) => stack.Children.Add(new TextBlock
        {
            Text = "  \u2022  " + t, TextWrapping = TextWrapping.Wrap,
            Foreground = new SolidColorBrush(Color.FromRgb(170, 170, 185)),
            Margin = new Thickness(12, 0, 0, 4),
            LineHeight = 20
        });

        void Code(string t) => stack.Children.Add(new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(22, 22, 30)),
            BorderBrush = BorderDim,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(10, 6, 10, 6),
            Margin = new Thickness(0, 0, 0, 8),
            Child = new TextBlock
            {
                Text = t, FontFamily = new FontFamily("Consolas"),
                FontSize = 12, Foreground = new SolidColorBrush(Color.FromRgb(200, 220, 200)),
                TextWrapping = TextWrapping.Wrap
            }
        });

        H1("Welcome to PromptWhizz");
        P("This app lets you generate images, videos, text, and audio using" +
          " KoboldCpp as the backend engine. Each tab corresponds to a different" +
          " mode, and each mode needs the right model file and sometimes extra files" +
          " like CLIP, VAE, or MMProj. Below is a thorough walkthrough for each mode.");

        H2("First Steps — KoboldCpp");
        P("Before anything works, you need to download KoboldCpp and point the app to it.");
        P("KoboldCpp is a single .exe that acts as a local inference server. It loads" +
          " model files (GGUF format) and exposes an HTTP API that this app talks to.");
        Bullet("Go to Settings \u2192 General, click \"Download KoboldCpp\" to auto-download the latest build, OR");
        Bullet("Download koboldcpp.exe manually from the official GitHub releases page and set the path in Settings.");
        Bullet("The default port is 5001. Make sure nothing else is using it.");
        P("Once KoboldCpp is downloaded and the path is configured, click Start in the bottom bar." +
          " The status should change from \"Stopped\" to \"Ready\" once it finishes loading.");

        H2("Image Generation (Tex2Img)");
        P("This tab generates images from text prompts. It uses Stable Diffusion models" +
          " in GGUF format (the same format KoboldCpp uses for LLMs, but for SD).");
        Bullet("Model: An SD GGUF file like \"sd_xl_base_8.0.safetensors\" or a .gguf variant." +
               " Place it anywhere and set the path in Settings \u2192 Models \u2192 Image Model.");
        Bullet("CLIP-L: Needed for text encoding. A file like \"clip_l.safetensors\" or a GGUF CLIP model." +
               " Set path in Settings \u2192 Models \u2192 CLIP-L.");
        Bullet("T5 XXL: Optional but recommended for SDXL. A file like \"t5xxl_fp16.safetensors\"." +
               " Set path in Settings \u2192 Models \u2192 T5 XXL.");
        Bullet("VAE: Optional. If you want a specific VAE for decoding, set it in Settings \u2192 Models \u2192 VAE." +
               " If left empty, KoboldCpp uses the built-in VAE from the model.");
        Bullet("LoRA: Optional. You can load a LoRA adapter (safetensors/gguf) and adjust its multiplier.");
        P("Typical workflow: Write a prompt (e.g. \"a beautiful mountain landscape, digital art\")," +
          " optionally a negative prompt, adjust width/height/steps/CFG, then click Generate." +
          " The result appears on the right. You can zoom, save, or copy it.");

        H2("Video Generation (Tex2Vid)");
        P("This generates short video clips from text. It uses a video model" +
          " (typically an SD-based video model like Stable Video Diffusion or AnimateDiff in GGUF format).");
        Bullet("Model: A video model GGUF/safetensors. Set in Settings \u2192 Models \u2192 Video Model.");
        Bullet("Video VAE: Optional, same concept as Image VAE but for the video decoder.");
        Bullet("Video T5: Optional text encoder for video models that need it.");
        Bullet("Frames: How many frames to generate (default 50). FPS: playback speed (default 16).");
        P("Video generation takes longer than images. Once done, you can save the video or preview it inline.");

        H2("Vision / OCR (Vision)");
        P("This tab lets you send images to a multimodal LLM for analysis, OCR," +
          " translation, or describing what's in the image.");
        Bullet("Model: A multimodal GGUF file (e.g. LLaVA, Qwen-VL, or any vision-capable model)." +
               " Set in Settings \u2192 Models \u2192 Vision Model.");
        Bullet("MMProj: The multimodal projection file that aligns image embeddings with the LLM." +
               " Required for vision models. Set in Settings \u2192 Models \u2192 MMProj.");
        Bullet("MMProj CPU: Check this to offload the MMProj processing to CPU and save VRAM.");
        P("You can also create live OCR overlays that sit on top of your screen and" +
          " periodically capture a region, OCR it, and show the result in a speech bubble." +
          " Click the + button next to \"LIVE/Realtime Translation\" to add one.");

        H2("Text Chat (Text)");
        P("A standard text-based chat interface with an LLM. Uses the same model as Vision" +
          " (or a separate text-only model if configured).");
        Bullet("Model: Set in Settings \u2192 Models \u2192 Text Model (or use the same as Vision).");
        Bullet("MMProj: Only needed if you want multimodal support in the text tab too.");
        P("Type your message in the input box and press Enter or click Send." +
          " The chat history is preserved per session.");

        H2("Audio (Audio)");
        P("Three sub-modes controlled by the dropdown at the top of the tab:");

        P("Realtime Listening: Captures your system audio (microphone or loopback)" +
          " and transcribes it live using a Whisper model. The transcription appears in" +
          " a floating overlay window. You can customize the overlay colors, font, and opacity.");
        Bullet("Model: A Whisper GGUF model file. Set in Settings \u2192 Models \u2192 Audio Model.");
        Bullet("Click \"Start Listening\" to begin. The floating overlay will show live transcriptions.");
        Bullet("Check \"Translate to English\" to also translate non-English speech.");

        P("Transcribe File: Select an audio file (WAV, MP3, OGG, FLAC) and transcribe it" +
          " using the same Whisper model. The result appears in the text box below.");

        P("Voice Clone: Generate speech from text using a voice cloning / TTS model." +
          " Select a reference audio sample (a short recording of the voice you want to clone)" +
          " or record one directly with your microphone. Type what you want the voice to say" +
          " and click \"Clone & Speak\". The generated audio is saved and played automatically.");
        Bullet("Model: A TTS model file. Set in Settings \u2192 Models \u2192 Voice Model.");
        Bullet("You can record multiple reference samples and pick the best one from the history list.");
        Bullet("Enable \"Watch .txt\" to monitor a text file for changes" +
               " and auto-clone any new text that appears (useful for integrating with external tools).");

        H2("Overlays & Hotkeys");
        P("In the Vision tab, you can create multiple screen OCR overlays." +
          " Each overlay is a transparent window with a speech bubble that shows OCR results." +
          " You can drag it around, resize it (bottom-right grip), and assign a hotkey to toggle its visibility.");
        Bullet("Double-click an overlay in the LIVE list to open the hotkey config window.");
        Bullet("Press up to 3 keys simultaneously as the hotkey (e.g. Ctrl+F1, Alt+Shift+X).");
        Bullet("The hotkey toggles the overlay between visible and hidden.");

        H2("Tips & Troubleshooting");
        Bullet("If KoboldCpp fails to start, check the logs in the bottom panel for error messages.");
        Bullet("Make sure your model paths in Settings are correct and point to actual files.");
        Bullet("Image generation out of memory? Try reducing width/height, or enable Flash Attention in Settings.");
        Bullet("Vision not working? Make sure both the Vision Model and MMProj paths are set.");
        Bullet("Audio transcription slow? Try a smaller Whisper model (e.g. base or small instead of large).");
        Bullet("Voice cloning needs a TTS model that supports voice cloning from reference audio.");
        Bullet("If the app feels unresponsive during generation, that's normal — the model is working.");
        Bullet("You can change the backend between CUDA, Vulkan, and CPU in Settings \u2192 General.");

        H2("Model File Types");
        P("KoboldCpp uses GGUF format for LLMs and vision models. For Stable Diffusion," +
          " it supports both .safetensors and .gguf formats. Here's a quick reference:");
        Bullet(".gguf / .bin — Main model files (LLMs, Whisper, Vision, TTS)");
        Bullet(".safetensors — Stable Diffusion models, LoRAs, CLIP, VAE, T5 encoders");
        Bullet(".gguf (for SD) — Quantized Stable Diffusion models");

        H2("Keyboard Shortcuts");
        Bullet("Ctrl+Enter — Send message / Generate (in text/image/video tabs)");
        Bullet("Delete — Delete selected thumbnail in Image tab");
        Bullet("Ctrl+Mouse Wheel — Zoom in/out on the generated image");
        Bullet("Assignable hotkeys per overlay (see Overlays & Hotkeys section)");
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

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
}
