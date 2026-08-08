using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using static MyAiGen.AppTheme;

namespace MyAiGen;

public sealed class ScreenOcrOverlay : Window
{
    public event Func<byte[], CancellationToken, Task<string>>? OcrRequested;
    public event Action? ClosedByUser;
    public event Action<double>? FontSizeChanged;
    public event Action<Color>? TextColorChanged;
    public event Action<string>? FontFamilyChanged;
    public event Action<double>? BgOpacityChanged;
    public event Action<Color>? BgColorChanged;
    public event Action<int>? BubblePositionChanged;

    private readonly Border _bubbleBorder;
    private readonly System.Windows.Shapes.Path _bubbleTail;
    private readonly Grid _bubbleGrid;
    private readonly Grid _root;
    private readonly TextBlock _resultBlock;
    private readonly Label _statusLabel;
    private readonly System.Windows.Shapes.Path _resizeGrip;
    private readonly SolidColorBrush _bgBrush;
    private readonly SolidColorBrush _textBrush;
    private readonly DispatcherTimer _captureTimer;
    private CancellationTokenSource? _cts;
    private bool _isRunning;
    private bool _isResizing;
    private Point _resizeStart;
    private Size _resizeStartSize;
    private double _fontSize = 12;
    private double _bgAlpha = 220;
    private int _bubblePosition;
    private const int CaptureIntervalMs = 3000;

    private const uint WDA_EXCLUDEFROMCAPTURE = 0x11;

    [DllImport("user32.dll")]
    private static extern bool SetWindowDisplayAffinity(IntPtr hwnd, uint dwAffinity);

    private static readonly Brush Fg = AppTheme.F(new SolidColorBrush(Color.FromArgb(230, 220, 220, 230)));
    private static readonly Geometry[] TailGeoms = Array.ConvertAll(new[] {
        "M0,0 L12,0 L6,-10 Z", "M0,10 L12,10 L6,0 Z",
        "M0,0 L0,12 L-10,6 Z", "M10,0 L10,12 L0,6 Z"
    }, s => AppTheme.F(Geometry.Parse(s)));
    private static readonly Brush DimFg = AppTheme.F(new SolidColorBrush(Color.FromArgb(140, 160, 160, 170)));
    private static readonly Brush GripFill = AppTheme.F(new SolidColorBrush(Color.FromArgb(100, 160, 160, 170)));

    public ScreenOcrOverlay()
    {
        Title = "Screen OCR";
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        Width = 280;
        Height = 320;
        MinWidth = 120;
        MinHeight = 120;
        Left = (SystemParameters.WorkArea.Width - Width) / 2;
        Top = (SystemParameters.WorkArea.Height - Height) / 2;
        ResizeMode = ResizeMode.NoResize;
        _bgBrush = new SolidColorBrush(Color.FromArgb((byte)_bgAlpha, 20, 20, 30));
        _textBrush = new SolidColorBrush(Color.FromArgb(230, 220, 220, 230));
        _root = new Grid();
        _bubbleGrid = new Grid { Margin = new Thickness(10, 0, 10, 10) };

        _bubbleTail = new System.Windows.Shapes.Path
        {
            Fill = _bgBrush,
            Stroke = BubbleBorder,
            StrokeThickness = 1,
            Data = Geometry.Parse("M0,0 L12,0 L6,-10 Z"),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, -9, 0, 0)
        };

        _bubbleGrid.Children.Add(_bubbleTail);

        _bubbleBorder = new Border
        {
            Background = _bgBrush,
            BorderBrush = BubbleBorder,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(10, 4, 10, 8),
            VerticalAlignment = VerticalAlignment.Stretch
        };

        var bubbleInner = new Grid();
        bubbleInner.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        bubbleInner.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var controlBar = new Grid { Margin = new Thickness(0, 2, 0, 4) };
        controlBar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        controlBar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        controlBar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        _statusLabel = new Label
        {
            Content = "Ready",
            Foreground = DimFg,
            FontSize = 10,
            Padding = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Center
        };

        Grid.SetColumn(_statusLabel, 0);
        controlBar.Children.Add(_statusLabel);

        var closeBtn = new Button
        {
            Content = "\u00d7",
            FontSize = 14,
            Cursor = Cursors.Hand,
            Background = Brushes.Transparent,
            Foreground = DimFg,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(6, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center
        };

        closeBtn.Click += (_, _) => { Stop(); ClosedByUser?.Invoke(); Close(); };
        Grid.SetColumn(closeBtn, 2);
        controlBar.Children.Add(closeBtn);
        Grid.SetRow(controlBar, 0);
        bubbleInner.Children.Add(controlBar);

        _resultBlock = new TextBlock
        {
            Foreground = _textBrush,
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Top,
            Text = "Position this window over text to OCR."
        };

        var scroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = _resultBlock
        };

        Grid.SetRow(scroll, 1);
        bubbleInner.Children.Add(scroll);
        _bubbleBorder.Child = bubbleInner;
        _bubbleGrid.Children.Add(_bubbleBorder);
        _root.Children.Add(_bubbleGrid);

        _resizeGrip = new System.Windows.Shapes.Path
        {
            Data = Geometry.Parse("M0,8 L8,0 M0,4 L4,0 M4,8 L8,4"),
            Stroke = GripFill,
            StrokeThickness = 1.5,
            Cursor = Cursors.SizeNWSE,
            Width = 10,
            Height = 10,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 3, 3)
        };

        _resizeGrip.MouseLeftButtonDown += OnResizeGripMouseDown;
        _resizeGrip.MouseMove += OnResizeGripMouseMove;
        _resizeGrip.MouseLeftButtonUp += OnResizeGripMouseUp;
        _root.Children.Add(_resizeGrip);

        Content = _root;

        _captureTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(CaptureIntervalMs) };
        _captureTimer.Tick += OnCaptureTick;

        ApplyBubblePosition();
    }

    public void SetBubblePosition(int pos)
    {
        _bubblePosition = pos % 4;
        ApplyBubblePosition();
        BubblePositionChanged?.Invoke(_bubblePosition);
    }

    private void ApplyBubblePosition()
    {
        _bubbleTail.HorizontalAlignment = HorizontalAlignment.Center;
        _bubbleTail.VerticalAlignment = VerticalAlignment.Top;
        _bubbleTail.Margin = new Thickness(0, -9, 0, 0);
        _bubbleGrid.Margin = new Thickness(10, 0, 10, 10);

        if (_bubblePosition < 0 || _bubblePosition >= TailGeoms.Length) return;
        _bubbleTail.Data = TailGeoms[_bubblePosition];

        switch (_bubblePosition)
        {
            case 0:
                _bubbleTail.HorizontalAlignment = HorizontalAlignment.Center;
                _bubbleTail.VerticalAlignment = VerticalAlignment.Top;
                _bubbleTail.Margin = new Thickness(0, -9, 0, 0);
                _bubbleGrid.Margin = new Thickness(10, 0, 10, 10);
                break;
            case 1:
                _bubbleTail.HorizontalAlignment = HorizontalAlignment.Center;
                _bubbleTail.VerticalAlignment = VerticalAlignment.Bottom;
                _bubbleTail.Margin = new Thickness(0, 0, 0, -9);
                _bubbleGrid.Margin = new Thickness(10, 10, 10, 0);
                break;
            case 2:
                _bubbleTail.HorizontalAlignment = HorizontalAlignment.Left;
                _bubbleTail.VerticalAlignment = VerticalAlignment.Center;
                _bubbleTail.Margin = new Thickness(-9, 0, 0, 0);
                _bubbleGrid.Margin = new Thickness(0, 10, 10, 10);
                break;
            case 3:
                _bubbleTail.HorizontalAlignment = HorizontalAlignment.Right;
                _bubbleTail.VerticalAlignment = VerticalAlignment.Center;
                _bubbleTail.Margin = new Thickness(0, 0, -9, 0);
                _bubbleGrid.Margin = new Thickness(10, 10, 0, 10);
                break;
        }
    }

    public void Start()
    {
        if (_isRunning) return;
        _isRunning = true;
        _cts = new CancellationTokenSource();
        Show();
        var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        if (hwnd != IntPtr.Zero)
            SetWindowDisplayAffinity(hwnd, WDA_EXCLUDEFROMCAPTURE);
        _captureTimer.Start();
    }

    public void Stop()
    {
        _isRunning = false;
        _captureTimer.Stop();
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
    }

    public void ToggleVisibility()
    {
        if (Visibility == Visibility.Visible)
        {
            Visibility = Visibility.Hidden;
            _captureTimer.Stop();
        }
        else
        {
            Visibility = Visibility.Visible;
            Activate();
            if (_isRunning)
                _captureTimer.Start();
        }
    }

    public void SetResult(string text)
    {
        Dispatcher.BeginInvoke(() =>
        {
            _resultBlock.Text = text;
            _statusLabel.Content = "Updated";
        });
    }

    public void SetStatus(string status)
    {
        Dispatcher.BeginInvoke(() => _statusLabel.Content = status);
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (!_isResizing)
            DragMove();
    }

    private void OnResizeGripMouseDown(object sender, MouseButtonEventArgs e)
    {
        _isResizing = true;
        _resizeStart = e.GetPosition(this);
        _resizeStartSize = new Size(Width, Height);
        _resizeGrip.CaptureMouse();
        e.Handled = true;
    }

    private void OnResizeGripMouseMove(object sender, MouseEventArgs e)
    {
        if (!_isResizing) return;
        var pos = e.GetPosition(this);
        double dx = pos.X - _resizeStart.X;
        double dy = pos.Y - _resizeStart.Y;
        Width = System.Math.Max(MinWidth, _resizeStartSize.Width + dx);
        Height = System.Math.Max(MinHeight, _resizeStartSize.Height + dy);
    }

    private void OnResizeGripMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isResizing) return;
        _isResizing = false;
        _resizeGrip.ReleaseMouseCapture();
        e.Handled = true;
    }

    private async void OnCaptureTick(object? sender, EventArgs e)
    {
        if (!_isRunning || OcrRequested == null) return;
        if (_isResizing) return;

        try
        {
            _statusLabel.Content = "Capturing...";

            var tl = _bubbleBorder.PointToScreen(new Point(0, 0));
            var br = _bubbleBorder.PointToScreen(new Point(_bubbleBorder.ActualWidth, _bubbleBorder.ActualHeight));

            var sx = (int)tl.X;
            var sy = (int)tl.Y;
            var sw = System.Math.Clamp((int)(br.X - tl.X), 16, 4000);
            var sh = System.Math.Clamp((int)(br.Y - tl.Y), 16, 4000);

            sx = System.Math.Max(0, sx);
            sy = System.Math.Max(0, sy);

            var bytes = await Task.Run(() =>
            {
                using var bitmap = new System.Drawing.Bitmap(sw, sh, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                using var g = System.Drawing.Graphics.FromImage(bitmap);
                g.CopyFromScreen(sx, sy, 0, 0, new System.Drawing.Size(sw, sh));
                using var ms = new MemoryStream();
                bitmap.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
                return ms.ToArray();
            });

            _statusLabel.Content = "OCR...";
            var ct = _cts?.Token ?? CancellationToken.None;
            var result = await OcrRequested(bytes, ct);
            if (!string.IsNullOrWhiteSpace(result))
            {
                _resultBlock.Text = result;
                _statusLabel.Content = "OK";
            }
        }
        catch (OperationCanceledException)
        {
            _statusLabel.Content = "Stopped";
        }
        catch (Exception ex)
        {
            _statusLabel.Content = "Error";
            _resultBlock.Text = $"Capture error: {ex.Message}";
        }
    }

    public void SetFontSize(double size)
    {
        if (size < 8 || size > 72) return;
        _fontSize = size;
        _resultBlock.FontSize = size;
        FontSizeChanged?.Invoke(size);
    }

    public void SetTextColor(Color color)
    {
        _textBrush.Color = color;
        TextColorChanged?.Invoke(color);
    }

    public void SetFontFamily(string family)
    {
        _resultBlock.FontFamily = new FontFamily(family);
        FontFamilyChanged?.Invoke(family);
    }

    public void SetBgOpacity(double opacity)
    {
        var alpha = (byte)System.Math.Clamp(opacity * 255, 0, 255);
        _bgAlpha = alpha;
        _bgBrush.Color = Color.FromArgb(alpha, _bgBrush.Color.R, _bgBrush.Color.G, _bgBrush.Color.B);
        BgOpacityChanged?.Invoke(opacity);
    }

    public void SetBgColor(Color color)
    {
        _bgBrush.Color = Color.FromArgb((byte)_bgAlpha, color.R, color.G, color.B);
        BgColorChanged?.Invoke(color);
    }

}
