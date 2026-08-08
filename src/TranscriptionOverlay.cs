using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using static MyAiGen.AppTheme;

namespace MyAiGen;

public sealed class TranscriptionOverlay : Window
{
    public event Action<double>? FontSizeChanged;
    public event Action<Color>? TextColorChanged;
    public event Action<double>? BgOpacityChanged;

    private readonly TextBlock _textBlock;
    private readonly Label _sizeLabel;
    private readonly Label _opacityLabel;
    private readonly Border _outer;
    private readonly Rectangle _colorDot;
    private Point _dragStart;
    private bool _resizing;
    private ResizeEdge _resizeEdge;
    private const int ResizeHandleSize = 8;
    private double _fontSize = 14;
    private double _bgAlpha = 200;
    private int _colorIndex;

    private static readonly Brush DimFg = AppTheme.F(new SolidColorBrush(Color.FromArgb(200, 200, 200, 200)));
    private static readonly Brush DimLabel = AppTheme.F(new SolidColorBrush(Color.FromArgb(160, 140, 140, 140)));

    private static readonly Color[] TextColors =
    {
        Color.FromArgb(240, 240, 240, 240), // white
        Color.FromArgb(240, 200, 220, 255), // ice blue
        Color.FromArgb(240, 180, 255, 180), // mint
        Color.FromArgb(240, 255, 220, 180), // warm
        Color.FromArgb(240, 255, 180, 180), // rose
        Color.FromArgb(240, 210, 180, 255), // lavender
        Color.FromArgb(240, 255, 255, 180), // yellow
        Color.FromArgb(240, 180, 255, 255), // cyan
    };

    private static readonly Brush[] TextBrushes = Array.ConvertAll(TextColors,
        c => AppTheme.F(new SolidColorBrush(c)));

    private enum ResizeEdge { None, Left, Right, Top, Bottom, TopLeft, TopRight, BottomLeft, BottomRight }

    public TranscriptionOverlay()
    {
        Title = "Live Transcription";
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        Width = 400;
        Height = 120;
        Left = SystemParameters.WorkArea.Right - Width - 20;
        Top = SystemParameters.WorkArea.Bottom - Height - 60;
        ResizeMode = ResizeMode.NoResize;

        _outer = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb((byte)_bgAlpha, 20, 20, 20)),
            BorderBrush = BubbleBorder,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(8),
            Child = new Grid()
        };

        var grid = (Grid)_outer.Child;
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var headerRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 4) };

        _colorDot = new Rectangle { Width = 10, Height = 10, Fill = TextBrushes[0], RadiusX = 5, RadiusY = 5, Margin = new Thickness(0, 0, 4, 0), VerticalAlignment = VerticalAlignment.Center };
        var colorBtn = new Button
        {
            Content = _colorDot, Width = 22, Height = 18,
            Background = Brushes.Transparent, BorderThickness = new Thickness(1),
            BorderBrush = DimBorder,
            Cursor = Cursors.Hand, Padding = new Thickness(0), HorizontalContentAlignment = HorizontalAlignment.Center
        };
        colorBtn.Click += (_, _) => CycleColor();
        headerRow.Children.Add(colorBtn);

        _sizeLabel = new Label
        {
            Content = "14", Foreground = DimLabel,
            FontSize = 10, Padding = new Thickness(4, 0, 2, 0), VerticalAlignment = VerticalAlignment.Center
        };
        headerRow.Children.Add(_sizeLabel);

        var smallerBtn = new Button
        {
            Content = "A−", Width = 22, Height = 18, FontSize = 9,
            Background = Brushes.Transparent, Foreground = DimFg,
            BorderThickness = new Thickness(1), BorderBrush = DimBorder,
            Cursor = Cursors.Hand, Padding = new Thickness(0)
        };
        smallerBtn.Click += (_, _) => ChangeFontSize(-2);
        headerRow.Children.Add(smallerBtn);

        var biggerBtn = new Button
        {
            Content = "A+", Width = 22, Height = 18, FontSize = 9,
            Background = Brushes.Transparent, Foreground = DimFg,
            BorderThickness = new Thickness(1), BorderBrush = DimBorder,
            Cursor = Cursors.Hand, Margin = new Thickness(4, 0, 0, 0), Padding = new Thickness(0)
        };
        biggerBtn.Click += (_, _) => ChangeFontSize(2);
        headerRow.Children.Add(biggerBtn);

        _opacityLabel = new Label
        {
            Content = "80%", Foreground = DimLabel,
            FontSize = 10, Padding = new Thickness(6, 0, 2, 0), VerticalAlignment = VerticalAlignment.Center
        };
        headerRow.Children.Add(_opacityLabel);

        var darkerBtn = new Button
        {
            Content = "◐−", Width = 22, Height = 18, FontSize = 9,
            Background = Brushes.Transparent, Foreground = DimFg,
            BorderThickness = new Thickness(1), BorderBrush = DimBorder,
            Cursor = Cursors.Hand, Padding = new Thickness(0)
        };
        darkerBtn.Click += (_, _) => ChangeOpacity(-20);
        headerRow.Children.Add(darkerBtn);

        var lighterBtn = new Button
        {
            Content = "◐+", Width = 22, Height = 18, FontSize = 9,
            Background = Brushes.Transparent, Foreground = DimFg,
            BorderThickness = new Thickness(1), BorderBrush = DimBorder,
            Cursor = Cursors.Hand, Margin = new Thickness(4, 0, 0, 0), Padding = new Thickness(0)
        };
        lighterBtn.Click += (_, _) => ChangeOpacity(20);
        headerRow.Children.Add(lighterBtn);

        headerRow.Children.Add(new Rectangle { Width = 1, Height = 1, Fill = Brushes.Transparent });

        var closeBtn = new Button
        {
            Content = "✕", Width = 20, Height = 20, FontSize = 10,
            Background = Brushes.Transparent, Foreground = DimFg,
            BorderThickness = new Thickness(0), Cursor = Cursors.Hand,
            HorizontalAlignment = HorizontalAlignment.Right
        };
        closeBtn.Click += (_, _) => Close();
        headerRow.Children.Add(closeBtn);
        grid.Children.Add(headerRow);

        _textBlock = new TextBlock
        {
            Foreground = TextBrushes[0],
            FontSize = _fontSize, TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Top,
            Text = "Waiting for audio..."
        };
        var scroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = _textBlock, Margin = new Thickness(0, 0, 0, 2)
        };
        scroll.PreviewMouseWheel += (_, e) =>
        {
            if (Keyboard.Modifiers == ModifierKeys.Control && Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
            {
                ChangeOpacity(e.Delta > 0 ? 20 : -20);
                e.Handled = true;
            }
            else if (Keyboard.Modifiers == ModifierKeys.Control)
            {
                ChangeFontSize(e.Delta > 0 ? 2 : -2);
                e.Handled = true;
            }
        };
        Grid.SetRow(scroll, 1);
        grid.Children.Add(scroll);

        Content = _outer;

        MouseLeftButtonDown += OnMouseLeftButtonDown;
        MouseMove += OnMouseMove;
        MouseLeftButtonUp += OnMouseLeftButtonUp;
    }

    public void AppendText(string text)
    {
        if (!string.IsNullOrWhiteSpace(text))
            _textBlock.Text = text;
    }

    public void SetWaiting() => _textBlock.Text = "Waiting for audio...";

    public void SetFontSize(double size)
    {
        _fontSize = Math.Clamp(size, 8, 72);
        _textBlock.FontSize = _fontSize;
        _sizeLabel.Content = _fontSize.ToString();
    }

    public void SetTextColor(Color color)
    {
        var idx = Array.IndexOf(TextColors, color);
        var brush = idx >= 0 ? TextBrushes[idx] : F(new SolidColorBrush(color));
        _textBlock.Foreground = brush;
        _colorDot.Fill = brush;
    }

    public void SetFontFamily(FontFamily font)
    {
        _textBlock.FontFamily = font;
    }

    public void SetBgOpacity(double alpha)
    {
        _bgAlpha = Math.Clamp(alpha, 30, 240);
        var bg = (SolidColorBrush)_outer.Background;
        bg.Color = Color.FromArgb((byte)_bgAlpha, bg.Color.R, bg.Color.G, bg.Color.B);
        _opacityLabel.Content = $"{(int)(_bgAlpha / 240.0 * 100)}%";
    }

    private void ChangeFontSize(int delta)
    {
        _fontSize = Math.Clamp(_fontSize + delta, 8, 72);
        _textBlock.FontSize = _fontSize;
        _sizeLabel.Content = _fontSize.ToString();
        FontSizeChanged?.Invoke(_fontSize);
    }

    private void ChangeOpacity(int delta)
    {
        _bgAlpha = Math.Clamp(_bgAlpha + delta, 30, 240);
        var bg = (SolidColorBrush)_outer.Background;
        bg.Color = Color.FromArgb((byte)_bgAlpha, bg.Color.R, bg.Color.G, bg.Color.B);
        _opacityLabel.Content = $"{(int)(_bgAlpha / 240.0 * 100)}%";
        BgOpacityChanged?.Invoke(_bgAlpha);
    }

    private void CycleColor()
    {
        _colorIndex = (_colorIndex + 1) % TextColors.Length;
        _textBlock.Foreground = TextBrushes[_colorIndex];
        _colorDot.Fill = TextBrushes[_colorIndex];
        TextColorChanged?.Invoke(TextColors[_colorIndex]);
    }

    private void OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        var pos = e.GetPosition(this);
        var edge = HitTestResizeEdge(pos);
        if (edge != ResizeEdge.None)
        {
            _resizing = true;
            _resizeEdge = edge;
            _dragStart = pos;
            Mouse.Capture(this);
            return;
        }
        DragMove();
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        var pos = e.GetPosition(this);
        Cursor = HitTestResizeEdge(pos) switch
        {
            ResizeEdge.Left or ResizeEdge.Right => Cursors.SizeWE,
            ResizeEdge.Top or ResizeEdge.Bottom => Cursors.SizeNS,
            ResizeEdge.TopLeft or ResizeEdge.BottomRight => Cursors.SizeNWSE,
            ResizeEdge.TopRight or ResizeEdge.BottomLeft => Cursors.SizeNESW,
            _ => Cursors.Arrow
        };
        if (!_resizing) return;
        var delta = pos - _dragStart;
        var minW = 200; var minH = 60;
        if (_resizeEdge is ResizeEdge.Right or ResizeEdge.TopRight or ResizeEdge.BottomRight)
            Width = Math.Max(minW, Width + delta.X);
        if (_resizeEdge is ResizeEdge.Left or ResizeEdge.TopLeft or ResizeEdge.BottomLeft)
        { var newW = Math.Max(minW, Width - delta.X); Left += Width - newW; Width = newW; }
        if (_resizeEdge is ResizeEdge.Bottom or ResizeEdge.BottomLeft or ResizeEdge.BottomRight)
            Height = Math.Max(minH, Height + delta.Y);
        if (_resizeEdge is ResizeEdge.Top or ResizeEdge.TopLeft or ResizeEdge.TopRight)
        { var newH = Math.Max(minH, Height - delta.Y); Top += Height - newH; Height = newH; }
        _dragStart = pos;
    }

    private void OnMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        _resizing = false;
        Mouse.Capture(null);
    }

    private ResizeEdge HitTestResizeEdge(Point p)
    {
        var h = ResizeHandleSize;
        bool left = p.X <= h, right = p.X >= Width - h;
        bool top = p.Y <= h, bottom = p.Y >= Height - h;
        if (top && left) return ResizeEdge.TopLeft;
        if (top && right) return ResizeEdge.TopRight;
        if (bottom && left) return ResizeEdge.BottomLeft;
        if (bottom && right) return ResizeEdge.BottomRight;
        if (left) return ResizeEdge.Left;
        if (right) return ResizeEdge.Right;
        if (top) return ResizeEdge.Top;
        if (bottom) return ResizeEdge.Bottom;
        return ResizeEdge.None;
    }
}
