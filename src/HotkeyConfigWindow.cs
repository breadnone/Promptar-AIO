using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace MyAiGen;

public sealed class HotkeyConfigWindow : Window
{
    private readonly TextBlock _comboDisplay;
    private readonly List<int> _capturedKeys = new();
    private readonly int _overlayId;
    private GlobalHotkeyManager.HotkeyCombo _result;

    public GlobalHotkeyManager.HotkeyCombo Result => _result;

    public HotkeyConfigWindow(int overlayId, GlobalHotkeyManager.HotkeyCombo current)
    {
        _overlayId = overlayId;
        _result = current;

        Title = $"Hotkey — LIVE #{overlayId}";
        WindowStyle = WindowStyle.ToolWindow;
        ResizeMode = ResizeMode.NoResize;
        Width = 360;
        Height = 200;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Topmost = true;
        Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(30, 30, 40));
        Foreground = System.Windows.Media.Brushes.LightGray;
        FontSize = 13;

        Loaded += (_, _) => EnableDarkTitleBar();
        LoadIcon();

        var grid = new Grid { Margin = new Thickness(16) };
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var instr = new TextBlock
        {
            Text = "Press the hotkey combination (up to 3 keys):",
            TextWrapping = TextWrapping.Wrap,
            Foreground = System.Windows.Media.Brushes.Gray,
            FontSize = 12,
            Margin = new Thickness(0, 0, 0, 8)
        };
        Grid.SetRow(instr, 0);
        grid.Children.Add(instr);

        _comboDisplay = new TextBlock
        {
            Text = current.IsEmpty ? "(none)" : current.ToString(),
            FontSize = 22,
            FontWeight = FontWeights.Bold,
            Foreground = System.Windows.Media.Brushes.White,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 4, 0, 4),
            Padding = new Thickness(12, 6, 12, 6),
            Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(60, 255, 255, 255))
        };
        Grid.SetRow(_comboDisplay, 1);
        grid.Children.Add(_comboDisplay);

        var note = new TextBlock
        {
            Text = "Use modifiers (Ctrl, Alt, Shift) or any keys. Press Esc to cancel.",
            TextWrapping = TextWrapping.Wrap,
            Foreground = System.Windows.Media.Brushes.Gray,
            FontSize = 11,
            Margin = new Thickness(0, 4, 0, 10)
        };
        Grid.SetRow(note, 2);
        grid.Children.Add(note);

        var btnRow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
        var clearBtn = new Button
        {
            Content = "Clear",
            Width = 80,
            Height = 28,
            Margin = new Thickness(0, 0, 6, 0),
            Cursor = Cursors.Hand
        };
        clearBtn.Click += (_, _) => { _capturedKeys.Clear(); UpdateDisplay(); };

        var applyBtn = new Button
        {
            Content = "Apply",
            Width = 80,
            Height = 28,
            Margin = new Thickness(6, 0, 6, 0),
            Cursor = Cursors.Hand,
            IsDefault = true
        };
        applyBtn.Click += (_, _) => { _result = BuildCombo(); DialogResult = true; Close(); };

        var cancelBtn = new Button
        {
            Content = "Cancel",
            Width = 80,
            Height = 28,
            Margin = new Thickness(6, 0, 0, 0),
            Cursor = Cursors.Hand,
            IsCancel = true
        };
        cancelBtn.Click += (_, _) => { DialogResult = false; Close(); };

        btnRow.Children.Add(clearBtn);
        btnRow.Children.Add(applyBtn);
        btnRow.Children.Add(cancelBtn);
        Grid.SetRow(btnRow, 3);
        grid.Children.Add(btnRow);

        Content = grid;

        PreviewKeyDown += OnPreviewKeyDown;
        KeyDown += (_, e) => e.Handled = true;
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        e.Handled = true;

        if (e.Key == Key.Escape)
        {
            DialogResult = false;
            Close();
            return;
        }

        if (e.Key == Key.Enter && _capturedKeys.Count > 0)
        {
            _result = BuildCombo();
            DialogResult = true;
            Close();
            return;
        }

        if (e.Key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt
            or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin)
        {
            var modVk = KeyInterop.VirtualKeyFromKey(e.Key);
            if (modVk == 0) return;
            if (_capturedKeys.Contains(modVk)) return;
            if (_capturedKeys.Count >= 3) return;
            _capturedKeys.Add(modVk);
            UpdateDisplay();
            return;
        }

        var vk = KeyInterop.VirtualKeyFromKey(e.Key);
        if (vk == 0) return;
        if (_capturedKeys.Contains(vk)) return;

        if (_capturedKeys.Count >= 3) return;
        _capturedKeys.Add(vk);
        UpdateDisplay();
    }

    private void UpdateDisplay()
    {
        if (_capturedKeys.Count == 0)
            _comboDisplay.Text = "(none)";
        else
        {
            var parts = _capturedKeys.Select(k => GlobalHotkeyManager.HotkeyCombo.KeyName(k)).ToList();
            _comboDisplay.Text = string.Join(" + ", parts);
        }
    }

    private GlobalHotkeyManager.HotkeyCombo BuildCombo()
    {
        if (_capturedKeys.Count == 0) return default;
        int k1 = _capturedKeys[0];
        int? k2 = _capturedKeys.Count > 1 ? _capturedKeys[1] : null;
        int? k3 = _capturedKeys.Count > 2 ? _capturedKeys[2] : null;
        return new GlobalHotkeyManager.HotkeyCombo(k1, k2, k3);
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
