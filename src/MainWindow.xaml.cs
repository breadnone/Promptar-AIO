using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Management;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using Microsoft.Win32;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Documents;
using Path = System.IO.Path;
using Microsoft.VisualBasic;
using NAudio.Wave;
using static MyAiGen.AppTheme;

namespace MyAiGen;

internal static class RoleColors
{
    // Single source of truth for role -> color, shared by both converters below
    // so the bubble background and the role label never drift out of sync.
    public static readonly Brush BubbleDefault = new SolidColorBrush(Color.FromRgb(0x1A, 0x1A, 0x24));
    public static readonly Brush BubbleUser = new SolidColorBrush(Color.FromRgb(0x1A, 0x2A, 0x4A));
    public static readonly Brush BubbleAssistant = new SolidColorBrush(Color.FromRgb(0x2A, 0x1A, 0x3A));
    public static readonly Brush BubbleAudio = new SolidColorBrush(Color.FromRgb(0x10, 0x24, 0x1A));

    public static readonly Brush LabelDefault = new SolidColorBrush(Color.FromRgb(0x90, 0x90, 0x9C));
    public static readonly Brush LabelUser = new SolidColorBrush(Color.FromRgb(0xFF, 0xB8, 0x40));
    public static readonly Brush LabelAssistant = new SolidColorBrush(Color.FromRgb(0x40, 0xC8, 0xFF));
    public static readonly Brush LabelAudio = new SolidColorBrush(Color.FromRgb(0x4A, 0xC8, 0xA8));

    static RoleColors()
    {
        BubbleDefault.Freeze(); BubbleUser.Freeze(); BubbleAssistant.Freeze(); BubbleAudio.Freeze();
        LabelDefault.Freeze(); LabelUser.Freeze(); LabelAssistant.Freeze(); LabelAudio.Freeze();
    }
}

/// <summary>
/// Replaces a 3-DataTrigger Style with a single converter-bound property.
/// Same rendered colors as before, far fewer objects in the visual tree's Style/Trigger graph.
/// </summary>
internal sealed class RoleToBubbleBrushConverter : System.Windows.Data.IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture) =>
        value as string switch
        {
            "user" => RoleColors.BubbleUser,
            "assistant" => RoleColors.BubbleAssistant,
            "audio" => RoleColors.BubbleAudio,
            _ => RoleColors.BubbleDefault
        };

    public object ConvertBack(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
        => throw new NotSupportedException();
}

internal sealed class RoleToLabelBrushConverter : System.Windows.Data.IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture) =>
        value as string switch
        {
            "user" => RoleColors.LabelUser,
            "assistant" => RoleColors.LabelAssistant,
            "audio" => RoleColors.LabelAudio,
            _ => RoleColors.LabelDefault
        };

    public object ConvertBack(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// Collapses an element when the bound value is null, otherwise Visible.
/// Replaces 3 near-identical Style+DataTrigger blocks (image, attachment list,
/// download-all button) that each existed only to do this one check.
/// </summary>
internal sealed class NullToVisibilityConverter : System.Windows.Data.IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
    {
        if (value == null) return Visibility.Collapsed;
        if (value is System.Collections.ICollection c && c.Count == 0) return Visibility.Collapsed;
        if (value is string s && string.IsNullOrEmpty(s)) return Visibility.Collapsed;
        return Visibility.Visible;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
        => throw new NotSupportedException();
}

internal sealed class PathToBitmapImageConverter : System.Windows.Data.IValueConverter
{
    // Chat bubble images never render wider than ~360px (see assistImgTrigger below),
    // so there's no reason to decode at full resolution — capping DecodePixelWidth
    // cuts memory and CPU cost per image, which matters over a long session with many
    // generated diagrams.
    private const int MaxDecodePixelWidth = 720;

    public object? Convert(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
    {
        if (value is not string path || string.IsNullOrEmpty(path) || !File.Exists(path))
            return null;

        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            // CacheOption.OnLoad reads the whole file into memory up front and releases
            // the file handle before BeginInit() returns. Without this, the default
            // (OnDemand) keeps a stream open on the PNG for as long as the BitmapImage
            // is alive — which is what was causing "file being used by another process"
            // when the agent tried to re-render to the same path shortly after.
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            // Bypasses the shared WPF image cache so a rewritten file at the same path
            // (e.g. a re-generated diagram) is picked up instead of showing a stale image.
            bmp.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
            bmp.DecodePixelWidth = MaxDecodePixelWidth;
            bmp.UriSource = new Uri(path, UriKind.Absolute);
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch
        {
            // A partially-written file (read mid-save) or corrupt image shouldn't take
            // down the binding/UI — just show nothing for this bubble.
            return null;
        }
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
        => throw new NotSupportedException();
}

internal sealed class BoolToVisibilityConverter : System.Windows.Data.IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
    {
        if (value is bool b && b) return Visibility.Visible;
        return Visibility.Collapsed;
    }
    public object ConvertBack(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
        => throw new NotSupportedException();
}

internal sealed class InverseBoolToVisibilityConverter : System.Windows.Data.IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
    {
        if (value is bool b && b) return Visibility.Collapsed;
        return Visibility.Visible;
    }
    public object ConvertBack(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
        => throw new NotSupportedException();
}

internal sealed class ExpandedToToggleIconConverter : System.Windows.Data.IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
    {
        if (value is bool expanded && expanded) return "▼ ";
        return "▶ ";
    }
    public object ConvertBack(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
        => throw new NotSupportedException();
}

internal static class MarkdownFlowDoc
{
    internal static readonly Brush CodeBg = new SolidColorBrush(Color.FromArgb(255, 30, 30, 38));
    internal static readonly Brush CodeFg = new SolidColorBrush(Color.FromArgb(255, 220, 220, 160));
    internal static readonly Brush DimFg = new SolidColorBrush(Color.FromArgb(255, 150, 150, 162));
    internal static readonly Brush CollapseHeaderFg = new SolidColorBrush(Color.FromArgb(255, 150, 160, 200));
    internal static readonly Brush CollapseBg = new SolidColorBrush(Color.FromArgb(255, 24, 24, 32));
    internal static readonly Brush CollapseBorder = new SolidColorBrush(Color.FromArgb(255, 50, 50, 62));
    internal static readonly Brush CollapseBodyFg = new SolidColorBrush(Color.FromArgb(255, 210, 210, 220));
    internal static readonly FontFamily UiFont = new("Segoe UI");
    internal static readonly FontFamily MonoFont = new("Consolas");
    internal static readonly Regex InlineRegex = new(
        @"(?<code>`[^`]+`)|(?<bold>\*\*[^*]+?\*\*|__[^_]+?__)|(?<italic>\*[^*]+?\*|_[^_]+?_)|(?<dim>~[^~]+?~)",
        RegexOptions.Compiled);

    internal static readonly Regex ReasoningRegex = new(@"<reasoning>\s*(.*?)\s*</reasoning>", RegexOptions.Singleline | RegexOptions.Compiled);
    private static Style? _cachedExpanderStyle;

    internal static Style BuildDarkExpanderStyle()
    {
        if (_cachedExpanderStyle != null) return _cachedExpanderStyle;

        var xaml = @"
<Style xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' 
       xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' 
       TargetType='Expander'>
    <Setter Property='Foreground' Value='#FF9696A0'/>
    <Setter Property='Background' Value='#FF18181F'/>
    <Setter Property='BorderBrush' Value='#FF32323E'/>
    <Setter Property='BorderThickness' Value='1'/>
    <Setter Property='Padding' Value='6,3,6,3'/>
    <Setter Property='FontSize' Value='11'/>
    <Setter Property='Template'>
        <Setter.Value>
            <ControlTemplate TargetType='Expander'>
                <Border Background='{TemplateBinding Background}' BorderBrush='{TemplateBinding BorderBrush}' BorderThickness='{TemplateBinding BorderThickness}' CornerRadius='0'>
                    <DockPanel>
                        <ToggleButton x:Name='HeaderSite' DockPanel.Dock='Top' IsChecked='{Binding IsExpanded, Mode=TwoWay, RelativeSource={RelativeSource TemplatedParent}}' 
                                      Background='Transparent' BorderThickness='0' Cursor='Hand' Padding='{TemplateBinding Padding}' 
                                      HorizontalContentAlignment='Left'>
                            <ToggleButton.Template>
                                <ControlTemplate TargetType='ToggleButton'>
                                    <Border Background='Transparent' Padding='{TemplateBinding Padding}'>
                                        <StackPanel Orientation='Horizontal'>
                                            <Grid Width='8' Height='8' Margin='0,0,6,0'>
                                                <Path x:Name='Arrow' Data='M 0 1.5 L 5 4 L 0 6.5 Z' Fill='#FF9696A0' HorizontalAlignment='Center' VerticalAlignment='Center' RenderTransformOrigin='0.5,0.5'>
                                                    <Path.RenderTransform>
                                                        <RotateTransform Angle='0'/>
                                                    </Path.RenderTransform>
                                                </Path>
                                            </Grid>
                                            <ContentPresenter Content='{TemplateBinding Content}' VerticalAlignment='Center'/>
                                        </StackPanel>
                                    </Border>
                                    <ControlTemplate.Triggers>
                                        <Trigger Property='IsChecked' Value='True'>
                                            <Setter TargetName='Arrow' Property='RenderTransform'>
                                                <Setter.Value>
                                                    <RotateTransform Angle='90'/>
                                                </Setter.Value>
                                            </Setter>
                                        </Trigger>
                                        <Trigger Property='IsMouseOver' Value='True'>
                                            <Setter TargetName='Arrow' Property='Fill' Value='#FFC8C8D8'/>
                                        </Trigger>
                                    </ControlTemplate.Triggers>
                                </ControlTemplate>
                            </ToggleButton.Template>
                            <TextBlock Text='{TemplateBinding Header}' Foreground='{TemplateBinding Foreground}' FontSize='{TemplateBinding FontSize}' FontWeight='Normal'/>
                        </ToggleButton>
                        <ContentPresenter x:Name='ExpandSite' Content='{TemplateBinding Content}' ContentTemplate='{TemplateBinding ContentTemplate}' DockPanel.Dock='Bottom' Visibility='Collapsed' Margin='8,2,8,8'/>
                    </DockPanel>
                </Border>
                <ControlTemplate.Triggers>
                    <Trigger Property='IsExpanded' Value='True'>
                        <Setter TargetName='ExpandSite' Property='Visibility' Value='Visible'/>
                    </Trigger>
                </ControlTemplate.Triggers>
            </ControlTemplate>
        </Setter.Value>
    </Setter>
</Style>";
        _cachedExpanderStyle = (Style)System.Windows.Markup.XamlReader.Parse(xaml);
        return _cachedExpanderStyle;
    }

    /// <summary>Collapsible block for the TextBlock-based chat renderer: a Border +
    /// StackPanel with a clickable TextBlock header (▸/▾ arrow) that toggles the body's
    /// Visibility directly. No ToggleButton, no parsed ControlTemplate. Left-click is marked
    /// handled so it never bubbles to the ListBox (bubble select + auto-scroll = the old
    /// "scrollbar jumps on first click" bug) or to the bubble's own click-to-toggle.</summary>
    internal static Border BuildCollapsibleBlockLite(string key, Dictionary<string, bool>? expandStates,
        string headerText, string bodyText, bool monospaceBody, bool startExpanded = false)
    {
        // Restore whatever state the user last left this collapsible in, if we've
        // seen it before (streaming re-parses the whole message repeatedly, and
        // without this every open collapsible would snap shut on the next token).
        bool expanded = startExpanded;
        if (expandStates != null && expandStates.TryGetValue(key, out var saved))
            expanded = saved;

        var safeBody = bodyText
            .Replace("<<COLLAPSE", "\u00ABCOLLAPSE")
            .Replace("<</COLLAPSE", "\u00AB/COLLAPSE");

        // The body must be one TextBlock per logical line (like every paragraph
        // elsewhere in the message): TextBlock.GetPositionFromPoint mis-maps y to
        // the wrong line when a block is laid out with LineBreak inlines, so text
        // selection inside a multi-line rich body highlighted the wrong line. Mono
        // bodies keep a single TextBlock with '\n' in the string — that layout maps
        // correctly, so it stays as is.
        var bodyStack = new StackPanel
        {
            Margin = new Thickness(10, 2, 8, 8),
            Visibility = expanded ? Visibility.Visible : Visibility.Collapsed,
            UseLayoutRounding = true,
            SnapsToDevicePixels = true
        };
        if (monospaceBody)
        {
            bodyStack.Children.Add(new TextBlock
            {
                Text = safeBody,
                TextWrapping = TextWrapping.Wrap,
                Foreground = CodeFg,
                FontFamily = MonoFont,
                FontSize = 11,
                UseLayoutRounding = true,
                SnapsToDevicePixels = true
            });
        }
        else
        {
            foreach (var rawLine in safeBody.Split('\n'))
            {
                if (string.IsNullOrWhiteSpace(rawLine))
                    continue;
                var lineTb = new TextBlock
                {
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = CollapseBodyFg,
                    FontFamily = UiFont,
                    FontSize = 12,
                    UseLayoutRounding = true,
                    SnapsToDevicePixels = true
                };
                var trimmed = rawLine.TrimStart();
                if (trimmed.StartsWith("## ") || trimmed.StartsWith("##\t"))
                    lineTb.Inlines.Add(new Bold(new Run(trimmed.Substring(2).Trim())) { FontSize = 14 });
                else if (trimmed.StartsWith("### ") || trimmed.StartsWith("###\t"))
                    lineTb.Inlines.Add(new Bold(new Run(trimmed.Substring(3).Trim())));
                else
                    AppendInlines(lineTb.Inlines, rawLine);
                bodyStack.Children.Add(lineTb);
            }
        }

        // Single TextBlock for "▸ header" (arrow + label as one run) — half the visuals
        // of the old arrow/label pair, and one less Border (the body's margin carries
        // the spacing the old inner contentBorder provided).
        var headerTextBlock = new TextBlock
        {
            Text = (expanded ? "\u25BE " : "\u25B8 ") + headerText,
            FontSize = 11,
            Foreground = CollapseHeaderFg,
            VerticalAlignment = VerticalAlignment.Center,
            Cursor = Cursors.Hand
        };
        var header = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Cursor = Cursors.Hand,
            Margin = new Thickness(6, 3, 6, 3),
            Background = Brushes.Transparent,
            Tag = MarkdownView.HeaderSentinel // excluded from MarkdownView's text selection
        };
        header.Children.Add(headerTextBlock);

        void Toggle()
        {
            expanded = !expanded;
            bodyStack.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
            headerTextBlock.Text = (expanded ? "\u25BE " : "\u25B8 ") + headerText;
            if (expandStates != null) expandStates[key] = expanded;
        }

        header.MouseLeftButtonDown += (_, e) => e.Handled = true;
        header.MouseLeftButtonUp += (_, e) =>
        {
            Toggle();
            e.Handled = true;
        };

        var stack = new StackPanel
        {
            UseLayoutRounding = true,
            SnapsToDevicePixels = true
        };
        stack.Children.Add(header);
        stack.Children.Add(bodyStack);

        var wrapper = new Border
        {
            Child = stack,
            Background = CollapseBg,
            BorderBrush = CollapseBorder,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(0),
            UseLayoutRounding = true,
            SnapsToDevicePixels = true,
            Focusable = false
        };
        return wrapper;
    }

    internal static void AppendInlines(InlineCollection inlines, string text)
    {
        var pos = 0;
        foreach (Match m in InlineRegex.Matches(text))
        {
            if (m.Index > pos)
                inlines.Add(new Run(text[pos..m.Index]));

            if (m.Groups["code"].Success)
            {
                var content = m.Groups["code"].Value.Trim('`');
                inlines.Add(new Run(content) { FontFamily = MarkdownFlowDoc.MonoFont, Background = CodeBg, Foreground = CodeFg });
            }
            else if (m.Groups["bold"].Success)
            {
                var content = m.Groups["bold"].Value.Trim('*', '_');
                inlines.Add(new Bold(new Run(content)));
            }
            else if (m.Groups["italic"].Success)
            {
                var content = m.Groups["italic"].Value.Trim('*', '_');
                inlines.Add(new Italic(new Run(content)));
            }
            else if (m.Groups["dim"].Success)
            {
                var content = m.Groups["dim"].Value.Trim('~');
                inlines.Add(new Run(content) { Foreground = DimFg });
            }

            pos = m.Index + m.Length;
        }

        if (pos < text.Length)
            inlines.Add(new Run(text[pos..]));
    }
}

/// <summary>TextBlock-based chat bubble body. Renders the chat markdown dialect into plain
/// TextBlocks (see MarkdownBlockRenderer) — no RichTextBox, no BlockUIContainer, no
/// per-block ControlTemplate. Re-renders are coalesced with a 30ms DispatcherTimer, so a
/// burst of tool-result appends during streaming still produces ~1 render per burst rather
/// than a full rebuild per token; each render is incremental (only the delta is parsed and
/// created). Expand/collapse state for embedded collapsibles lives on this instance, so it
/// survives every re-render of the same message.
/// WPF's TextBlock has no native selection support, so selection is implemented here at
/// character granularity: every pointer move is mapped to a character offset inside the
/// hovered TextBlock (TextBlock.GetPositionFromPoint), and each affected block's inline
/// runs are rebuilt so the highlight paints exactly the selected character range while
/// keeping inline formatting (bold, italic, inline code, dim) intact. Ctrl+C copies the
/// selected characters (newline-joined), Ctrl+A selects the whole view. The collapsible
/// headers are excluded from selection so clicking them still toggles.</summary>
public sealed class MarkdownView : StackPanel
{
    public static readonly DependencyProperty TextProperty =
        DependencyProperty.Register(nameof(Text), typeof(string), typeof(MarkdownView),
            new PropertyMetadata(null, OnTextChanged));

    public string? Text
    {
        get => (string?)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    // ── text selection ──
    /// <summary>Tagged onto collapsible header panels so their label/arrow TextBlocks can be
    /// excluded from selection (they're the toggle — clicking them must not start a drag).</summary>
    internal static readonly object HeaderSentinel = new();
    private static readonly Brush SelectionBrush = new SolidColorBrush(Color.FromArgb(110, 70, 110, 220));

    /// <summary>One leaf of a block's inline tree, captured at render time with all styling
    /// resolved (container-level props like a Bold heading's FontSize are folded in), so a
    /// selection rebuild reproduces the original look exactly.</summary>
    private struct Frag
    {
        public string Text;
        public bool Bold;
        public bool Italic;
        public Brush? Fg;
        public Brush? Bg;
        public FontFamily? Font;
        public double? Size;
        public FontWeight? Weight;
        public FontStyle? Style;
    }

    /// <summary>Original inline fragments of each block, captured after every render. The
    /// selection model maps (block, char offset) onto these fragments, so re-rendering a
    /// sub-range for the SelectionBrush never loses the original formatting.</summary>
    private readonly Dictionary<TextBlock, List<Frag>> _orig = new();
    /// <summary>The currently highlighted selection as (block, first char, one-past-last),
    /// kept in top-to-bottom order so Ctrl+C copies documents in order even for reverse drags.</summary>
    private List<(TextBlock Block, int Start, int End)> _sel = new();
    private (TextBlock Block, int Offset)? _selAnchor;
    private (TextBlock Block, int Offset)? _selCurrent;
    private bool _selecting;
    private Point _downPt;
    private Point _downPtInTb;
    private TextBlock? _downTb;

    private string? _lastRendered;
    private string? _pending;
    private DispatcherTimer? _renderTimer;
    private readonly Dictionary<string, bool> _expandStates = new();
    /// <summary>Parser state of the last render, so streaming appends re-render incrementally.</summary>
    private MarkdownBlockRenderer.Resume? _resume;
    /// <summary>Cached visual-tree walk of selectable blocks. Recomputed only when the tree
    /// can change (re-render or a fresh mouse-down), not on every mouse move during a drag.</summary>
    private List<TextBlock>? _selectable;
    private bool _selectableDirty = true;

    public MarkdownView()
    {
        Focusable = true; // so Ctrl+C / Ctrl+A keys can reach the view after a drag
        // Suppress the ScrollViewer's scroll-to-focused-element: taking keyboard focus on
        // mouse-down must not scroll the list (the content would shift under the cursor).
        // The bubble the user clicked is already on screen — focus without scrolling.
        AddHandler(FrameworkElement.RequestBringIntoViewEvent,
            new RequestBringIntoViewEventHandler((_, e) => e.Handled = true), true);
        MouseLeftButtonDown += OnSelectDown;
        MouseMove += OnSelectMove;
        MouseLeftButtonUp += OnSelectUp;
        KeyDown += OnSelectKeyDown;
    }

    private static void OnTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var view = (MarkdownView)d;
        var newText = e.NewValue as string ?? string.Empty;
        if (view._lastRendered == newText) return;

        // A recycled ListBox container now holds a different message (its text is not an
        // append of the one it last displayed): drop the expand/collapse and parser state
        // of the previous message so the new one starts fresh.
        if (view._lastRendered != null
            && (newText.Length < view._lastRendered.Length
                || !newText.StartsWith(view._lastRendered, StringComparison.Ordinal)))
        {
            view._expandStates.Clear();
            view._resume = null;
        }

        view._pending = newText;
        if (view._renderTimer != null) return; // already ticking — it will pick up the newest text

        // Coalesce via dispatcher timer. Leave an existing timer running so fast sequential
        // tool results each render ~30ms after they arrive (streaming one by one), while a
        // burst of appends still collapses into a single render pass.
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(30) };
        timer.Tick += (_, _) =>
        {
            if (view._selecting)
                return; // mid-drag: RenderNow would drop the TextBlocks the drag is anchored
                        // to and reset _sel/_orig, killing the highlight before it can show.
                        // Keep ticking — we'll pick up the pending text once the drag ends.
            if (view._pending != null && view._pending != view._lastRendered)
            {
                view.RenderNow(view._pending);
                return; // more may have queued up while we were rendering; check next tick
            }
            timer.Stop();
            if (view._renderTimer == timer) view._renderTimer = null;
        };
        view._renderTimer = timer;
        timer.Start();
    }

    private void RenderNow(string newText)
    {
        _lastRendered = newText;
        // Disconnect selection state: the blocks below are about to be dropped wholesale
        // along with every TextPointer/offset that refers to them.
        _selecting = false;
        _selAnchor = null;
        _selCurrent = null;
        _downTb = null;
        _sel = new List<(TextBlock, int, int)>();
        _selectable = null;
        _selectableDirty = true;

        // Incremental re-render: when the new text is a pure append of the last render,
        // only the appended/merged blocks are created and touched is filled with them.
        var touched = new List<TextBlock>();
        var (resume, incremental) = MarkdownBlockRenderer.RenderInto(this, newText, _expandStates, _resume, touched);
        _resume = resume;
        if (incremental)
        {
            foreach (var tb in touched)
            {
                var frags = CapturedFrags(tb);
                if (frags.Count > 0)
                    _orig[tb] = frags;
            }
        }
        else
        {
            _orig.Clear();
            foreach (var block in SelectableBlocks())
            {
                var frags = CapturedFrags(block);
                if (frags.Count > 0)
                    _orig[block] = frags;
            }
        }
    }

    /// <summary>Renders any pending text immediately instead of waiting for the coalescing
    /// timer. Called from the tool-call loop after each AppendContent so tool results stream
    /// one by one rather than batching at the end.</summary>
    public void Flush()
    {
        if (_selecting) return; // mid-drag — see the render-timer guard in OnTextChanged
        if (_renderTimer != null)
        {
            _renderTimer.Stop();
            _renderTimer = null;
        }
        if (_pending != null && _pending != _lastRendered)
            RenderNow(_pending);
    }

    // ── character-level text selection ──

    private static bool HasLocal(DependencyObject d, DependencyProperty dp)
        => d.ReadLocalValue(dp) != DependencyProperty.UnsetValue;

    private static FontFamily? InheritedFont(TextElement el, FontFamily? cur) =>
        HasLocal(el, TextElement.FontFamilyProperty) ? el.FontFamily : cur;
    private static double? InheritedSize(TextElement el, double? cur) =>
        HasLocal(el, TextElement.FontSizeProperty) ? el.FontSize : cur;
    private static Brush? InheritedFg(TextElement el, Brush? cur) =>
        HasLocal(el, TextElement.ForegroundProperty) ? el.Foreground : cur;
    private static Brush? InheritedBg(TextElement el, Brush? cur) =>
        HasLocal(el, TextElement.BackgroundProperty) ? el.Background : cur;

    private static List<Frag> CapturedFrags(TextBlock tb)
    {
        var frags = new List<Frag>();
        if (tb.Inlines.Count > 0)
        {
            foreach (var inline in tb.Inlines)
                FlattenInline(inline, false, false, null, null, null, null, frags);
        }
        else if (!string.IsNullOrEmpty(tb.Text))
        {
            frags.Add(new Frag { Text = tb.Text });
        }
        return frags;
    }

    /// <summary>Flattens an inline tree into leaf Frags with styling resolved. Bold/Italic
    /// containers re-create the container decoration on rebuild, so their own local FontSize/
    /// Foreground/Family/Background are folded into every child Frag; a Run's FontWeight and
    /// FontStyle are only captured when not inside the matching container (the container is
    /// what supplies bold/italic to the glyphs).</summary>
    private static void FlattenInline(Inline inline, bool inBold, bool inItalic,
        FontFamily? inFont, double? inSize, Brush? inFg, Brush? inBg, List<Frag> into)
    {
        switch (inline)
        {
            case Run run:
                into.Add(new Frag
                {
                    Text = run.Text ?? string.Empty,
                    Bold = inBold,
                    Italic = inItalic,
                    Fg = inFg ?? (HasLocal(run, TextElement.ForegroundProperty) ? run.Foreground : null),
                    Bg = inBg ?? (HasLocal(run, TextElement.BackgroundProperty) ? run.Background : null),
                    Font = inFont ?? (HasLocal(run, TextElement.FontFamilyProperty) ? run.FontFamily : null),
                    Size = inSize ?? (HasLocal(run, TextElement.FontSizeProperty) ? run.FontSize : null),
                    Weight = !inBold && HasLocal(run, TextElement.FontWeightProperty) ? run.FontWeight : null,
                    Style = !inItalic && HasLocal(run, TextElement.FontStyleProperty) ? run.FontStyle : null
                });
                break;
            case LineBreak:
                into.Add(new Frag { Text = "\n" });
                break;
            case Bold bold:
                foreach (var c in bold.Inlines)
                    FlattenInline(c, true, inItalic, InheritedFont(bold, inFont), InheritedSize(bold, inSize),
                        InheritedFg(bold, inFg), InheritedBg(bold, inBg), into);
                break;
            case Italic italic:
                foreach (var c in italic.Inlines)
                    FlattenInline(c, inBold, true, InheritedFont(italic, inFont), InheritedSize(italic, inSize),
                        InheritedFg(italic, inFg), InheritedBg(italic, inBg), into);
                break;
            case Span span:
                foreach (var c in span.Inlines)
                    FlattenInline(c, inBold, inItalic, InheritedFont(span, inFont), InheritedSize(span, inSize),
                        InheritedFg(span, inFg), InheritedBg(span, inBg), into);
                break;
        }
    }

    private static int FragLength(List<Frag> frags)
    {
        int n = 0;
        foreach (var f in frags) n += f.Text.Length;
        return n;
    }

    private static string FragText(List<Frag> frags)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var f in frags) sb.Append(f.Text);
        return sb.ToString();
    }

    /// <summary>Rebuilds a block's inlines from captured fragments (restoring the original
    /// look when selStart &lt; 0). When [selStart, selEnd) clips a fragment, the fragment is
    /// split into plain/selected/plain runs so only the exact char range gets the
    /// SelectionBrush background.</summary>
    private void ApplyFrags(TextBlock tb, List<Frag> frags, int selStart, int selEnd)
    {
        tb.Inlines.Clear();
        int pos = 0;
        foreach (var f in frags)
        {
            int len = f.Text.Length;
            int fs = pos, fe = pos + len;
            if (f.Text == "\n")
            {
                tb.Inlines.Add(new LineBreak());
            }
            else if (selStart < 0 || fe <= selStart || fs >= selEnd)
            {
                tb.Inlines.Add(BuildFragInline(f, null, false));
            }
            else
            {
                int a = Math.Max(fs, selStart), b = Math.Min(fe, selEnd);
                if (fs < a) tb.Inlines.Add(BuildFragInline(f, f.Text.Substring(0, a - fs), false));
                if (a < b) tb.Inlines.Add(BuildFragInline(f, f.Text.Substring(a - fs, b - a), true));
                if (b < fe) tb.Inlines.Add(BuildFragInline(f, f.Text.Substring(b - fs), false));
            }
            pos = fe;
        }
    }

    /// <summary>Rebuilds one captured fragment as an Inline (a Run wrapped in Bold/Italic as
    /// captured, with every styling property that was live at capture time re-applied — but
    /// only those, so inherited properties keep flowing from the recreated containers).</summary>
    private static Inline BuildFragInline(Frag f, string? text, bool selected)
    {
        var run = new Run(text ?? f.Text);
        if (f.Fg != null) run.Foreground = f.Fg;
        if (selected) run.Background = SelectionBrush;
        else if (f.Bg != null) run.Background = f.Bg;
        if (f.Font != null) run.FontFamily = f.Font;
        if (f.Size != null) run.FontSize = f.Size.Value;
        if (f.Weight != null && !f.Bold) run.FontWeight = f.Weight.Value;
        if (f.Style != null && !f.Italic) run.FontStyle = f.Style.Value;
        if (f.Bold) return new Bold(run);
        if (f.Italic) return new Italic(run);
        return run;
    }

    /// <summary>Returns the captured fragments for a block, capturing them on first use.
    /// Blocks inside a collapsible body that was collapsed at render time aren't in _orig
    /// yet — when the user expands the collapsible and selects, capture them lazily so the
    /// selection machinery sees the same text the block now displays.</summary>
    private List<Frag> OrigFrags(TextBlock tb)
    {
        if (!_orig.TryGetValue(tb, out var frags))
        {
            frags = CapturedFrags(tb);
            _orig[tb] = frags;
        }
        return frags;
    }

    /// <summary>Maps a point inside a block to a character offset in its captured text.</summary>
    private int CharOffsetAt(TextBlock tb, Point ptInTb)
    {
        var frags = OrigFrags(tb);
        int len = FragLength(frags);
        var pos = tb.GetPositionFromPoint(ptInTb, true);
        if (pos == null)
            return ptInTb.Y > tb.RenderSize.Height * 0.5 ? len : 0;
        // Symbol offset avoids allocating the block's text via TextRange on every mouse move.
        // Our blocks are run-only (no LineBreak elements), so symbols == characters.
        int off = tb.ContentStart.GetOffsetToPosition(pos);
        return Math.Clamp(off, 0, len);
    }

    private void OnSelectDown(object sender, MouseButtonEventArgs e)
    {
        _selectableDirty = true; // a header toggle may have changed which blocks are on screen
        Focus();
        _downPt = e.GetPosition(this);
        _downTb = HitTextBlock(_downPt);
        _downPtInTb = _downTb != null ? e.GetPosition(_downTb) : default;
        if (_downTb == null)
        {
            RestoreAndClear();
            return; // empty area — let the click bubble (bubble collapse toggle, ListBox select)
        }
        _selecting = true;
        _selAnchor = null;   // not set until the pointer actually drags (jitter protection)
        _selCurrent = null;
        RestoreAll();        // drop any previous highlight right away
        CaptureMouse();
        e.Handled = true; // don't bubble to the ListBox (bubble select + auto-scroll)
    }

    private void OnSelectMove(object sender, MouseEventArgs e)
    {
        if (!_selecting) return;
        var pt = e.GetPosition(this);
        var tb = HitTextBlock(pt);
        if (tb == null) return;
        int off = CharOffsetAt(tb, e.GetPosition(tb));

        if (_selAnchor == null)
        {
            // Drag hasn't started yet: require real movement so a plain click (with the
            // unavoidable mouse jitter after WM_LBUTTONDOWN) never selects anything.
            if (Math.Abs(pt.X - _downPt.X) < 3 && Math.Abs(pt.Y - _downPt.Y) < 3) return;
            _selAnchor = (_downTb!, CharOffsetAt(_downTb!, _downPtInTb));
            _selCurrent = (tb, off);
        }
        else
        {
            var (cur, curOff) = _selCurrent!.Value;
            if (cur == tb && curOff == off) return;
            _selCurrent = (tb, off);
        }
        UpdateSelection();
        e.Handled = true;
    }

    private void OnSelectUp(object sender, MouseButtonEventArgs e)
    {
        if (_selecting)
        {
            _selecting = false;
            ReleaseMouseCapture();
            e.Handled = true;
        }
    }

    private void OnSelectKeyDown(object sender, KeyEventArgs e)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Control) == 0) return;
        if (e.Key == Key.C)
        {
            var sb = new System.Text.StringBuilder();
            foreach (var (tb, s, en) in _sel)
            {
                string text = FragText(OrigFrags(tb));
                if (s <= en && en <= text.Length)
                {
                    if (sb.Length > 0 && en > s) sb.Append('\n');
                    sb.Append(text, s, en - s);
                }
            }
            if (sb.Length > 0)
            {
                try { Clipboard.SetText(sb.ToString()); } catch { /* clipboard in use — ignore */ }
            }
            e.Handled = true;
        }
        else if (e.Key == Key.A)
        {
            var order = SelectableBlocks();
            if (order.Count > 0)
            {
                _selAnchor = (order[0], 0);
                var last = order[^1];
                int len = FragLength(OrigFrags(last));
                _selCurrent = (last, len);
                UpdateSelection();
            }
            e.Handled = true;
        }
    }

    /// <summary>Every TextBlock rendered by MarkdownBlockRenderer (paragraphs, headers,
    /// list items, code, collapsible bodies) is a selectable unit, in visual/tree order.
    /// Collapsible header text and anything inside a collapsed element are excluded.</summary>
    private List<TextBlock> SelectableBlocks()
    {
        if (_selectableDirty || _selectable == null)
        {
            var result = new List<TextBlock>();
            CollectSelectable(this, result);
            _selectable = result;
            _selectableDirty = false;
        }
        return _selectable;
    }

    internal static void CollectSelectable(DependencyObject node, List<TextBlock> into)
    {
        int n = VisualTreeHelper.GetChildrenCount(node);
        for (int i = 0; i < n; i++)
        {
            var child = VisualTreeHelper.GetChild(node, i);
            if (child is FrameworkElement fe && fe.Visibility != Visibility.Visible)
                continue; // collapsed collapsible body — not on screen, not selectable
            if (child is TextBlock tb)
            {
                if (!IsInHeader(tb) && (!string.IsNullOrEmpty(tb.Text) || tb.Inlines.Count > 0))
                    into.Add(tb);
            }
            CollectSelectable(child, into);
        }
    }

    private TextBlock? HitTextBlock(Point p)
    {
        var hit = VisualTreeHelper.HitTest(this, p)?.VisualHit;
        DependencyObject? cur = hit;
        while (cur != null && cur is not TextBlock && cur != this)
            cur = VisualTreeHelper.GetParent(cur);
        if (cur is TextBlock tb && !IsInHeader(tb))
            return tb;
        return null;
    }

    /// <summary>True if the element sits inside a collapsible header panel (tagged with
    /// HeaderSentinel) — header text is the toggle, never part of a selection.</summary>
    internal static bool IsInHeader(DependencyObject node)
    {
        DependencyObject? cur = node;
        while (cur != null && cur is not MarkdownView)
        {
            if (cur is FrameworkElement fe && ReferenceEquals(fe.Tag, HeaderSentinel))
                return true;
            cur = VisualTreeHelper.GetParent(cur);
        }
        return false;
    }

    /// <summary>Reapplies the highlight for the current anchor/current char pair. Boundary
    /// blocks show a partial char range (DragOffset..end or 0..DragOffset); blocks between
    /// them highlight their full text. Runs in reverse (upward drags) still produce a list
    /// in reading order so Ctrl+C copies the document in order.</summary>
    private void UpdateSelection()
    {
        if (_selAnchor == null || _selCurrent == null)
        {
            RestoreAndClear();
            return;
        }
        var (anchorTb, anchorOff) = _selAnchor.Value;
        var (curTb, curOff) = _selCurrent.Value;
        var order = SelectableBlocks();
        int a = order.IndexOf(anchorTb), c = order.IndexOf(curTb);
        if (a < 0 || c < 0) { RestoreAll(); return; }
        var prev = _sel;
        int lo = Math.Min(a, c), hi = Math.Max(a, c);
        var list = new List<(TextBlock, int, int)>();
        for (int i = lo; i <= hi; i++)
        {
            var tb = order[i];
            var frags = OrigFrags(tb);
            int len = FragLength(frags);
            int s, e;
            if (lo == hi)
            {
                s = Math.Min(anchorOff, curOff);
                e = Math.Max(anchorOff, curOff);
            }
            else if (i == lo)
            {
                s = a == lo ? anchorOff : curOff;
                e = len;
            }
            else if (i == hi)
            {
                s = 0;
                e = a == hi ? anchorOff : curOff;
            }
            else
            {
                s = 0;
                e = len;
            }
            s = Math.Clamp(s, 0, len);
            e = Math.Clamp(e, 0, len);
            if (e <= s) continue;
            // Skip the inline rebuild when this block's selection range didn't move: it's
            // visually identical, and rebuilding every block between anchor and cursor on
            // each mouse move is the biggest per-move alloc (long cross-line drags).
            var ps = -1; var pe = -1;
            foreach (var p in prev) if (ReferenceEquals(p.Block, tb)) { ps = p.Start; pe = p.End; break; }
            if (ps != s || pe != e)
                ApplyFrags(tb, frags, s, e);
            list.Add((tb, s, e));
        }
        // Restore blocks that left the selection in this frame.
        foreach (var (tb, _, _) in prev)
        {
            bool kept = false;
            foreach (var n in list) if (ReferenceEquals(n.Item1, tb)) { kept = true; break; }
            if (!kept && _orig.TryGetValue(tb, out var frags))
                ApplyFrags(tb, frags, -1, -1);
        }
        _sel = list;
    }

    /// <summary>Restores every currently-highlighted block to its original unstyled capture
    /// (clears the SelectionBrush by rebuilding the captured ranges unselected).</summary>
    private void RestoreAll()
    {
        foreach (var (tb, _, _) in _sel)
            if (_orig.TryGetValue(tb, out var frags))
                ApplyFrags(tb, frags, -1, -1);
        _sel = new List<(TextBlock, int, int)>();
    }

    private void RestoreAndClear()
    {
        RestoreAll();
        _selecting = false;
        _selAnchor = null;
        _selCurrent = null;
    }
}

/// <summary>Parses the chat markdown dialect into plain TextBlocks appended to a StackPanel.
/// Covers reasoning blocks, defensive line breaks before fences/<<COLLAPSE>>, code fences,
/// collapsible blocks, headers, bullet and numbered lists, blank-line spacers — emitting
/// lightweight TextBlocks instead of FlowDocument blocks, with inline styling via the shared
/// AppendInlines/InlineRegex so the rendered text stays visually identical while the visual
/// tree and re-render cost shrink. Re-renders are incremental: a new text that is a pure
/// append of the last render only creates blocks for the delta (see Resume).</summary>
internal static class MarkdownBlockRenderer
{
    private static readonly Brush MsgFg = new SolidColorBrush(Color.FromArgb(255, 0xE0, 0xE0, 0xE8));

    // Compile once, not per line per render: Regex.Match(input, pattern) with an inline
    // pattern string builds a brand-new compiled Regex for every call — this renderer
    // runs it once per source line on every re-render of a streaming message.
    private static readonly Regex HeaderRegex = new(@"^(#{1,6})\s+(.*)$", RegexOptions.Compiled);
    private static readonly Regex BulletRegex = new(@"^[-*+]\s+(.*)$", RegexOptions.Compiled);
    private static readonly Regex NumberedRegex = new(@"^\d+\.\s+(.*)$", RegexOptions.Compiled);
    private static readonly Regex GluedFenceRegex = new(@"(?<!^)(?<!\n)(```)", RegexOptions.Compiled);
    private static readonly Regex GluedCollapseRegex = new(@"(?<!^)(?<!\n)(<<COLLAPSE)", RegexOptions.Compiled);
    private static readonly Regex CollapseTagRegex = new(@"^<<COLLAPSE(?<mono>-MONO)?:(?<header>.*)>>$", RegexOptions.Compiled);
    private static readonly MatchEvaluator WrapReasoning = m =>
        $"<<COLLAPSE-MONO:Thinking...>>{m.Groups[1].Value}<</COLLAPSE>>";

    /// <summary>Parser state carried between renders of the same message. Streaming appends
    /// re-render through the same state machine, so only the delta is parsed and only the
    /// new/changed blocks are created instead of rebuilding the whole message every burst.</summary>
    internal sealed class Resume
    {
        public string PrevPre = "";  // preprocessed text of the last render (prefix check)
        public int PrevLen;          // PrevPre.Length
        public bool PrevEndsNewline; // last render ended on a line boundary
        public bool LastSpacerIsTrailing; // last block was the spacer of a trailing empty line
        public int Ordinal;          // stable collapsible identity counter
        public bool InCode;          // inside an open code fence
        public List<string>? CodeLines;
        public bool InCollapse;      // inside an open collapsible
        public List<string>? CollapseLines;
        public string? CollapseHeader;
        public bool CollapseMono;
        public bool? ListBullet;     // true = bullet list, false = numbered, null = none
        public int ListNumber;
        public string LastKind = ""; // para | listitem | heading | spacer | collapsible | code | collapbody
        public TextBlock? LastPara;  // last paragraph/list-item block (mid-line continuation target)
    }

    internal static (Resume? Resume, bool Incremental) RenderInto(
        StackPanel target, string text, Dictionary<string, bool> expandStates, Resume? resume, List<TextBlock>? touched)
    {
        if (string.IsNullOrEmpty(text))
        {
            target.Children.Add(new TextBlock()); // keep the bubble's minimum height
            return (null, false);
        }

        var pre = GluedCollapseRegex.Replace(GluedFenceRegex.Replace(
            MarkdownFlowDoc.ReasoningRegex.Replace(text, WrapReasoning), "\n$1"), "\n$1");

        // Fast path: the new preprocessed text is a strict append of the last render's.
        // Only the delta lines are parsed with the saved parser state, so a streaming burst
        // creates elements only for the newly arrived content. Any case where the old text
        // is not a pure prefix (mid-<reasoning> wrap, glued markers moving lines, a recycled
        // container now holding different content) falls through to the full re-render.
        if (resume != null && pre.Length > resume.PrevLen && pre.StartsWith(resume.PrevPre, StringComparison.Ordinal))
        {
            var delta = pre.Substring(resume.PrevLen).Replace("\r\n", "\n");
            var deltaLines = delta.Split('\n');
            if (resume.PrevEndsNewline)
            {
                // The old text's trailing empty line rendered as a spacer; in the merged
                // text that line becomes deltaLines[0], so drop a spacer that represented
                // only that artifact so the delta lines parse exactly as a full re-render.
                if (deltaLines[0].Length > 0 && resume.LastSpacerIsTrailing && target.Children.Count > 0)
                    target.Children.RemoveAt(target.Children.Count - 1);
                foreach (var dl in deltaLines)
                    ProcessLine(target, expandStates, resume, dl, touched);
            }
            else
            {
                // Mid-line append: merge the head into the construct the previous line
                // ended inside, then process the remaining delta lines.
                if (deltaLines[0].Length > 0)
                {
                    if (resume.InCollapse)
                    {
                        if (resume.CollapseLines!.Count > 0) resume.CollapseLines[^1] += deltaLines[0];
                        else resume.CollapseLines.Add(deltaLines[0]);
                    }
                    else if (resume.InCode)
                    {
                        // A fence line absorbs any same-line continuation (the renderer
                        // discards content glued to a fence marker) — merge only inside
                        // the accumulated code lines.
                        if (resume.CodeLines!.Count > 0) resume.CodeLines[^1] += deltaLines[0];
                    }
                    else if (resume.LastKind == "para" || resume.LastKind == "listitem")
                    {
                        // Rebuild the last paragraph from its full line + continuation so
                        // inline markers split across the boundary style identically to a
                        // full render.
                        var tb = resume.LastPara!;
                        var merged = tb.Text + deltaLines[0];
                        tb.Inlines.Clear();
                        MarkdownFlowDoc.AppendInlines(tb.Inlines, merged);
                        TrackTouched(touched, tb);
                    }
                }
                if (resume != null)
                {
                    for (int i = 1; i < deltaLines.Length; i++)
                        ProcessLine(target, expandStates, resume, deltaLines[i], touched);
                }
            }
            if (resume != null)
            {
                FlushResidual(target, expandStates, resume, touched);
                SaveState(resume, pre);
                return (resume, true);
            }
        }

        target.Children.Clear();
        var r = new Resume { PrevPre = pre, PrevLen = pre.Length, PrevEndsNewline = pre.EndsWith('\n') };
        foreach (var rawLine in pre.Replace("\r\n", "\n").Split('\n'))
            ProcessLine(target, expandStates, r, rawLine, null);
        FlushResidual(target, expandStates, r, null);
        SaveState(r, pre);
        return (r, false);
    }

    private static void SaveState(Resume r, string pre)
    {
        r.PrevPre = pre;
        r.PrevLen = pre.Length;
        r.PrevEndsNewline = pre.EndsWith('\n');
        // A trailing empty line after the final '\n' is a split artifact, not a real blank
        // line — its spacer is dropped when the delta merges into it (see RenderInto).
        r.LastSpacerIsTrailing = pre.EndsWith('\n') && !pre.EndsWith("\n\n") && pre.Length > 1 && r.LastKind == "spacer";
    }

    private static string NextKey(Resume r, string header) => $"{r.Ordinal++}:{header}";

    private static void TrackTouched(List<TextBlock>? touched, UIElement el)
    {
        if (touched == null) return;
        if (el is TextBlock tb && !MarkdownView.IsInHeader(tb) && (!string.IsNullOrEmpty(tb.Text) || tb.Inlines.Count > 0))
            touched.Add(tb);
        MarkdownView.CollectSelectable(el, touched);
    }

    private static TextBlock MakeTextBlock()
    {
        return new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            FontFamily = MarkdownFlowDoc.UiFont,
            FontSize = 12,
            Foreground = MsgFg,
            UseLayoutRounding = true,
            SnapsToDevicePixels = true
        };
    }

    private static void AddParagraph(StackPanel target, Resume r, string line, List<TextBlock>? touched)
    {
        var tb = MakeTextBlock();
        MarkdownFlowDoc.AppendInlines(tb.Inlines, line);
        target.Children.Add(tb);
        TrackTouched(touched, tb);
        r.LastKind = "para";
        r.LastPara = tb;
    }

    private static void AddSpacer(StackPanel target)
    {
        target.Children.Add(new TextBlock { Margin = new Thickness(0, 0, 0, 4) });
    }

    private static void ProcessLine(StackPanel target, Dictionary<string, bool> expandStates,
        Resume r, string line, List<TextBlock>? touched)
    {
        if (r.InCollapse)
        {
            var endIdx = line.IndexOf("<</COLLAPSE>>", StringComparison.Ordinal);
            if (endIdx >= 0)
            {
                var restOfLine = line.Substring(0, endIdx);
                if (!string.IsNullOrEmpty(restOfLine))
                    r.CollapseLines!.Add(restOfLine);

                r.ListBullet = null;
                r.ListNumber = 0;
                r.CollapseLines ??= new List<string>();
                var bodyText = string.Join("\n", r.CollapseLines).Trim('\n');
                var header1 = r.CollapseHeader ?? "Details";
                var block = MarkdownFlowDoc.BuildCollapsibleBlockLite(NextKey(r, header1), expandStates, header1, bodyText, r.CollapseMono);
                target.Children.Add(block);
                TrackTouched(touched, block);
                r.InCollapse = false;
                r.CollapseLines = null;
                r.CollapseHeader = null;
                r.LastKind = "collapsible";
                r.LastPara = null;

                var afterEnd = line.Substring(endIdx + "<</COLLAPSE>>".Length);
                if (!string.IsNullOrEmpty(afterEnd))
                    AddParagraph(target, r, afterEnd, touched);
                return;
            }
            r.CollapseLines!.Add(line);
            r.LastKind = "collapbody";
            r.LastPara = null;
            return;
        }

        var collapseStartIdx = line.IndexOf("<<COLLAPSE", StringComparison.Ordinal);
        if (collapseStartIdx >= 0)
        {
            var endTagIdx = line.IndexOf(">>", collapseStartIdx, StringComparison.Ordinal);
            if (endTagIdx > collapseStartIdx)
            {
                var tagContent = line.Substring(collapseStartIdx, endTagIdx + 2 - collapseStartIdx);
                var match = CollapseTagRegex.Match(tagContent);
                if (match.Success)
                {
                    r.InCollapse = true;
                    r.CollapseLines = new List<string>();
                    r.CollapseHeader = match.Groups["header"].Value.Trim();
                    r.CollapseMono = match.Groups["mono"].Success;
                    r.LastKind = "collapbody";
                    r.LastPara = null;

                    var restOfLine = line.Substring(endTagIdx + 2);
                    if (!string.IsNullOrEmpty(restOfLine))
                    {
                        var inlineCloseIdx = restOfLine.IndexOf("<</COLLAPSE>>", StringComparison.Ordinal);
                        if (inlineCloseIdx >= 0)
                        {
                            var bodyPart = restOfLine.Substring(0, inlineCloseIdx);
                            if (!string.IsNullOrEmpty(bodyPart))
                                r.CollapseLines.Add(bodyPart);

                            r.ListBullet = null;
                            r.ListNumber = 0;
                            var bodyText2 = string.Join("\n", r.CollapseLines).Trim('\n');
                            var header2 = r.CollapseHeader ?? "Details";
                            var block2 = MarkdownFlowDoc.BuildCollapsibleBlockLite(NextKey(r, header2), expandStates, header2, bodyText2, r.CollapseMono);
                            target.Children.Add(block2);
                            TrackTouched(touched, block2);
                            r.InCollapse = false;
                            r.CollapseLines = null;
                            r.CollapseHeader = null;
                            r.LastKind = "collapsible";

                            var afterEnd2 = restOfLine.Substring(inlineCloseIdx + "<</COLLAPSE>>".Length);
                            if (!string.IsNullOrEmpty(afterEnd2))
                                AddParagraph(target, r, afterEnd2, touched);
                        }
                        else
                        {
                            r.CollapseLines.Add(restOfLine);
                        }
                    }
                    return;
                }
            }
        }

        if (line.TrimStart().StartsWith("```"))
        {
            if (r.CodeLines == null)
            {
                r.InCode = true;
                r.CodeLines = new List<string>();
                r.LastKind = "code";
                r.LastPara = null;
            }
            else
            {
                r.ListBullet = null;
                r.ListNumber = 0;
                var codeText = string.Join("\n", r.CodeLines);
                var block = MarkdownFlowDoc.BuildCollapsibleBlockLite(NextKey(r, "Code"), expandStates, "Code", codeText, monospaceBody: true);
                target.Children.Add(block);
                TrackTouched(touched, block);
                r.InCode = false;
                r.CodeLines = null;
                r.LastKind = "collapsible";
                r.LastPara = null;
            }
            return;
        }
        if (r.InCode)
        {
            r.CodeLines!.Add(line);
            r.LastKind = "code";
            r.LastPara = null;
            return;
        }

        var trimmed = line.TrimStart();

        var headerMatch = HeaderRegex.Match(trimmed);
        if (headerMatch.Success)
        {
            r.ListBullet = null;
            r.ListNumber = 0;
            var level = headerMatch.Groups[1].Value.Length;
            var tb = MakeTextBlock();
            tb.FontSize = level switch { 1 => 20, 2 => 18, 3 => 16, 4 => 14, _ => 13 };
            tb.FontWeight = FontWeights.Bold;
            tb.Margin = new Thickness(0, 6, 0, 4);
            MarkdownFlowDoc.AppendInlines(tb.Inlines, headerMatch.Groups[2].Value);
            target.Children.Add(tb);
            TrackTouched(touched, tb);
            r.LastKind = "heading";
            r.LastPara = null;
            return;
        }

        var bulletMatch = BulletRegex.Match(trimmed);
        var numberedMatch = NumberedRegex.Match(trimmed);
        if (bulletMatch.Success || numberedMatch.Success)
        {
            var isBullet = bulletMatch.Success;
            if (r.ListBullet != isBullet)
            {
                r.ListBullet = isBullet;
                r.ListNumber = 0;
            }
            r.ListNumber++;
            var itemText = bulletMatch.Success ? bulletMatch.Groups[1].Value : numberedMatch.Groups[1].Value;
            var itemTb = MakeTextBlock();
            itemTb.Margin = new Thickness(0, 2, 0, 2);
            itemTb.Inlines.Add(new Run(isBullet ? "\u2022 " : $"{r.ListNumber}. "));
            MarkdownFlowDoc.AppendInlines(itemTb.Inlines, itemText);
            target.Children.Add(itemTb);
            TrackTouched(touched, itemTb);
            r.LastKind = "listitem";
            r.LastPara = itemTb;
            return;
        }

        r.ListBullet = null;
        r.ListNumber = 0;

        if (string.IsNullOrWhiteSpace(line))
        {
            // Collapse consecutive blank lines into a single spacer (the old FlowDocument
            // path did the same for empty paragraphs).
            if (target.Children.Count > 0
                && target.Children[^1] is TextBlock { Inlines.Count: 0 } last
                && string.IsNullOrEmpty(last.Text)
                && last.Margin.Bottom == 4)
                return;
            AddSpacer(target);
            r.LastKind = "spacer";
            r.LastPara = null;
            return;
        }

        AddParagraph(target, r, line, touched);
    }

    private static void FlushResidual(StackPanel target, Dictionary<string, bool> expandStates, Resume r, List<TextBlock>? touched)
    {
        if (r.InCode)
        {
            // A code fence that never closed ends the message as a plain code block.
            var tb = MakeTextBlock();
            tb.FontFamily = MarkdownFlowDoc.MonoFont;
            tb.Background = MarkdownFlowDoc.CodeBg;
            tb.Foreground = MarkdownFlowDoc.CodeFg;
            tb.Padding = new Thickness(6);
            tb.Text = string.Join("\n", r.CodeLines!);
            target.Children.Add(tb);
            TrackTouched(touched, tb);
            r.InCode = false;
            r.CodeLines = null;
            r.LastKind = "code";
            r.LastPara = null;
        }
        if (r.InCollapse)
        {
            var headerTrailing = r.CollapseHeader ?? "Details";
            var block = MarkdownFlowDoc.BuildCollapsibleBlockLite(NextKey(r, headerTrailing), expandStates, headerTrailing,
                string.Join("\n", r.CollapseLines!), r.CollapseMono, startExpanded: true);
            target.Children.Add(block);
            TrackTouched(touched, block);
            r.InCollapse = false;
            r.CollapseLines = null;
            r.CollapseHeader = null;
            r.LastKind = "collapsible";
            r.LastPara = null;
        }

        if (target.Children.Count == 0)
            target.Children.Add(new TextBlock());
    }
}

public sealed class MainWindow : Window
{
    private AppSettings _settings = new();
    private string _configPath = "";
    private bool _uiReady;
    private KoboldCppProcess? _koboldProcess;
    private KoboldCppClient? _koboldClient;
    private OpenRouterClient? _openRouterClient;
    private CancellationTokenSource? _cts;
    private bool _isGenerating;
    private bool _isKoboldRunning;
    private bool _isKoboldStarting;
    private KoboldMode _currentMode = KoboldMode.Image;
    private TaskCompletionSource? _koboldReadyTcs;
    /// Set the instant koboldcpp's own stdout announces its HTTP server is listening.
    /// Lets WaitForKoboldReadyAsync stop blind-polling the port with real HTTP requests
    /// while the model is still loading (each failed poll during that window used to throw
    /// an HttpRequestException/TaskCanceledException — harmless since it's caught, but it
    /// spammed the debugger's first-chance exception log every second for however long the
    /// model took to load).
    private TaskCompletionSource? _koboldStdoutReadyTcs;

    private static readonly Brush Fg = AppTheme.F(new SolidColorBrush(Color.FromRgb(230, 230, 238)));
    private static readonly Brush FgDim = AppTheme.F(new SolidColorBrush(Color.FromRgb(150, 150, 162)));
    private static readonly Brush FgMuted = AppTheme.F(new SolidColorBrush(Color.FromRgb(100, 100, 112)));
    private static readonly Brush Border = AppTheme.F(new SolidColorBrush(Color.FromRgb(55, 55, 64)));

    private static readonly Brush BrGreen = AppTheme.F(new SolidColorBrush(Color.FromRgb(60, 160, 60)));
    private static readonly Brush BrBlue = AppTheme.F(new SolidColorBrush(Color.FromRgb(60, 60, 160)));
    private static readonly Brush Success = AppTheme.F(new SolidColorBrush(Color.FromRgb(80, 200, 120)));
    private static readonly Brush BrErrorBg = AppTheme.F(new SolidColorBrush(Color.FromRgb(24, 16, 16)));
    private static readonly Brush BrErrorFg = AppTheme.F(new SolidColorBrush(Color.FromRgb(230, 180, 180)));
    private static readonly Brush BrErrorBorder = AppTheme.F(new SolidColorBrush(Color.FromRgb(55, 35, 35)));
    private static readonly Brush BrDarkGreen = AppTheme.F(new SolidColorBrush(Color.FromRgb(0, 90, 30)));
    private static readonly Brush BrRubyRed = AppTheme.F(new SolidColorBrush(Color.FromRgb(180, 25, 40)));
    private static readonly Brush BrDodgeBlue = AppTheme.F(new SolidColorBrush(Color.FromRgb(30, 144, 255)));
    private static readonly Brush _sideHoverBg = AppTheme.F(new SolidColorBrush(Color.FromRgb(45, 45, 53)));
    private static readonly Brush AccentBg = AppTheme.F(new SolidColorBrush(Color.FromRgb(30, 45, 85)));
    private static readonly FontFamily FontSegoe = new FontFamily("Segoe UI Variable, Segoe UI");
    private static readonly FontFamily FontIcon = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets, Segoe UI Variable, Segoe UI");
    private static readonly int[] _btnToTab = [3, 0, 1, 2, 4];

    // Consecutive read_file dedup tracking
    private string? _lastReadFilePath;
    private string? _lastReadLabel;
    private int _consecutiveReadCount;
    private int _lastReadInsertPos = -1;
    private bool _plannerPopulatedNotes;

    private static readonly Dictionary<string, string> ProviderUrls = new()
    {
        { "OpenRouter", "https://openrouter.ai/api/v1/" },
        { "Chutes.ai", "https://api.chutes.ai/v1/" },
        { "Requesty", "https://router.requesty.ai/v1/" },
        { "Custom", "" }
    };

    internal static readonly Style SliderStyle = BuildSliderStyle();

    internal static Style BuildSliderStyle()
    {
        var xaml = @"<Style xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'
       xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'
       TargetType='Slider'>
  <Setter Property='Template'>
    <Setter.Value>
      <ControlTemplate TargetType='Slider'>
        <Grid Height='24'>
          <Border Background='#2A2A34' Height='6' VerticalAlignment='Center' CornerRadius='0'/>
          <Track Name='PART_Track'>
            <Track.DecreaseRepeatButton>
              <RepeatButton Command='Slider.DecreaseLarge'>
                <RepeatButton.Template>
                  <ControlTemplate TargetType='RepeatButton'><Border/></ControlTemplate>
                </RepeatButton.Template>
              </RepeatButton>
            </Track.DecreaseRepeatButton>
            <Track.Thumb>
              <Thumb Name='Thumb' Width='16' Height='16' Cursor='Hand'>
                <Thumb.Template>
                  <ControlTemplate TargetType='Thumb'>
                    <Ellipse Fill='#6490FF' Width='16' Height='16'/>
                  </ControlTemplate>
                </Thumb.Template>
              </Thumb>
            </Track.Thumb>
            <Track.IncreaseRepeatButton>
              <RepeatButton Command='Slider.IncreaseLarge'>
                <RepeatButton.Template>
                  <ControlTemplate TargetType='RepeatButton'><Border/></ControlTemplate>
                </RepeatButton.Template>
              </RepeatButton>
            </Track.IncreaseRepeatButton>
          </Track>
        </Grid>
      </ControlTemplate>
    </Setter.Value>
  </Setter>
</Style>";
        return (Style)System.Windows.Markup.XamlReader.Parse(xaml);
    }

    private TextBox _promptBox = null!;
    private TextBox _negativeBox = null!;
    private TextBox _seedBox = null!;
    private CheckBox _randomSeedCheck = null!;
    private ListBox _thumbnailBox = null!;
    private ColumnDefinition _thumbColumn = null!;
    private TextBox _batchCountBox = null!;
    private Slider _widthSlider = null!;
    private Label _widthLabel = null!;
    private Slider _heightSlider = null!;
    private Label _heightLabel = null!;
    private Slider _stepsSlider = null!;
    private Label _stepsLabel = null!;
    private Slider _cfgSlider = null!;
    private Label _cfgLabel = null!;
    private Button _generateBtn = null!;
    private Button _textSendBtn = null!;
    private ProgressBar _progressBar = null!;
    private Label _progressLabel = null!;
    private Image _resultImage = null!;
    private TextBlock _placeholder = null!;
    private TextBox _logBox = null!;
    private Label _statusLabel = null!;
    private Label _tpsLabel = null!;

    private double _zoomLevel = 1.0;
    private ScaleTransform _zoomTransform = null!;
    private Label _zoomLabel = null!;
    private ListBox _refImageList = null!;
    private readonly List<string> _refImagePaths = new();
    private Border _loadingOverlay = null!;
    private TextBlock _overlayLabel = null!;
    private bool _isShuttingDown;

    private static string KoboldCppDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "PromptWhizz");
    private Slider _denoisingSlider = null!;
    private Label _denoisingLabel = null!;
    private ScrollViewer _imageScrollViewer = null!;
    private ComboBox _modeCombo = null!;
    private bool _isDragging;
    private Point _dragStart;
    private double _dragHOffset, _dragVOffset;
    private bool _lastKcppWasProgress;

    private TabControl _tabControl = null!;
    private TextBox _videoPromptBox = null!;
    private TextBox _videoNegativeBox = null!;
    private Slider _videoFramesSlider = null!;
    private Label _videoFramesLabel = null!;
    private Slider _videoFpsSlider = null!;
    private Label _videoFpsLabel = null!;
    private Slider _videoStepsSlider = null!;
    private Label _videoStepsLabel = null!;
    private Slider _videoCfgSlider = null!;
    private Label _videoCfgLabel = null!;
    private Slider _videoWidthSlider = null!;
    private Label _videoWidthLabel = null!;
    private Slider _videoHeightSlider = null!;
    private Label _videoHeightLabel = null!;
    private CheckBox _videoRandomSeedCheck = null!;
    private TextBox _videoSeedBox = null!;
    private MediaElement _videoPlayer = null!;
    private Button _videoPlayBtn = null!;
    private Slider _videoSeekSlider = null!;
    private Label _videoTimeLabel = null!;
    private Slider _videoVolumeSlider = null!;
    private System.Windows.Threading.DispatcherTimer _videoTimer = null!;

    private Button _videoSaveBtn = null!;

    private ChatConversationControl _chatControl = null!;
    private TextBox _chatInputBox = null!;
    private readonly ObservableCollection<ChatMessage> _chatHistory = new();
    private readonly List<string> _textAttachments = new();
    private WrapPanel _textAttachPanel = null!;

    private TextBox _audioResultBox = null!;
    private Label _audioStatusLabel = null!;
    private Button _audioCaptureBtn = null!;
    private CheckBox _audioOverlayCheck = null!;
    private CheckBox _audioToPromptCheck = null!;
    private Slider _audioFontSlider = null!;
    private Label _audioFontLabel = null!;
    private Slider _audioOpacitySlider = null!;
    private Label _audioOpacityLabel = null!;
    private ComboBox _audioFontCombo = null!;
    private ComboBox _audioColorCombo = null!;
    private CheckBox _audioTranslateCheck = null!;
    private ComboBox _textEnableThinking = null!;
    private ComboBox _textThinkingEffort = null!;
    private ComboBox _textCompactPrompt = null!;
    private ComboBox _textNoCertify = null!;
    private ComboBox _textAgenticNoShift = null!;
    private CheckBox _toolsCheck = null!;
    private CheckBox _debugCheck = null!;
    private ComboBox _textAgenticWorkflow = null!;
    private ComboBox _textConfirmMode = null!;
    private Grid _confirmRow = null!;
    private ComboBox _backendModeCombo = null!;
    private ComboBox _externalProviderCombo = null!;
    private Grid _externalProviderRow = null!;
    private ComboBox _externalModelCombo = null!;
    private Grid _externalModelRow = null!;
    private Grid _openRouterModelRow = null!;
    private Grid _openRouterKeyRow = null!;
    private Button _refreshModelsBtn = null!;
    private ComboBox _modelFilterCombo = null!;
    private List<OpenRouterModelInfo>? _allOpenRouterModels;
    private TextBox _openRouterApiKeyBox = null!;
    private TextBox _customApiUrlBox = null!;
    private Grid _customApiUrlRow = null!;
    private TextBox _textSystemPromptBox = null!;
    private Slider _textContextSlider = null!;
    private TextBox _textContextValue = null!;
    private TextBox _textBatchSizeBox = null!;
    private TextBox _textBlasBatchBox = null!;
    private TextBox _textGpuLayersBox = null!;
    private Slider _textTopKSlider = null!;
    private TextBox _textTopKValue = null!;
    private Slider _textTempSlider = null!;
    private TextBox _textTempValue = null!;
    private Slider _textTopPSlider = null!;
    private TextBox _textTopPValue = null!;
    private Slider _textRepPenSlider = null!;
    private TextBox _textRepPenValue = null!;
    private TextBox _textTimeoutBox = null!;
    private Slider _maxIterSlider = null!;
    private TextBox _maxIterValue = null!;
    private Slider _stallNudgeSlider = null!;
    private TextBox _stallNudgeValue = null!;
    private Slider _stallLockoutSlider = null!;
    private TextBox _stallLockoutValue = null!;
    private Slider _readNudgeSlider = null!;
    private TextBox _readNudgeValue = null!;
    private Slider _readHardStopSlider = null!;
    private TextBox _readHardStopValue = null!;
    private TextBox _plannerModelBox = null!;
    private TextBox _plannerTemplateBox = null!;
    private ComboBox _plannerEnabledCombo = null!;
    private ComboBox _thumbPreviewCombo = null!;
    private (string Name, Color Color)[] _overlayColorPresets = null!;
    private bool _syncingOverlay;
    private ListBox _audioHistoryList = null!;
    private ScrollViewer? _audioScrollViewer;
    private readonly ObservableCollection<ChatMessage> _audioHistory = new();
    private readonly List<ScreenOcrOverlay> _screenOcrOverlays = new();
    private Border _rightImageBorder = null!;
    private TextBox _maxHistoryBox = null!;
    private TextBox _textHistoryBox = null!;
    private int _maxHistoryCount = 50;
    private StreamingTranscriber? _transcriber;
    private TranscriptionOverlay? _transcriptionOverlay;
    private ComboBox _audioModeCombo = null!;
    private ComboBox _audioSourceCombo = null!;
    private UIElement _audioLivePanel = null!;
    private UIElement _audioTranscribePanel = null!;
    private UIElement _audioVoiceClonePanel = null!;
    private UIElement _audioMusicPanel = null!;
    private TextBox _voiceRefAudioBox = null!;
    private ListBox _voiceRefHistoryList = null!;
    private TextBox _voiceTextInput = null!;
    private Label _voiceStatusLabel = null!;
    private Button _voiceCloneBtn = null!;
    private Button _voiceRecordBtn = null!;
    private string? _voiceResultPath;
    private WaveInEvent? _voiceRecorder;
    private WaveFileWriter? _voiceRecorderWriter;
    private readonly object _voiceRecorderLock = new();
    private string? _voiceRecorderFile;
    private bool _voiceIsRecording;
    private CheckBox _voiceWatchCheck = null!;
    private TextBox _voiceWatchPathBox = null!;
    private FileSystemWatcher? _voiceFileWatcher;
    private int _voiceWatchLineCount;
    private DateTime _voiceWatchLastChange;
    private System.Threading.Timer? _voiceWatchDebounce;
    private bool _voiceWatchSuppressCheck;

    private string _visionImagePath = "";
    private ComboBox _visionTargetLang = null!;
    private readonly ObservableCollection<ChatMessage> _visionChatHistory = new();
    private ChatConversationControl _visionChatControl = null!;
    private Grid _visionChatPanel = null!;
    private TextBox _visionChatInput = null!;
    private ListBox _liveOverlayList = null!;
    private Slider _visionFontSlider = null!;
    private Label _visionFontLabel = null!;
    private Slider _visionOpacitySlider = null!;
    private Label _visionOpacityLabel = null!;
    private ComboBox _visionFontCombo = null!;
    private ComboBox _visionTextColorCombo = null!;
    private ComboBox _visionBgColorCombo = null!;
    private bool _visionSyncingOverlay;
    private int _liveOverlayCounter;
    private readonly GlobalHotkeyManager _hotkeyManager = new();
    private readonly Dictionary<int, ScreenOcrOverlay> _overlayById = new();

    private readonly List<DetectedFile> _detectedFiles = new();

    private long _tpsTimestamp;

    private readonly List<string> _allThumbFiles = new();
    private int _thumbLoadedCount;
    private const int ThumbBatchSize = 10;
    private readonly Func<StackPanel>?[] _tabBuilders = new Func<StackPanel>?[5];
    private readonly bool[] _tabBuilt = new bool[5];
    private readonly Button[] _sideModeBtns = new Button[5];
    private bool _thumbnailsLoaded;

    // Session state
    private FrameworkElement _textInnerTabs = null!;
    private ListBox _sessionList = null!;
    private TextBox _sessionTitleBox = null!;
    private TextBox _sessionProjectBox = null!;
    private AgentSession? _activeSession;
    private readonly ObservableCollection<AgentSession> _sessions = new();

    public MainWindow()
    {
        Title = "Promptar";
        Width = 1200;
        Height = 800;
        MinWidth = 900;
        MinHeight = 600;
        Background = Bg;
        Foreground = Fg;
        FontFamily = new FontFamily("Segoe UI");
        FontSize = 13;
        WindowStyle = WindowStyle.SingleBorderWindow;
        ResizeMode = ResizeMode.CanResizeWithGrip;

        Loaded += OnMainWindowLoaded;
        Closing += OnMainWindowClosing;
        _hotkeyManager.HotkeyTriggered += (id) => Dispatcher.BeginInvoke(() => OnHotkeyTriggered(id));

        DarkenScrollbarResources(this.Resources);
        if (Application.Current != null)
            DarkenScrollbarResources(Application.Current.Resources);

        _configPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "StableDiffusionStudio", "config.json");

        LoadConfig(_configPath);
        Content = BuildRootGrid();
        LoadIcon();
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
                bw.Write((short)0);
                bw.Write((short)1);
                bw.Write((short)1);
                bw.Write((byte)0);
                bw.Write((byte)0);
                bw.Write((byte)0);
                bw.Write((byte)0);
                bw.Write((short)1);
                bw.Write((short)32);
                bw.Write(pngBytes.Length);
                bw.Write(22);
                bw.Write(pngBytes);
                bw.Flush();
                ms.Position = 0;
                Icon = BitmapFrame.Create(ms, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            }
        }
        catch { }
    }

    private async void OnMainWindowLoaded(object _, RoutedEventArgs _2)
    {
        EnableDarkTitleBar();
        ApplySettingsToUI();
        _uiReady = true;
        LoadSessionsFromSettings();
        Directory.CreateDirectory(KoboldCppDirectory);
        if (!string.IsNullOrWhiteSpace(_settings.OutputPath))
            Directory.CreateDirectory(_settings.OutputPath);

        var exePath = Path.Combine(KoboldCppDirectory, "koboldcpp.exe");
        if (!File.Exists(exePath))
        {
            ShowLoadingOverlay();
            _overlayLabel.Text = "Downloading KoboldCpp...";
            var downloaded = await TryDownloadKoboldCppAsync();
            if (!downloaded) { HideLoadingOverlay(); return; }
        }

        _settings.KoboldExePath = exePath;

        bool hasCuda = HasNvidiaGpu() || HasNvidiaSmi() ||
            File.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "nvcuda.dll"));
        if (!hasCuda && _settings.Backend == "cuda")
        {
            _settings.Backend = "auto";
            AutoSaveConfig();
        }

        HideLoadingOverlay();
        Log("Ready. Click Generate or Send to start KoboldCpp with the appropriate model for the active tab.");
        ApplyDarkScrollbarOverride();

        // Populate external model list on boot if using external mode
        if (_settings.BackendMode == "external")
            _ = PopulateModelsForProviderAsync(_settings.ExternalProvider);
    }

    private async void OnToggleCapture(object sender, RoutedEventArgs e)
    {
        if (_transcriber?.IsRunning == true)
        {
            StopCapture();
            return;
        }

        var requiredMode = KoboldMode.Audio;
        if (!await EnsureKoboldModeReadyAsync(requiredMode))
            return;

        if (_audioOverlayCheck.IsChecked == true && (_transcriptionOverlay == null || !_transcriptionOverlay.IsVisible))
        {
            _transcriptionOverlay?.Close();
            _transcriptionOverlay = new TranscriptionOverlay();
            WireOverlayEvents(_transcriptionOverlay);
            _transcriptionOverlay.Closed += (_, _) => _audioOverlayCheck.IsChecked = false;
            _transcriptionOverlay.Show();
        }
        else if (_audioOverlayCheck.IsChecked == false && _transcriptionOverlay != null)
        {
            _transcriptionOverlay.Close();
            _transcriptionOverlay = null;
        }

        _transcriber = new StreamingTranscriber(_koboldClient!, useMicrophone: _audioSourceCombo.SelectedIndex == 0, chunkDurationMs: 8000,
            onChunk: text =>
            {
                Dispatcher.BeginInvoke(() =>
                {
                    _audioResultBox.Text = text;
                    _transcriptionOverlay?.AppendText(text);
                    _audioHistory.Add(new ChatMessage { Role = "audio", Content = text });
                    while (_audioHistory.Count > _maxHistoryCount)
                        _audioHistory.RemoveAt(0);
                    ScrollAudioToEnd();
                    if (_audioToPromptCheck.IsChecked == true)
                    {
                        if (_tabControl.SelectedIndex == 0) _promptBox.Text = text;
                        else if (_tabControl.SelectedIndex == 1) _videoPromptBox.Text = text;
                    }
                });
            },
            onError: err =>
            {
                Dispatcher.BeginInvoke(() => _audioStatusLabel.Content = $"Error: {err}");
            })
        {
            TranslateToEnglish = _audioTranslateCheck.IsChecked == true
        };

        _audioCaptureBtn.Content = "Stop Listening";
        _audioCaptureBtn.Background = Error;
        _audioStatusLabel.Content = _audioSourceCombo.SelectedIndex == 0 ? "Listening to microphone..." : "Listening to speakers...";
        _transcriber.Start();
        UpdateTabLockState();
    }

    private void StopCapture()
    {
        _transcriber?.Dispose();
        _transcriber = null;
        _audioCaptureBtn.Content = "Start Listening";
        _audioCaptureBtn.Background = Accent;
        _audioStatusLabel.Content = "Stopped";
        UpdateTabLockState();
    }

    private void WireOverlayEvents(TranscriptionOverlay overlay)
    {
        overlay.FontSizeChanged += fs =>
        {
            _syncingOverlay = true;
            _audioFontSlider.Value = fs;
            _audioFontLabel.Content = ((int)fs).ToString();
            _syncingOverlay = false;
        };
        overlay.TextColorChanged += c =>
        {
            _syncingOverlay = true;
            for (int i = 0; i < _overlayColorPresets.Length; i++)
            {
                if (_overlayColorPresets[i].Color == c)
                {
                    _audioColorCombo.SelectedIndex = i;
                    break;
                }
            }
            _syncingOverlay = false;
        };
        overlay.BgOpacityChanged += alpha =>
        {
            _syncingOverlay = true;
            _audioOpacitySlider.Value = alpha;
            _audioOpacityLabel.Content = $"{(int)(alpha / 240.0 * 100)}%";
            _syncingOverlay = false;
        };
    }

    private async void OnMainWindowClosing(object? _, System.ComponentModel.CancelEventArgs e)
    {
        if (_isShuttingDown) return;
        e.Cancel = true;
        _isShuttingDown = true;
        ShowLoadingOverlay();
        _overlayLabel.Text = "Shutting down KoboldCpp...";

        try
        {
            _cts?.Cancel();
            _cts?.Dispose();
            _cts = null;

            await Task.Run(() =>
            {
                try { _koboldProcess?.Stop(); } catch { }
                _koboldProcess?.Dispose();
            });
            _koboldProcess = null;
            _koboldClient?.Dispose();
            _koboldClient = null;
            _openRouterClient?.Dispose();
            _openRouterClient = null;
            _transcriber?.Dispose();
            _transcriber = null;
            _transcriptionOverlay?.Close();
            _transcriptionOverlay = null;
            foreach (var o in _screenOcrOverlays.ToArray())
            {
                o.Stop();
                o.Close();
            }
            _screenOcrOverlays.Clear();
            _hotkeyManager.Dispose();
            StopVoiceRecording();
            _voiceWatchDebounce?.Dispose();
            _voiceWatchDebounce = null;
            if (_voiceFileWatcher != null)
            {
                _voiceFileWatcher.EnableRaisingEvents = false;
                _voiceFileWatcher.Changed -= OnWatchFileChanged;
                _voiceFileWatcher.Dispose();
                _voiceFileWatcher = null;
            }
        }
        catch { }

        _settings.ImageDenoisingStrength = (float)_denoisingSlider.Value;
        _settings.ViewMode = _modeCombo.SelectedIndex;
        SyncActiveSessionToHistory();
        AutoSaveConfig();

        Close();
    }

    private void OnWatchFileChanged(object sender, FileSystemEventArgs e)
    {
        _voiceWatchLastChange = DateTime.UtcNow;
        _voiceWatchDebounce?.Dispose();
        _voiceWatchDebounce = new System.Threading.Timer(_ =>
        {
            try
            {
                var elapsed = DateTime.UtcNow - _voiceWatchLastChange;
                if (elapsed.TotalMilliseconds < 800) return;
                Dispatcher.BeginInvoke(() => ProcessWatchFileChange());
            }
            catch { }
        }, null, 1000, System.Threading.Timeout.Infinite);
    }

    private void ProcessWatchFileChange()
    {
        try
        {
            var path = _voiceWatchPathBox.Text;
            if (!File.Exists(path)) return;
            var allLines = File.ReadAllLines(path);
            if (allLines.Length <= _voiceWatchLineCount) return;
            var newText = string.Join(Environment.NewLine, allLines.Skip(_voiceWatchLineCount));
            _voiceWatchLineCount = allLines.Length;
            if (string.IsNullOrWhiteSpace(newText)) return;
            _voiceTextInput.Text = (_voiceTextInput.Text.Trim() + Environment.NewLine + newText).Trim();
            _voiceStatusLabel.Content = "New text detected";
            _voiceStatusLabel.Foreground = Fg;
            if (_voiceCloneBtn.IsEnabled)
                _voiceCloneBtn.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        }
        catch { }
    }

    private void StopVoiceRecording()
    {
        lock (_voiceRecorderLock)
        {
            if (!_voiceIsRecording) return;
            try { _voiceRecorder?.StopRecording(); } catch { }
            _voiceRecorder?.Dispose();
            _voiceRecorder = null;
            _voiceRecorderWriter?.Dispose();
            _voiceRecorderWriter = null;
            _voiceIsRecording = false;
        }
        Dispatcher.BeginInvoke(() =>
        {
            _voiceRecordBtn.Content = "Record Mic";
            _voiceRecordBtn.Background = Surface;
            _voiceStatusLabel.Content = "";
        });
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
            int violet = 0x00961946;
            DwmSetWindowAttribute(hwnd, 35, ref violet, sizeof(int));
        }
        catch { }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    private FrameworkElement BuildRootGrid()
    {
        var root = new Grid { Margin = new Thickness(0) };
        root.RowDefinitions.Add(new RowDefinition());
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(30) });

        var outer = new Grid();
        outer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(48) });
        outer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var sidebar = BuildSideToolbar();
        Grid.SetColumn(sidebar, 0);
        outer.Children.Add(sidebar);

        var content = BuildContent();
        Grid.SetColumn(content, 1);
        outer.Children.Add(content);

        Grid.SetRow(outer, 0);
        root.Children.Add(outer);

        var footer = BuildStatusBar();
        Grid.SetRow(footer, 1);
        root.Children.Add(footer);

        _loadingOverlay = BuildLoadingOverlay();
        root.Children.Add(_loadingOverlay);

        return root;
    }

    private Border BuildSideToolbar()
    {
        var modeLabels = new[] { "TEXT", "IMG", "VID", "VIS", "AUD" };
        var panel = new StackPanel { Background = Surface };

        for (int i = 0; i < 5; i++)
        {
            int tabIdx = _btnToTab[i];
            var btn = new Button
            {
                Content = modeLabels[i],
                Height = 38,
                FontSize = 10,
                FontFamily = FontSegoe,
                Cursor = Cursors.Hand,
                Background = Brushes.Transparent,
                Foreground = FgDim,
                BorderThickness = new Thickness(3, 0, 0, 0),
                BorderBrush = Brushes.Transparent,
                Padding = new Thickness(0),
                HorizontalContentAlignment = HorizontalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center
            };
            btn.MouseEnter += (_, _) => { if (_tabControl.SelectedIndex != tabIdx) btn.Background = _sideHoverBg; };
            btn.MouseLeave += (_, _) => { if (_tabControl.SelectedIndex != tabIdx) btn.Background = Brushes.Transparent; };
            btn.Click += (_, _) => _tabControl.SelectedIndex = tabIdx;
            _sideModeBtns[i] = btn;
            panel.Children.Add(btn);
        }

        panel.Children.Add(new Rectangle { Height = 1, Fill = BorderAlt, Margin = new Thickness(8, 4, 8, 4) });

        var settingsBtn = new Button
        {
            Content = "\u2699",
            Height = 38,
            FontSize = 16,
            FontFamily = FontSegoe,
            Cursor = Cursors.Hand,
            Background = Brushes.Transparent,
            Foreground = FgDim,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            ToolTip = "Settings"
        };
        settingsBtn.MouseEnter += (_, _) => settingsBtn.Background = _sideHoverBg;
        settingsBtn.MouseLeave += (_, _) => settingsBtn.Background = Brushes.Transparent;
        settingsBtn.Click += (_, _) =>
        {
            var dlg = new SettingsWindow(_settings, this, _configPath);
            dlg.SettingsApplied += OnSettingsApplied;
            dlg.UpdateRequested += () => Dispatcher.BeginInvoke(() => OnDownloadKoboldClick(null!, null!));
            if (dlg.ShowDialog() == true)
            {
                ApplySettingsToUI();
                AutoSaveConfig();
                Log("Settings saved.");
                RestartKoboldIfRunning();
            }
        };
        panel.Children.Add(settingsBtn);

        return new Border
        {
            Background = Surface,
            BorderBrush = BorderAlt,
            BorderThickness = new Thickness(0, 0, 1, 0),
            Child = panel
        };
    }

    private void UpdateSidebarSelection(int tabIdx)
    {
        int selBtn = -1;
        for (int i = 0; i < _btnToTab.Length; i++)
        {
            if (_btnToTab[i] == tabIdx) { selBtn = i; break; }
        }
        for (int i = 0; i < _sideModeBtns.Length; i++)
        {
            if (_sideModeBtns[i] == null) continue;
            if (i == selBtn)
            {
                _sideModeBtns[i].Background = AccentBg;
                _sideModeBtns[i].BorderBrush = Accent;
                _sideModeBtns[i].Foreground = Brushes.White;
            }
            else
            {
                _sideModeBtns[i].Background = Brushes.Transparent;
                _sideModeBtns[i].BorderBrush = Brushes.Transparent;
                _sideModeBtns[i].Foreground = FgDim;
            }
        }
    }

    private static readonly Brush BrVisionLocked = AppTheme.F(new SolidColorBrush(Color.FromRgb(120, 30, 30)));
    private static readonly Brush BrVisionUnlocked = AppTheme.F(new SolidColorBrush(Color.FromRgb(100, 60, 200)));
    private static readonly Brush BrTextSend = AppTheme.F(new SolidColorBrush(Color.FromRgb(60, 60, 200)));
    private Grid BuildContent()
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(380) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(6) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        _tabControl = new TabControl
        {
            Background = Bg,
            BorderThickness = new Thickness(0),
            FontSize = 13
        };

        try
        {
            var template = (ControlTemplate)System.Windows.Markup.XamlReader.Parse(@"
<ControlTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'
                 TargetType='TabControl'>
    <ContentPresenter ContentSource='SelectedContent' Margin='0'/>
</ControlTemplate>");
            _tabControl.Template = template;
        }
        catch { }

        try
        {
            var tabItemStyle = (Style)System.Windows.Markup.XamlReader.Parse(@"
<Style xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' TargetType='TabItem'>
    <Setter Property='Background' Value='#26262C'/>
    <Setter Property='Foreground' Value='#9696A2'/>
    <Setter Property='FontSize' Value='13'/>
    <Setter Property='Padding' Value='0,6,0,6'/>
    <Setter Property='Margin' Value='0'/>
    <Setter Property='BorderThickness' Value='0'/>
    <Setter Property='HorizontalContentAlignment' Value='Stretch'/>
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
                    <MultiTrigger>
                        <MultiTrigger.Conditions>
                            <Condition Property='IsMouseOver' Value='True'/>
                            <Condition Property='IsSelected' Value='True'/>
                        </MultiTrigger.Conditions>
                        <Setter TargetName='Bd' Property='Background' Value='#5880F0'/>
                    </MultiTrigger>
                </ControlTemplate.Triggers>
            </ControlTemplate>
        </Setter.Value>
    </Setter>
</Style>");
            _tabControl.Resources[typeof(TabItem)] = tabItemStyle;
        }
        catch { }

        var imgTab = new TabItem { Header = " Tex2Img " };
        _tabBuilt[0] = true;
        _tabBuilders[0] = null;
        var imgScroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = BuildLeftPanel()
        };
        imgTab.Content = imgScroll;

        var tabHeaders = new[] { " Tex2Vid ", " Vision ", " Text ", " Audio " };
        Func<StackPanel>[] builders = [BuildVideoPanel, BuildVisionPanel, BuildTextPanel, BuildAudioPanel];
        for (int i = 0; i < 4; i++)
        {
            int idx = i + 1;
            _tabBuilders[idx] = builders[i];
            _tabBuilt[idx] = false;
            var tab = new TabItem { Header = tabHeaders[i] };
            var scroll = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Content = new StackPanel()
            };
            tab.Content = scroll;
            _tabControl.Items.Add(tab);
        }

        _tabControl.Items.Insert(0, imgTab);

        _tabControl.SelectionChanged += OnTabChanged;
        _tabControl.SelectedIndex = 3;
        UpdateSidebarSelection(3);

        _generateBtn = new Button
        {
            Content = "Generate",
            FontSize = 14,
            FontWeight = FontWeight.FromOpenTypeWeight(600),
            Cursor = Cursors.Hand,
            Background = BrGreen,
            Foreground = Brushes.White,
            BorderThickness = new Thickness(0),
            Height = 38,
            Margin = new Thickness(8, 0, 8, 0),
            Visibility = Visibility.Collapsed
        };
        _generateBtn.Click += OnGenerateClick;
        _progressBar = new ProgressBar
        {
            Height = 6,
            Foreground = Success,
            Background = new SolidColorBrush(Color.FromRgb(40, 40, 48)),
            BorderThickness = new Thickness(0),
            Minimum = 0,
            Maximum = 100,
            Value = 0,
            VerticalAlignment = VerticalAlignment.Center
        };

        _progressLabel = new Label
        {
            Content = "",
            Foreground = FgDim,
            FontSize = 11,
            Padding = new Thickness(6, 0, 8, 0),
            VerticalContentAlignment = VerticalAlignment.Center,
            MinWidth = 40,
            HorizontalContentAlignment = HorizontalAlignment.Right
        };

        var progressRow = new Grid { Margin = new Thickness(8, 4, 8, 0) };
        progressRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        progressRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        progressRow.Children.Add(_progressBar);
        Grid.SetColumn(_progressLabel, 1);
        progressRow.Children.Add(_progressLabel);

        var leftOuter = new Grid();
        leftOuter.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        leftOuter.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        leftOuter.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        leftOuter.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var tabBorder = new Border
        {
            Background = Bg,
            BorderBrush = Border,
            BorderThickness = new Thickness(0, 0, 1, 0),
            Child = _tabControl
        };

        Grid.SetRow(tabBorder, 0);
        leftOuter.Children.Add(tabBorder);

        var genLabel = new TextBlock
        {
            Text = "Task",
            FontSize = 11,
            FontWeight = FontWeight.FromOpenTypeWeight(600),
            Foreground = Accent,
            Margin = new Thickness(10, 4, 0, 0)
        };

        Grid.SetRow(genLabel, 1);
        leftOuter.Children.Add(genLabel);

        Grid.SetRow(_generateBtn, 2);
        leftOuter.Children.Add(_generateBtn);

        Grid.SetRow(progressRow, 3);
        leftOuter.Children.Add(progressRow);

        grid.Children.Add(leftOuter);

        double splitStartX = 0, splitStartWidth = 0;
        var splitter = new Border
        {
            Width = 6,
            Background = Bg,
            Cursor = Cursors.SizeWE
        };
        splitter.MouseDown += (_, e) =>
        {
            splitStartX = e.GetPosition(this).X;
            splitStartWidth = ((Grid)splitter.Parent).ColumnDefinitions[0].Width.Value;
            splitter.CaptureMouse();
        };
        splitter.MouseMove += (_, e) =>
        {
            if (e.LeftButton == MouseButtonState.Pressed && splitter.IsMouseCaptured)
            {
                double delta = e.GetPosition(this).X - splitStartX;
                double newWidth = Math.Max(250, Math.Min(600, splitStartWidth + delta));
                ((Grid)splitter.Parent).ColumnDefinitions[0].Width = new GridLength(newWidth);
            }
        };
        splitter.MouseUp += (_, _) => splitter.ReleaseMouseCapture();
        Grid.SetColumn(splitter, 1);
        grid.Children.Add(splitter);

        var right = BuildRightPanel();
        Grid.SetColumn(right, 2);
        grid.Children.Add(right);

        return grid;
    }

    private StackPanel BuildLeftPanel()
    {
        var s = new StackPanel { Margin = new Thickness(12, 8, 12, 8) };

        s.Children.Add(PromptLabelWithButton("Prompt", (_, _) => LoadMdFile(false)));
        _promptBox = new TextBox
        {
            Height = 80,
            FontSize = 13,
            Background = InputBg,
            Foreground = Fg,
            BorderBrush = Border,
            CaretBrush = Fg,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Padding = new Thickness(8, 6, 8, 6),
            VerticalContentAlignment = VerticalAlignment.Top
        };
        s.Children.Add(Card(_promptBox));

        s.Children.Add(SectionLabel("Negative Prompt"));
        _negativeBox = new TextBox
        {
            Height = 60,
            FontSize = 13,
            Background = BrErrorBg,
            Foreground = BrErrorFg,
            BorderBrush = BrErrorBorder,
            CaretBrush = Fg,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Padding = new Thickness(8, 6, 8, 6),
            VerticalContentAlignment = VerticalAlignment.Top
        };
        s.Children.Add(Card(_negativeBox));

        s.Children.Add(SectionLabel("Dimensions"));
        var dimPanel = new StackPanel { Margin = new Thickness(0, 0, 0, 6) };
        dimPanel.Children.Add(SliderRow("W:", 64, 2048, 1024, 64, out _widthSlider, out _widthLabel));
        dimPanel.Children.Add(SliderRow("H:", 64, 2048, 1024, 64, out _heightSlider, out _heightLabel));
        s.Children.Add(Card(dimPanel));

        s.Children.Add(SectionLabel("Sampling"));
        var sampPanel = new StackPanel();
        sampPanel.Children.Add(SliderRow("Steps:", 1, 100, 20, 1, out _stepsSlider, out _stepsLabel));
        sampPanel.Children.Add(SliderRow("CFG:", 1f, 30f, 7f, 0.5f, out _cfgSlider, out _cfgLabel));

        var misc = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
        _randomSeedCheck = new CheckBox { Content = "Random", Foreground = Fg, IsChecked = true, FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
        _seedBox = new TextBox
        {
            Text = "42",
            Width = 80,
            Height = 26,
            FontSize = 12,
            Background = InputBg,
            Foreground = Fg,
            BorderBrush = Border,
            CaretBrush = Fg,
            Padding = new Thickness(6, 0, 6, 0),
            VerticalContentAlignment = VerticalAlignment.Center,
            IsEnabled = false
        };
        _randomSeedCheck.Checked += (_, _) => _seedBox.IsEnabled = false;
        _randomSeedCheck.Unchecked += (_, _) => _seedBox.IsEnabled = true;

        var batchLbl = new TextBlock { Text = "Batch:", Foreground = Fg, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 4, 0) };
        _batchCountBox = new TextBox
        {
            Text = "1",
            Width = 50,
            Height = 26,
            FontSize = 12,
            Background = InputBg,
            Foreground = Fg,
            BorderBrush = Border,
            CaretBrush = Fg,
            Padding = new Thickness(6, 0, 6, 0),
            VerticalContentAlignment = VerticalAlignment.Center
        };
        misc.Children.Add(_randomSeedCheck);
        misc.Children.Add(_seedBox);
        misc.Children.Add(batchLbl);
        misc.Children.Add(_batchCountBox);
        sampPanel.Children.Add(misc);

        s.Children.Add(Card(sampPanel));

        s.Children.Add(SectionLabel("Reference Images"));
        var refPanel = new StackPanel { Margin = new Thickness(0, 0, 0, 4) };
        _refImageList = new ListBox
        {
            Height = 60,
            FontSize = 11,
            Background = InputBg,
            Foreground = Fg,
            BorderBrush = Border,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(4, 2, 4, 2)
        };
        var browseRefBtn = MakeBtn("Add Images", 90, 28, 12, (_, _) =>
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Filter = "Images (*.png;*.jpg;*.jpeg;*.webp)|*.png;*.jpg;*.jpeg;*.webp|All files (*.*)|*.*",
                Multiselect = true
            };
            if (dlg.ShowDialog(this) == true)
            {
                foreach (var f in dlg.FileNames)
                {
                    if (!_refImagePaths.Contains(f, StringComparer.OrdinalIgnoreCase))
                    {
                        _refImagePaths.Add(f);
                        _refImageList.Items.Add(Path.GetFileName(f));
                    }
                }
                Log($"References: {_refImagePaths.Count} file(s)");
            }
        }, Surface, Fg);
        var clearRefBtn = MakeBtn("Clear", 70, 28, 12, (_, _) => { _refImagePaths.Clear(); _refImageList.Items.Clear(); }, Surface, Fg);
        var refBtnRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
        refBtnRow.Children.Add(browseRefBtn);
        refBtnRow.Children.Add(new Rectangle { Width = 6, Fill = Brushes.Transparent });
        refBtnRow.Children.Add(clearRefBtn);
        refPanel.Children.Add(_refImageList);
        refPanel.Children.Add(refBtnRow);
        refPanel.Children.Add(SliderRow("Denoise:", 0f, 1f, _settings.ImageDenoisingStrength, 0.05f, out _denoisingSlider, out _denoisingLabel));
        s.Children.Add(Card(refPanel));

        var thumbRow = new Grid { Margin = new Thickness(0, 0, 0, 4) };
        thumbRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(80) });
        thumbRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        thumbRow.Children.Add(new TextBlock { Text = "Thumbnails:", Foreground = Fg, FontSize = 11, VerticalAlignment = VerticalAlignment.Center });
        _thumbPreviewCombo = new ComboBox
        {
            Height = 24,
            FontSize = 11,
            Template = SettingsWindow.DarkComboTemplate,
            Background = InputBg,
            Foreground = Fg,
            BorderBrush = Border,
            Margin = new Thickness(6, 0, 0, 0),
            Padding = new Thickness(6, 0, 6, 0)
        };
        var tpItemStyle = new Style(typeof(ComboBoxItem));
        tpItemStyle.Setters.Add(new Setter(Control.BackgroundProperty, Surface));
        tpItemStyle.Setters.Add(new Setter(Control.ForegroundProperty, Fg));
        tpItemStyle.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(6, 2, 6, 2)));
        var tpHover = new Trigger { Property = ComboBoxItem.IsMouseOverProperty, Value = true };
        tpHover.Setters.Add(new Setter(Control.BackgroundProperty, Highlight));
        tpItemStyle.Triggers.Add(tpHover);
        _thumbPreviewCombo.ItemContainerStyle = tpItemStyle;
        _thumbPreviewCombo.Items.Add("Disable");
        _thumbPreviewCombo.Items.Add("Enable");
        _thumbPreviewCombo.SelectedIndex = _settings.ThumbnailPreview ? 1 : 0;
        Grid.SetColumn(_thumbPreviewCombo, 1);
        thumbRow.Children.Add(_thumbPreviewCombo);
        s.Children.Add(thumbRow);

        _thumbPreviewCombo.SelectionChanged += (_, _) =>
        {
            bool enabled = _thumbPreviewCombo.SelectedIndex == 1;
            _settings.ThumbnailPreview = enabled;
            AutoSaveConfig();
            if (_thumbnailBox != null)
            {
                _thumbnailBox.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;
                if (_thumbColumn != null)
                    _thumbColumn.Width = enabled ? new GridLength(136) : new GridLength(0);
                if (enabled && !_thumbnailsLoaded)
                {
                    _thumbnailsLoaded = true;
                    RebuildThumbnails();
                }
            }
        };

        return s;
    }

    private StackPanel BuildVideoPanel()
    {
        var s = new StackPanel { Margin = new Thickness(12, 8, 12, 8) };

        s.Children.Add(PromptLabelWithButton("Prompt", (_, _) => LoadMdFile(true)));
        _videoPromptBox = new TextBox
        {
            Height = 80,
            FontSize = 13,
            Background = InputBg,
            Foreground = Fg,
            BorderBrush = Border,
            CaretBrush = Fg,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Padding = new Thickness(8, 6, 8, 6),
            VerticalContentAlignment = VerticalAlignment.Top
        };
        s.Children.Add(Card(_videoPromptBox));

        s.Children.Add(SectionLabel("Negative Prompt"));
        _videoNegativeBox = new TextBox
        {
            Height = 60,
            FontSize = 13,
            Background = BrErrorBg,
            Foreground = BrErrorFg,
            BorderBrush = BrErrorBorder,
            CaretBrush = Fg,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Padding = new Thickness(8, 6, 8, 6),
            VerticalContentAlignment = VerticalAlignment.Top
        };
        s.Children.Add(Card(_videoNegativeBox));

        s.Children.Add(SectionLabel("Dimensions"));
        var dimPanel = new StackPanel { Margin = new Thickness(0, 0, 0, 6) };
        dimPanel.Children.Add(SliderRow("W:", 64, 1024, 512, 64, out _videoWidthSlider, out _videoWidthLabel));
        dimPanel.Children.Add(SliderRow("H:", 64, 1024, 512, 64, out _videoHeightSlider, out _videoHeightLabel));
        var warn = new TextBlock
        {
            Text = "Max 512x512 recommended for LTX2.3",
            Foreground = FgMuted,
            FontSize = 10,
            Margin = new Thickness(0, 2, 0, 0)
        };
        dimPanel.Children.Add(warn);
        s.Children.Add(Card(dimPanel));

        s.Children.Add(SectionLabel("Frames & FPS"));
        var fpsPanel = new StackPanel();
        fpsPanel.Children.Add(SliderRow("Frames:", 1, 200, 50, 1, out _videoFramesSlider, out _videoFramesLabel));
        fpsPanel.Children.Add(SliderRow("FPS:", 1, 32, 16, 1, out _videoFpsSlider, out _videoFpsLabel));
        s.Children.Add(Card(fpsPanel));

        s.Children.Add(SectionLabel("Sampling"));
        var sampPanel = new StackPanel();
        sampPanel.Children.Add(SliderRow("Steps:", 1, 100, 15, 1, out _videoStepsSlider, out _videoStepsLabel));
        sampPanel.Children.Add(SliderRow("CFG:", 0.5f, 30f, 1f, 0.5f, out _videoCfgSlider, out _videoCfgLabel));

        var misc = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
        _videoRandomSeedCheck = new CheckBox { Content = "Random", Foreground = Fg, IsChecked = true, FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
        _videoSeedBox = new TextBox
        {
            Text = "42",
            Width = 80,
            Height = 26,
            FontSize = 12,
            Background = InputBg,
            Foreground = Fg,
            BorderBrush = Border,
            CaretBrush = Fg,
            Padding = new Thickness(6, 0, 6, 0),
            VerticalContentAlignment = VerticalAlignment.Center,
            IsEnabled = false
        };
        _videoRandomSeedCheck.Checked += (_, _) => _videoSeedBox.IsEnabled = false;
        _videoRandomSeedCheck.Unchecked += (_, _) => _videoSeedBox.IsEnabled = true;
        misc.Children.Add(_videoRandomSeedCheck);
        misc.Children.Add(_videoSeedBox);
        sampPanel.Children.Add(misc);

        s.Children.Add(Card(sampPanel));

        return s;
    }

    private void OnTabChanged(object sender, SelectionChangedEventArgs e)
    {
        int idx = _tabControl.SelectedIndex;
        UpdateSidebarSelection(idx);
        if (idx >= 0 && idx < _tabBuilders.Length && !_tabBuilt[idx] && _tabBuilders[idx] != null)
        {
            _tabBuilt[idx] = true;
            var panel = _tabBuilders[idx]!();
            if (_tabControl.Items[idx] is TabItem tab && tab.Content is ScrollViewer sv)
                sv.Content = panel;
            ApplySettingsToUI();
        }

        if (idx == 0 && !_thumbnailsLoaded && _thumbPreviewCombo != null && _thumbPreviewCombo.SelectedIndex == 1)
        {
            _thumbnailsLoaded = true;
            RebuildThumbnails();
        }

        if (_resultImage == null) return;

        bool isImage = idx == 0;
        bool isVideo = idx == 1;
        bool isVision = idx == 2;
        bool isText = idx == 3;
        bool isAudio = idx == 4;

        _resultImage.Visibility = isImage ? Visibility.Visible : Visibility.Collapsed;
        _rightImageBorder.Visibility = isImage ? Visibility.Visible : Visibility.Collapsed;
        _videoPlayer.Visibility = isVideo ? Visibility.Visible : Visibility.Collapsed;
        _videoSeekSlider.IsEnabled = isVideo;
        _chatControl.Panel.Visibility = isText ? Visibility.Visible : Visibility.Collapsed;
        _visionChatPanel.Visibility = isVision ? Visibility.Visible : Visibility.Collapsed;
        _audioHistoryList.Visibility = isAudio ? Visibility.Visible : Visibility.Collapsed;

        if (!isVideo) { _videoTimer.Stop(); _videoPlayer.Pause(); }
        _videoSaveBtn.Visibility = isVideo ? Visibility.Visible : Visibility.Collapsed;
        _placeholder.Text = isImage ? "Ready" : isVideo ? "Enter a prompt and click Generate" : "";
        if (isImage)
            _placeholder.Visibility = _resultImage.Source != null ? Visibility.Collapsed : Visibility.Visible;
        else if (isVideo)
            _placeholder.Visibility = _videoPlayer.Source != null ? Visibility.Collapsed : Visibility.Visible;
        else
            _placeholder.Visibility = Visibility.Collapsed;

        if (isText)
        {
            _generateBtn.Visibility = Visibility.Collapsed;
            if (!string.Equals(_settings.AgenticWorkflowMode, "enable", StringComparison.OrdinalIgnoreCase))
                _chatHistory.Clear();
        }
        else if (isImage)
        {
            _generateBtn.Visibility = Visibility.Visible;
            _generateBtn.Content = "Generate";
            _generateBtn.Background = BrGreen;
        }
        else if (isVideo)
        {
            _generateBtn.Visibility = Visibility.Visible;
            _generateBtn.Content = "Generate Video";
            _generateBtn.Background = BrBlue;
        }
        else if (isVision)
        {
            _generateBtn.Visibility = Visibility.Collapsed;
            UpdateVisionLockState();
        }
        else
        {
            _generateBtn.Visibility = Visibility.Collapsed;
        }
        UpdateTransportBarVisibility();
        UpdateTabLockState();
    }

    private void UpdateTransportBarVisibility()
    {
        var parent = _videoPlayer.Parent as Grid;
        if (parent == null) return;
        if (parent.Children.Count < 2) return;
        var transportBar = parent.Children[1] as Border;
        if (transportBar != null)
            transportBar.Visibility = _videoPlayer.Source != null && _tabControl.SelectedIndex == 1
                ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateVideoTimeLabel()
    {
        if (_videoPlayer.Source == null || !_videoPlayer.NaturalDuration.HasTimeSpan)
        {
            _videoTimeLabel.Content = "00:00 / 00:00";
            return;
        }
        var current = _videoPlayer.Position;
        var total = _videoPlayer.NaturalDuration.TimeSpan;
        _videoTimeLabel.Content = $"{(int)current.TotalMinutes:D2}:{current.Seconds:D2} / {(int)total.TotalMinutes:D2}:{total.Seconds:D2}";
    }

    private StackPanel BuildTextPanel()
    {
        var s = new StackPanel { Margin = new Thickness(12, 8, 12, 8) };

        s.Children.Add(SectionLabel("Prompt"));
        _chatInputBox = new TextBox
        {
            Height = 100,
            FontSize = 13,
            Background = InputBg,
            Foreground = Fg,
            BorderBrush = Border,
            CaretBrush = Fg,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Padding = new Thickness(8, 6, 8, 6),
            VerticalContentAlignment = VerticalAlignment.Top
        };
        s.Children.Add(Card(_chatInputBox));
        _chatInputBox.PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter || Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) return;
            e.Handled = true;
            OnChatSendClick(_chatInputBox, new RoutedEventArgs());
        };

        var btnRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
        _textSendBtn = new Button { Content = "Send", Width = 60, Height = 26, FontSize = 11, Cursor = Cursors.Hand, Background = Accent, Foreground = Brushes.White, BorderThickness = new Thickness(0) };
        _textSendBtn.Click += OnChatSendClick;
        btnRow.Children.Add(_textSendBtn);
        btnRow.Children.Add(new Rectangle { Width = 6, Fill = Brushes.Transparent });
        var attachBtn = new Button { Content = "+", Width = 26, Height = 26, FontSize = 14, FontWeight = FontWeight.FromOpenTypeWeight(600), Cursor = Cursors.Hand, Background = new SolidColorBrush(Color.FromRgb(80, 80, 100)), Foreground = Brushes.White, BorderThickness = new Thickness(0), Padding = new Thickness(0) };
        attachBtn.Click += OnTextAttachClick;
        btnRow.Children.Add(attachBtn);
        btnRow.Children.Add(new Rectangle { Width = 4, Fill = Brushes.Transparent });
        var menuBtn = new Button { Content = "\u22EE", Width = 22, Height = 26, FontSize = 14, Cursor = Cursors.Hand, Background = Surface, Foreground = Fg, BorderThickness = new Thickness(1), BorderBrush = Border, Padding = new Thickness(0), VerticalContentAlignment = VerticalAlignment.Center, HorizontalContentAlignment = HorizontalAlignment.Center, ToolTip = "More" };
        var menu = new ContextMenu
        {
            Background = Surface,
            Foreground = Fg,
            FontSize = 12,
            BorderThickness = new Thickness(1),
            BorderBrush = BorderAlt
        };
        try
        {
            var cmStyle = (Style)System.Windows.Markup.XamlReader.Parse(@"
<Style xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' TargetType='ContextMenu'>
    <Setter Property='Template'>
        <Setter.Value>
            <ControlTemplate TargetType='ContextMenu'>
                <Border Background='{TemplateBinding Background}' BorderBrush='{TemplateBinding BorderBrush}' BorderThickness='{TemplateBinding BorderThickness}'>
                    <ItemsPresenter Margin='0'/>
                </Border>
            </ControlTemplate>
        </Setter.Value>
    </Setter>
</Style>");
            menu.Resources[typeof(ContextMenu)] = cmStyle;
            var miStyle = (Style)System.Windows.Markup.XamlReader.Parse(@"
<Style xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' TargetType='MenuItem'>
    <Setter Property='Background' Value='Transparent'/>
    <Setter Property='Foreground' Value='#E6E6EE'/>
    <Setter Property='Padding' Value='12,5,16,5'/>
    <Setter Property='Cursor' Value='Hand'/>
    <Setter Property='Template'>
        <Setter.Value>
            <ControlTemplate TargetType='MenuItem'>
                <Border Name='Bd' Background='{TemplateBinding Background}' Padding='{TemplateBinding Padding}'>
                    <ContentPresenter ContentSource='Header' VerticalAlignment='Center'/>
                </Border>
                <ControlTemplate.Triggers>
                    <Trigger Property='IsMouseOver' Value='True'>
                        <Setter TargetName='Bd' Property='Background' Value='#3A3A44'/>
                    </Trigger>
                </ControlTemplate.Triggers>
            </ControlTemplate>
        </Setter.Value>
    </Setter>
</Style>");
            menu.Resources[typeof(MenuItem)] = miStyle;
        }
        catch { }
        var clearItem = new MenuItem { Header = "Clear" };
        clearItem.Click += (_, _) => { if (_activeSession != null) _activeSession.Messages.Clear(); _chatHistory.Clear(); };
        menu.Items.Add(clearItem);
        var saveItem = new MenuItem { Header = "Save" };
        saveItem.Click += (_, _) => { SyncActiveSessionToHistory(); AutoSaveConfig(); Log("Session saved."); };
        menu.Items.Add(saveItem);
        var loadItem = new MenuItem { Header = "Load" };
        loadItem.Click += (_, _) => { LoadSessionsFromSettings(); Log("Sessions reloaded."); };
        menu.Items.Add(loadItem);
        menuBtn.Click += (_, _) => { menu.PlacementTarget = menuBtn; menu.IsOpen = true; };
        btnRow.Children.Add(menuBtn);
        s.Children.Add(btnRow);

        _textAttachPanel = new WrapPanel { Margin = new Thickness(0, 0, 0, 6) };
        s.Children.Add(_textAttachPanel);

        // Custom tab header: Config | Sessions (no TabControl — manual layout for clean borderless look)
        _textInnerTabs = CreateTabControl(BuildTextConfigPanel, BuildSessionsPanel);
        s.Children.Add(_textInnerTabs);

        return s;
    }

    private StackPanel BuildTextConfigPanel()
    {
        var s = new StackPanel { Margin = new Thickness(4, 8, 4, 8) };
        var textOptionsPanel = new StackPanel { Margin = new Thickness(0, 4, 0, 8) };

        var thinkingRow = new Grid { Margin = new Thickness(0, 0, 0, 6) };
        thinkingRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(80) });
        thinkingRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        thinkingRow.Children.Add(new TextBlock { Text = "Thinking:", Foreground = Fg, FontSize = 11, VerticalAlignment = VerticalAlignment.Center });
        _textEnableThinking = new ComboBox { Height = 24, FontSize = 11, Template = SettingsWindow.DarkComboTemplate, Background = InputBg, Foreground = Fg, BorderBrush = Border, Margin = new Thickness(6, 0, 0, 0), Padding = new Thickness(6, 0, 6, 0) };
        var ttItemStyle = new Style(typeof(ComboBoxItem));
        ttItemStyle.Setters.Add(new Setter(Control.BackgroundProperty, Surface));
        ttItemStyle.Setters.Add(new Setter(Control.ForegroundProperty, Fg));
        var ttHover = new Trigger { Property = ComboBoxItem.IsMouseOverProperty, Value = true };
        ttHover.Setters.Add(new Setter(Control.BackgroundProperty, Highlight));
        ttItemStyle.Triggers.Add(ttHover);
        _textEnableThinking.ItemContainerStyle = ttItemStyle;
        _textEnableThinking.Items.Add("Disable");
        _textEnableThinking.Items.Add("Enable");
        _textEnableThinking.SelectedIndex = _settings.EnableThinking ? 1 : 0;
        Grid.SetColumn(_textEnableThinking, 1);
        thinkingRow.Children.Add(_textEnableThinking);
        textOptionsPanel.Children.Add(thinkingRow);

        var effortRow = new Grid { Margin = new Thickness(0, 0, 0, 6) };
        effortRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(80) });
        effortRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        effortRow.Children.Add(new TextBlock { Text = "Thinking Effort:", Foreground = Fg, FontSize = 11, VerticalAlignment = VerticalAlignment.Center });
        _textThinkingEffort = new ComboBox { Height = 24, FontSize = 11, Template = SettingsWindow.DarkComboTemplate, Background = InputBg, Foreground = Fg, BorderBrush = Border, Margin = new Thickness(6, 0, 0, 0), Padding = new Thickness(6, 0, 6, 0) };
        var teItemStyle = new Style(typeof(ComboBoxItem));
        teItemStyle.Setters.Add(new Setter(Control.BackgroundProperty, Surface));
        teItemStyle.Setters.Add(new Setter(Control.ForegroundProperty, Fg));
        var teHover = new Trigger { Property = ComboBoxItem.IsMouseOverProperty, Value = true };
        teHover.Setters.Add(new Setter(Control.BackgroundProperty, Highlight));
        teItemStyle.Triggers.Add(teHover);
        _textThinkingEffort.ItemContainerStyle = teItemStyle;
        _textThinkingEffort.Items.Add("low");
        _textThinkingEffort.Items.Add("medium");
        _textThinkingEffort.Items.Add("high");
        _textThinkingEffort.SelectedItem = _settings.ThinkingEffort ?? "medium";
        _textThinkingEffort.SelectionChanged += (_, _) => { _settings.ThinkingEffort = _textThinkingEffort.SelectedItem as string ?? "medium"; AutoSaveConfig(); };
        Grid.SetColumn(_textThinkingEffort, 1);
        effortRow.Children.Add(_textThinkingEffort);
        textOptionsPanel.Children.Add(effortRow);

        var cpRow = new Grid { Margin = new Thickness(0, 0, 0, 6) };
        cpRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(80) });
        cpRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        cpRow.Children.Add(new TextBlock { Text = "Compact Prompt:", Foreground = Fg, FontSize = 11, VerticalAlignment = VerticalAlignment.Center });
        _textCompactPrompt = new ComboBox { Height = 24, FontSize = 11, Template = SettingsWindow.DarkComboTemplate, Background = InputBg, Foreground = Fg, BorderBrush = Border, Margin = new Thickness(6, 0, 0, 0), Padding = new Thickness(6, 0, 6, 0) };
        var cpItemStyle = new Style(typeof(ComboBoxItem));
        cpItemStyle.Setters.Add(new Setter(Control.BackgroundProperty, Surface));
        cpItemStyle.Setters.Add(new Setter(Control.ForegroundProperty, Fg));
        var cpHover = new Trigger { Property = ComboBoxItem.IsMouseOverProperty, Value = true };
        cpHover.Setters.Add(new Setter(Control.BackgroundProperty, Highlight));
        cpItemStyle.Triggers.Add(cpHover);
        _textCompactPrompt.ItemContainerStyle = cpItemStyle;
        _textCompactPrompt.Items.Add("Enable");
        _textCompactPrompt.Items.Add("Disable");
        _textCompactPrompt.SelectedIndex = _settings.CompactPrompt ? 0 : 1;
        Grid.SetColumn(_textCompactPrompt, 1);
        cpRow.Children.Add(_textCompactPrompt);
        textOptionsPanel.Children.Add(cpRow);

        var agenticRow = new Grid { Margin = new Thickness(0, 0, 0, 6) };
        agenticRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(80) });
        agenticRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        agenticRow.Children.Add(new TextBlock { Text = "Agentic Workflow:", Foreground = Fg, FontSize = 11, VerticalAlignment = VerticalAlignment.Center });
        _textAgenticWorkflow = new ComboBox { Height = 24, FontSize = 11, Template = SettingsWindow.DarkComboTemplate, Background = InputBg, Foreground = Fg, BorderBrush = Border, Margin = new Thickness(6, 0, 0, 0), Padding = new Thickness(6, 0, 6, 0) };
        var awItemStyle = new Style(typeof(ComboBoxItem));
        awItemStyle.Setters.Add(new Setter(Control.BackgroundProperty, Surface));
        awItemStyle.Setters.Add(new Setter(Control.ForegroundProperty, Fg));
        var awHover = new Trigger { Property = ComboBoxItem.IsMouseOverProperty, Value = true };
        awHover.Setters.Add(new Setter(Control.BackgroundProperty, Highlight));
        awItemStyle.Triggers.Add(awHover);
        _textAgenticWorkflow.ItemContainerStyle = awItemStyle;
        _textAgenticWorkflow.Items.Add("Disable");
        _textAgenticWorkflow.Items.Add("Enable");
        _textAgenticWorkflow.SelectedIndex = string.Equals(_settings.AgenticWorkflowMode, "enable", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
        Grid.SetColumn(_textAgenticWorkflow, 1);
        agenticRow.Children.Add(_textAgenticWorkflow);
        textOptionsPanel.Children.Add(agenticRow);

        _confirmRow = new Grid { Margin = new Thickness(0, 0, 0, 6) };
        _confirmRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(80) });
        _confirmRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _confirmRow.Children.Add(new TextBlock { Text = "Confirm:", Foreground = Fg, FontSize = 11, VerticalAlignment = VerticalAlignment.Center });
        _textConfirmMode = new ComboBox { Height = 24, FontSize = 11, Template = SettingsWindow.DarkComboTemplate, Background = InputBg, Foreground = Fg, BorderBrush = Border, Margin = new Thickness(6, 0, 0, 0), Padding = new Thickness(6, 0, 6, 0) };
        var cmItemStyle = new Style(typeof(ComboBoxItem));
        cmItemStyle.Setters.Add(new Setter(Control.BackgroundProperty, Surface));
        cmItemStyle.Setters.Add(new Setter(Control.ForegroundProperty, Fg));
        var cmHover = new Trigger { Property = ComboBoxItem.IsMouseOverProperty, Value = true };
        cmHover.Setters.Add(new Setter(Control.BackgroundProperty, Highlight));
        cmItemStyle.Triggers.Add(cmHover);
        _textConfirmMode.ItemContainerStyle = cmItemStyle;
        _textConfirmMode.Items.Add("Auto");
        _textConfirmMode.Items.Add("Manual");
        _textConfirmMode.SelectedIndex = string.Equals(_settings.ConfirmMode, "manual", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
        Grid.SetColumn(_textConfirmMode, 1);
        _confirmRow.Children.Add(_textConfirmMode);
        _confirmRow.Visibility = string.Equals(_settings.AgenticWorkflowMode, "enable", StringComparison.OrdinalIgnoreCase) ? Visibility.Visible : Visibility.Collapsed;
        textOptionsPanel.Children.Add(_confirmRow);
        _textConfirmMode.SelectionChanged += _textConfirmMode_SelectionChanged;

        var historyRow = new Grid { Margin = new Thickness(0) };
        historyRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(80) });
        historyRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        historyRow.Children.Add(new TextBlock { Text = "History:", Foreground = Fg, FontSize = 11, VerticalAlignment = VerticalAlignment.Center });
        _textHistoryBox = new TextBox { Text = _maxHistoryCount.ToString(), Width = 60, Height = 22, FontSize = 11, Background = InputBg, Foreground = Fg, BorderBrush = Border, CaretBrush = Fg, VerticalContentAlignment = VerticalAlignment.Center, HorizontalContentAlignment = HorizontalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(6, 0, 0, 0) };
        _textHistoryBox.TextChanged += (_, _) => { if (int.TryParse(_textHistoryBox.Text, out var val) && val > 0) _maxHistoryCount = val; };
        Grid.SetColumn(_textHistoryBox, 1);
        historyRow.Children.Add(_textHistoryBox);
        textOptionsPanel.Children.Add(historyRow);

        var advExpander = new Expander { Header = "Advanced", Foreground = Fg, Background = Surface, BorderBrush = Border, Margin = new Thickness(0, 6, 0, 0), FontSize = 12, IsExpanded = false, Style = MarkdownFlowDoc.BuildDarkExpanderStyle() };
        var advPanel = new StackPanel { Margin = new Thickness(8, 6, 4, 6) };

        advPanel.Children.Add(new TextBlock { Text = "System Prompt:", Foreground = Fg, FontSize = 11, Margin = new Thickness(0, 0, 0, 3) });
        _textSystemPromptBox = new TextBox { Height = 60, FontSize = 12, Background = InputBg, Foreground = Fg, BorderBrush = Border, CaretBrush = Fg, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(6, 4, 6, 4), Text = _settings.TextSystemPrompt };
        _textSystemPromptBox.TextChanged += (_, _) => { _settings.TextSystemPrompt = _textSystemPromptBox.Text; AutoSaveConfig(); };
        advPanel.Children.Add(Card(_textSystemPromptBox));

        // Backend mode (local / external)
        var modeRow = new Grid { Margin = new Thickness(0, 0, 0, 6) };
        modeRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(80) });
        modeRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        modeRow.Children.Add(new TextBlock { Text = "Mode:", Foreground = Fg, FontSize = 11, VerticalAlignment = VerticalAlignment.Center });
        _backendModeCombo = new ComboBox { Height = 24, FontSize = 11, Template = SettingsWindow.DarkComboTemplate, Background = InputBg, Foreground = Fg, BorderBrush = Border, Margin = new Thickness(6, 0, 0, 0), Padding = new Thickness(6, 0, 6, 0) };
        ApplyComboStyle(_backendModeCombo);
        _backendModeCombo.Items.Add("local");
        _backendModeCombo.Items.Add("external");
        _backendModeCombo.SelectedIndex = _settings.BackendMode == "external" ? 1 : 0;
        _backendModeCombo.SelectionChanged += async (_, _) =>
        {
            _settings.BackendMode = _backendModeCombo.SelectedIndex == 1 ? "external" : "local";
            if (_settings.BackendMode != "local" && _isKoboldRunning)
                StopKoboldCpp();
            UpdateBackendUIVisibility();
            AutoSaveConfig();
            if (_settings.BackendMode == "external" && string.IsNullOrWhiteSpace(_externalModelCombo?.Text))
                await PopulateModelsForProviderAsync(_settings.ExternalProvider);
        };
        Grid.SetColumn(_backendModeCombo, 1);
        modeRow.Children.Add(_backendModeCombo);
        advPanel.Children.Add(modeRow);

        // External provider selector (visible in external mode)
        var provRow = new Grid { Margin = new Thickness(0, 0, 0, 6) };
        provRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(80) });
        provRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        provRow.Children.Add(new TextBlock { Text = "Provider:", Foreground = Fg, FontSize = 11, VerticalAlignment = VerticalAlignment.Center });
        _externalProviderCombo = new ComboBox { Height = 24, FontSize = 11, Template = SettingsWindow.DarkComboTemplate, Background = InputBg, Foreground = Fg, BorderBrush = Border, Margin = new Thickness(6, 0, 0, 0), Padding = new Thickness(6, 0, 6, 0) };
        ApplyComboStyle(_externalProviderCombo);
        foreach (var kv in ProviderUrls)
            _externalProviderCombo.Items.Add(kv.Key);
        var selProv = ProviderUrls.ContainsKey(_settings.ExternalProvider) ? _settings.ExternalProvider : "OpenRouter";
        // Sync API URL to provider default on boot (unless Custom)
        if (selProv != "Custom" && ProviderUrls.TryGetValue(selProv, out var bootUrl))
            _settings.CustomApiUrl = bootUrl;
        _externalProviderCombo.SelectedItem = selProv;
        _externalProviderCombo.SelectionChanged += async (_, _) =>
        {
            var prov = _externalProviderCombo.SelectedItem as string ?? "OpenRouter";
            _settings.ExternalProvider = prov;
            if (ProviderUrls.TryGetValue(prov, out var url))
            {
                _customApiUrlBox.Text = url;
                _settings.CustomApiUrl = url;
            }
            AutoSaveConfig();
            await PopulateModelsForProviderAsync(prov);
        };
        Grid.SetColumn(_externalProviderCombo, 1);
        provRow.Children.Add(_externalProviderCombo);
        _externalProviderRow = provRow;
        advPanel.Children.Add(provRow);

        // External API URL (visible in external mode)
        var urlRow = new Grid { Margin = new Thickness(0, 0, 0, 6) };
        urlRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(80) });
        urlRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        urlRow.Children.Add(new TextBlock { Text = "API URL:", Foreground = Fg, FontSize = 11, VerticalAlignment = VerticalAlignment.Center, ToolTip = "Auto-filled by provider selection. Edit manually for 'Custom' or to override." });
        _customApiUrlBox = new TextBox { Height = 24, FontSize = 11, Background = InputBg, Foreground = Fg, BorderBrush = Border, CaretBrush = Fg, VerticalContentAlignment = VerticalAlignment.Center, Padding = new Thickness(6, 0, 6, 0), Margin = new Thickness(6, 0, 0, 0), Text = _settings.CustomApiUrl };
        _customApiUrlBox.TextChanged += (_, _) => { _settings.CustomApiUrl = _customApiUrlBox.Text; AutoSaveConfig(); };
        Grid.SetColumn(_customApiUrlBox, 1);
        urlRow.Children.Add(_customApiUrlBox);
        _customApiUrlRow = urlRow;
        advPanel.Children.Add(urlRow);

        // OpenRouter API key (visible in external mode)
        var keyRow = new Grid { Margin = new Thickness(0, 0, 0, 6) };
        keyRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(80) });
        keyRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        keyRow.Children.Add(new TextBlock { Text = "API Key:", Foreground = Fg, FontSize = 11, VerticalAlignment = VerticalAlignment.Center, ToolTip = "Required for most external APIs. Get OpenRouter key at https://openrouter.ai/keys" });
        _openRouterApiKeyBox = new TextBox { Height = 24, FontSize = 11, Background = InputBg, Foreground = Fg, BorderBrush = Border, CaretBrush = Fg, VerticalContentAlignment = VerticalAlignment.Center, Padding = new Thickness(6, 0, 6, 0), Margin = new Thickness(6, 0, 0, 0), Text = _settings.OpenRouterApiKey };
        _openRouterApiKeyBox.TextChanged += (_, _) => { _settings.OpenRouterApiKey = _openRouterApiKeyBox.Text; AutoSaveConfig(); };
        Grid.SetColumn(_openRouterApiKeyBox, 1);
        keyRow.Children.Add(_openRouterApiKeyBox);
        _openRouterKeyRow = keyRow;
        advPanel.Children.Add(keyRow);

        // Model selection (visible in external mode — populated per provider)
        var modelRow = new Grid { Margin = new Thickness(0, 0, 0, 6) };
        _openRouterModelRow = modelRow;
        _externalModelRow = modelRow;
        modelRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(80) });
        modelRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        modelRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        modelRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        modelRow.Children.Add(new TextBlock { Text = "Model:", Foreground = Fg, FontSize = 11, VerticalAlignment = VerticalAlignment.Center });
        _externalModelCombo = new ComboBox { Height = 24, FontSize = 11, Template = SettingsWindow.DarkComboTemplate, Background = InputBg, Foreground = Fg, BorderBrush = Border, Margin = new Thickness(6, 0, 0, 0), Padding = new Thickness(6, 0, 6, 0) };
        ApplyComboStyle(_externalModelCombo);
        _externalModelCombo.SelectionChanged += (_, _) =>
        {
            if (_externalModelCombo.SelectedItem is ComboBoxItem ci && ci.Tag is string mid)
            { _settings.OpenRouterModel = mid; AutoSaveConfig(); }
        };
        Grid.SetColumn(_externalModelCombo, 1);
        modelRow.Children.Add(_externalModelCombo);
        _modelFilterCombo = new ComboBox { Height = 24, FontSize = 11, Width = 80, Template = SettingsWindow.DarkComboTemplate, Background = InputBg, Foreground = Fg, BorderBrush = Border, Margin = new Thickness(4, 0, 0, 0), Padding = new Thickness(4, 0, 4, 0) };
        ApplyComboStyle(_modelFilterCombo);
        _modelFilterCombo.Items.Add(new ComboBoxItem { Content = "All", Tag = "all", IsSelected = true });
        _modelFilterCombo.Items.Add(new ComboBoxItem { Content = ":free only", Tag = "free" });
        _modelFilterCombo.SelectionChanged += (_, _) => ApplyModelFilter();
        Grid.SetColumn(_modelFilterCombo, 2);
        modelRow.Children.Add(_modelFilterCombo);
        _refreshModelsBtn = new Button { Content = "\u21BB", Width = 28, Height = 24, FontSize = 13, Cursor = Cursors.Hand, Background = Surface, Foreground = Fg, BorderBrush = Border, BorderThickness = new Thickness(1), Padding = new Thickness(0), Margin = new Thickness(4, 0, 0, 0), ToolTip = "Refresh model list" };
        _refreshModelsBtn.Click += async (_, _) => await PopulateModelsForProviderAsync(_settings.ExternalProvider);
        Grid.SetColumn(_refreshModelsBtn, 3);
        modelRow.Children.Add(_refreshModelsBtn);
        advPanel.Children.Add(modelRow);

        var plannerModelRow = new Grid { Margin = new Thickness(0, 0, 0, 6) };
        plannerModelRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(80) });
        plannerModelRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        plannerModelRow.Children.Add(new TextBlock { Text = "Planner Model:", Foreground = Fg, FontSize = 11, VerticalAlignment = VerticalAlignment.Center, ToolTip = "Path to a smaller GGUF model for generating project context summaries via local model-swapping. Set in Settings → Models → Text." });
        var plannerModelInner = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(6, 0, 0, 0) };
        _plannerModelBox = new TextBox { Height = 24, FontSize = 11, Background = InputBg, Foreground = Fg, BorderBrush = Border, CaretBrush = Fg, VerticalContentAlignment = VerticalAlignment.Center, IsReadOnly = true, Width = 200, Text = _settings.PlannerModelPath };
        _plannerModelBox.TextChanged += (_, _) => { _settings.PlannerModelPath = _plannerModelBox.Text; AutoSaveConfig(); };
        var browsePlannerBtn = new Button { Content = "Browse...", Width = 80, Height = 24, FontSize = 11, Cursor = Cursors.Hand, Background = Surface, Foreground = Fg, BorderBrush = Border, BorderThickness = new Thickness(1), Margin = new Thickness(4, 0, 0, 0) };
        browsePlannerBtn.Click += (_, _) =>
        {
            var dlg = new Microsoft.Win32.OpenFileDialog { Title = "Select Planner GGUF Model", Filter = "GGUF files (*.gguf)|*.gguf|All files (*.*)|*.*" };
            if (dlg.ShowDialog() == true) { _plannerModelBox.Text = dlg.FileName; }
        };
        plannerModelInner.Children.Add(_plannerModelBox);
        plannerModelInner.Children.Add(browsePlannerBtn);
        Grid.SetColumn(plannerModelInner, 1);
        plannerModelRow.Children.Add(plannerModelInner);
        advPanel.Children.Add(plannerModelRow);

        var plannerTemplateRow = new Grid { Margin = new Thickness(0, 0, 0, 6) };
        plannerTemplateRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(80) });
        plannerTemplateRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        plannerTemplateRow.Children.Add(new TextBlock { Text = "Template:", Foreground = Fg, FontSize = 11, VerticalAlignment = VerticalAlignment.Center, ToolTip = "Path to a .md file containing the planner output skeleton/template. The planner formats its analysis using this structure. Set in Settings → Text." });
        var tmplInner = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(6, 0, 0, 0) };
        _plannerTemplateBox = new TextBox { Height = 24, FontSize = 11, Background = InputBg, Foreground = Fg, BorderBrush = Border, CaretBrush = Fg, VerticalContentAlignment = VerticalAlignment.Center, IsReadOnly = true, Width = 200, Text = _settings.PlannerTemplatePath };
        _plannerTemplateBox.TextChanged += (_, _) => { _settings.PlannerTemplatePath = _plannerTemplateBox.Text; AutoSaveConfig(); };
        var browseTmplBtn = new Button { Content = "Browse...", Width = 80, Height = 24, FontSize = 11, Cursor = Cursors.Hand, Background = Surface, Foreground = Fg, BorderBrush = Border, BorderThickness = new Thickness(1), Margin = new Thickness(4, 0, 0, 0) };
        browseTmplBtn.Click += (_, _) =>
        {
            var dlg = new Microsoft.Win32.OpenFileDialog { Title = "Select Planner Template", Filter = "Markdown files (*.md)|*.md|All files (*.*)|*.*" };
            if (dlg.ShowDialog() == true) { _plannerTemplateBox.Text = dlg.FileName; }
        };
        tmplInner.Children.Add(_plannerTemplateBox);
        tmplInner.Children.Add(browseTmplBtn);
        Grid.SetColumn(tmplInner, 1);
        plannerTemplateRow.Children.Add(tmplInner);
        advPanel.Children.Add(plannerTemplateRow);

        var ctxRow = MakeSubRow("Context Size:");
        var ctxInner = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0) };
        _textContextSlider = new Slider { Minimum = 512, Maximum = 65536, Value = _settings.ContextSize, TickFrequency = 512, IsSnapToTickEnabled = true, Width = 140, Height = 20, VerticalAlignment = VerticalAlignment.Center, Style = SliderStyle, Foreground = Accent, Background = Border, BorderBrush = Brushes.Transparent };
        _textContextValue = new TextBox { Text = _settings.ContextSize.ToString(), Width = 65, Height = 22, FontSize = 11, Background = InputBg, Foreground = Fg, BorderBrush = Border, CaretBrush = Fg, VerticalContentAlignment = VerticalAlignment.Center, HorizontalContentAlignment = HorizontalAlignment.Center, Margin = new Thickness(6, 0, 0, 0) };
        _textContextSlider.ValueChanged += (_, e) => { var val = (int)e.NewValue; _textContextValue.Text = val.ToString(); _settings.ContextSize = val; AutoSaveConfig(); };
        _textContextValue.TextChanged += (_, _) => { if (int.TryParse(_textContextValue.Text, out var val) && val >= 512 && val <= 65536) { _textContextSlider.Value = val; _settings.ContextSize = val; AutoSaveConfig(); } };
        ctxInner.Children.Add(_textContextSlider);
        ctxInner.Children.Add(_textContextValue);
        Grid.SetColumn(ctxInner, 1);
        ctxRow.Children.Add(ctxInner);
        advPanel.Children.Add(ctxRow);

        _textBatchSizeBox = MakeInputRow(advPanel, "Batch Size:", _settings.BatchSize.ToString(), val => { if (int.TryParse(val, out var n) && n > 0) { _settings.BatchSize = n; AutoSaveConfig(); } });
        _textBlasBatchBox = MakeInputRow(advPanel, "BLAS Batch:", _settings.BlasBatchSize.ToString(), val => { if (int.TryParse(val, out var n) && n > 0) { _settings.BlasBatchSize = n; AutoSaveConfig(); } });
        _textGpuLayersBox = MakeInputRow(advPanel, "GPU Layers:", _settings.GpuLayers.ToString(), val => { if (int.TryParse(val, out var n)) { _settings.GpuLayers = n; AutoSaveConfig(); } });
        advPanel.Children.Add(new Rectangle { Height = 4, Fill = Brushes.Transparent });

        var noCertifyRow = new Grid { Margin = new Thickness(0, 0, 0, 6) };
        noCertifyRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(80) });
        noCertifyRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        noCertifyRow.Children.Add(new TextBlock { Text = "No Certify:", Foreground = Fg, FontSize = 11, VerticalAlignment = VerticalAlignment.Center });
        _textNoCertify = new ComboBox { Height = 24, FontSize = 11, Template = SettingsWindow.DarkComboTemplate, Background = InputBg, Foreground = Fg, BorderBrush = Border, Margin = new Thickness(6, 0, 0, 0), Padding = new Thickness(6, 0, 6, 0) };
        var ncItemStyle = new Style(typeof(ComboBoxItem));
        ncItemStyle.Setters.Add(new Setter(Control.BackgroundProperty, Surface));
        ncItemStyle.Setters.Add(new Setter(Control.ForegroundProperty, Fg));
        var ncHover = new Trigger { Property = ComboBoxItem.IsMouseOverProperty, Value = true };
        ncHover.Setters.Add(new Setter(Control.BackgroundProperty, Highlight));
        ncItemStyle.Triggers.Add(ncHover);
        _textNoCertify.ItemContainerStyle = ncItemStyle;
        _textNoCertify.Items.Add("Disable");
        _textNoCertify.Items.Add("Enable");
        _textNoCertify.SelectedIndex = _settings.NoCertify ? 1 : 0;
        Grid.SetColumn(_textNoCertify, 1);
        noCertifyRow.Children.Add(_textNoCertify);
        advPanel.Children.Add(noCertifyRow);

        var toolsRow = new Grid { Margin = new Thickness(0, 0, 0, 6) };
        toolsRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(80) });
        toolsRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        toolsRow.Children.Add(new TextBlock { Text = "Tools(EXP):", Foreground = Fg, FontSize = 11, VerticalAlignment = VerticalAlignment.Center, ToolTip = "Send structured tool definitions (function calling) to the local backend" });
        _toolsCheck = new CheckBox { IsChecked = _settings.SendToolsToLocalBackend, Foreground = Fg, FontSize = 12, ToolTip = "When disabled, tools are only sent to OpenRouter/external backends. Local KoboldCpp will use text-based instruction calling instead." };
        _toolsCheck.Checked += (_, _) => { _settings.SendToolsToLocalBackend = true; AutoSaveConfig(); };
        _toolsCheck.Unchecked += (_, _) => { _settings.SendToolsToLocalBackend = false; AutoSaveConfig(); };
        Grid.SetColumn(_toolsCheck, 1);
        toolsRow.Children.Add(_toolsCheck);
        advPanel.Children.Add(toolsRow);

        var debugRow = new Grid { Margin = new Thickness(0, 0, 0, 6) };
        debugRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(80) });
        debugRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        debugRow.Children.Add(new TextBlock { Text = "Debug:", Foreground = Fg, FontSize = 11, VerticalAlignment = VerticalAlignment.Center, ToolTip = "Show block warnings in the agent transcript (e.g. \"Read loop guard triggered — reading file blocked\", \"FileNotFound Guard Triggered\", and other BLOCKED tool results). Default off hides them from the UI only — the model still sees every tool result." });
        _debugCheck = new CheckBox { IsChecked = _settings.DebugShowBlockWarnings, Foreground = Fg, FontSize = 12, ToolTip = "When off (default), blocked tool results are hidden from the transcript but still sent to the model." };
        _debugCheck.Checked += (_, _) => { _settings.DebugShowBlockWarnings = true; AutoSaveConfig(); };
        _debugCheck.Unchecked += (_, _) => { _settings.DebugShowBlockWarnings = false; AutoSaveConfig(); };
        Grid.SetColumn(_debugCheck, 1);
        debugRow.Children.Add(_debugCheck);
        advPanel.Children.Add(debugRow);

        var plannerRow = new Grid { Margin = new Thickness(0, 0, 0, 6) };
        plannerRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(80) });
        plannerRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        plannerRow.Children.Add(new TextBlock { Text = "Planner:", Foreground = Fg, FontSize = 11, VerticalAlignment = VerticalAlignment.Center, ToolTip = "Enable the secondary planner model to generate project-context summaries before each agent step. The planner model is loaded temporarily, generates a plan, then is swapped out for the main text model." });
        _plannerEnabledCombo = new ComboBox { Height = 24, FontSize = 11, Template = SettingsWindow.DarkComboTemplate, Background = InputBg, Foreground = Fg, BorderBrush = Border, Margin = new Thickness(6, 0, 0, 0), Padding = new Thickness(6, 0, 6, 0) };
        var plItemStyle = new Style(typeof(ComboBoxItem));
        plItemStyle.Setters.Add(new Setter(Control.BackgroundProperty, Surface));
        plItemStyle.Setters.Add(new Setter(Control.ForegroundProperty, Fg));
        var plHover = new Trigger { Property = ComboBoxItem.IsMouseOverProperty, Value = true };
        plHover.Setters.Add(new Setter(Control.BackgroundProperty, Highlight));
        plItemStyle.Triggers.Add(plHover);
        _plannerEnabledCombo.ItemContainerStyle = plItemStyle;
        _plannerEnabledCombo.Items.Add("Disable");
        _plannerEnabledCombo.Items.Add("Enable");
        _plannerEnabledCombo.SelectedIndex = _settings.PlannerEnabled ? 1 : 0;
        _plannerEnabledCombo.SelectionChanged += (_, _) => { _settings.PlannerEnabled = _plannerEnabledCombo.SelectedIndex == 1; AutoSaveConfig(); };
        Grid.SetColumn(_plannerEnabledCombo, 1);
        plannerRow.Children.Add(_plannerEnabledCombo);
        advPanel.Children.Add(plannerRow);

        var tempRow = MakeSubRow("Temperature:");
        _textTempSlider = new Slider { Minimum = 0, Maximum = 2, Value = _settings.TextTemperature, TickFrequency = 0.05, IsSnapToTickEnabled = true, Width = 140, Height = 20, VerticalAlignment = VerticalAlignment.Center, Style = SliderStyle, Foreground = Accent, Background = Border, BorderBrush = Brushes.Transparent };
        _textTempValue = new TextBox { Text = _settings.TextTemperature.ToString("F2"), Width = 55, Height = 22, FontSize = 11, Background = InputBg, Foreground = Fg, BorderBrush = Border, CaretBrush = Fg, VerticalContentAlignment = VerticalAlignment.Center, HorizontalContentAlignment = HorizontalAlignment.Center, Margin = new Thickness(6, 0, 0, 0) };
        _textTempSlider.ValueChanged += (_, e) => { var val = (float)e.NewValue; _textTempValue.Text = val.ToString("F2"); _settings.TextTemperature = val; AutoSaveConfig(); };
        _textTempValue.TextChanged += (_, _) => { if (float.TryParse(_textTempValue.Text, out var val) && val >= 0 && val <= 2) { _textTempSlider.Value = val; _settings.TextTemperature = val; AutoSaveConfig(); } };
        var tempInner = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0) };
        tempInner.Children.Add(_textTempSlider);
        tempInner.Children.Add(_textTempValue);
        Grid.SetColumn(tempInner, 1);
        tempRow.Children.Add(tempInner);
        advPanel.Children.Add(tempRow);

        var topPRow = MakeSubRow("Top P:");
        _textTopPSlider = new Slider { Minimum = 0, Maximum = 1, Value = _settings.TextTopP, TickFrequency = 0.05, IsSnapToTickEnabled = true, Width = 140, Height = 20, VerticalAlignment = VerticalAlignment.Center, Style = SliderStyle, Foreground = Accent, Background = Border, BorderBrush = Brushes.Transparent };
        _textTopPValue = new TextBox { Text = _settings.TextTopP.ToString("F2"), Width = 55, Height = 22, FontSize = 11, Background = InputBg, Foreground = Fg, BorderBrush = Border, CaretBrush = Fg, VerticalContentAlignment = VerticalAlignment.Center, HorizontalContentAlignment = HorizontalAlignment.Center, Margin = new Thickness(6, 0, 0, 0) };
        _textTopPSlider.ValueChanged += (_, e) => { var val = (float)e.NewValue; _textTopPValue.Text = val.ToString("F2"); _settings.TextTopP = val; AutoSaveConfig(); };
        _textTopPValue.TextChanged += (_, _) => { if (float.TryParse(_textTopPValue.Text, out var val) && val >= 0 && val <= 1) { _textTopPSlider.Value = val; _settings.TextTopP = val; AutoSaveConfig(); } };
        var topPInner = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0) };
        topPInner.Children.Add(_textTopPSlider);
        topPInner.Children.Add(_textTopPValue);
        Grid.SetColumn(topPInner, 1);
        topPRow.Children.Add(topPInner);
        advPanel.Children.Add(topPRow);

        var topKRow = MakeSubRow("Top K:");
        _textTopKSlider = new Slider { Minimum = 0, Maximum = 200, Value = _settings.TextTopK, TickFrequency = 1, IsSnapToTickEnabled = true, Width = 140, Height = 20, VerticalAlignment = VerticalAlignment.Center, Style = SliderStyle, Foreground = Accent, Background = Border, BorderBrush = Brushes.Transparent };
        _textTopKValue = new TextBox { Text = _settings.TextTopK.ToString("F0"), Width = 55, Height = 22, FontSize = 11, Background = InputBg, Foreground = Fg, BorderBrush = Border, CaretBrush = Fg, VerticalContentAlignment = VerticalAlignment.Center, HorizontalContentAlignment = HorizontalAlignment.Center, Margin = new Thickness(6, 0, 0, 0) };
        _textTopKSlider.ValueChanged += (_, e) => { var val = (int)e.NewValue; _textTopKValue.Text = val.ToString(); _settings.TextTopK = val; AutoSaveConfig(); };
        _textTopKValue.TextChanged += (_, _) => { if (int.TryParse(_textTopKValue.Text, out var val) && val >= 0 && val <= 200) { _textTopKSlider.Value = val; _settings.TextTopK = val; AutoSaveConfig(); } };
        var topKInner = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0) };
        topKInner.Children.Add(_textTopKSlider);
        topKInner.Children.Add(_textTopKValue);
        Grid.SetColumn(topKInner, 1);
        topKRow.Children.Add(topKInner);
        advPanel.Children.Add(topKRow);

        var repPenRow = MakeSubRow("Repeat Penalty:");
        _textRepPenSlider = new Slider { Minimum = 1.0, Maximum = 2.0, Value = _settings.TextRepeatPenalty, TickFrequency = 0.01, IsSnapToTickEnabled = true, Width = 140, Height = 20, VerticalAlignment = VerticalAlignment.Center, Style = SliderStyle, Foreground = Accent, Background = Border, BorderBrush = Brushes.Transparent };
        _textRepPenValue = new TextBox { Text = _settings.TextRepeatPenalty.ToString("F2"), Width = 55, Height = 22, FontSize = 11, Background = InputBg, Foreground = Fg, BorderBrush = Border, CaretBrush = Fg, VerticalContentAlignment = VerticalAlignment.Center, HorizontalContentAlignment = HorizontalAlignment.Center, Margin = new Thickness(6, 0, 0, 0) };
        _textRepPenSlider.ValueChanged += (_, e) => { var val = (float)e.NewValue; _textRepPenValue.Text = val.ToString("F2"); _settings.TextRepeatPenalty = val; AutoSaveConfig(); };
        _textRepPenValue.TextChanged += (_, _) => { if (float.TryParse(_textRepPenValue.Text, out var val) && val >= 1.0 && val <= 2.0) { _textRepPenSlider.Value = val; _settings.TextRepeatPenalty = val; AutoSaveConfig(); } };
        var repPenInner = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0) };
        repPenInner.Children.Add(_textRepPenSlider);
        repPenInner.Children.Add(_textRepPenValue);
        Grid.SetColumn(repPenInner, 1);
        repPenRow.Children.Add(repPenInner);
        advPanel.Children.Add(repPenRow);

        var timeoutRow = MakeSubRow("Timeout (s):");
        _textTimeoutBox = new TextBox { Text = _settings.TextTimeoutSeconds.ToString(), Width = 65, Height = 22, FontSize = 11, Background = InputBg, Foreground = Fg, BorderBrush = Border, CaretBrush = Fg, VerticalContentAlignment = VerticalAlignment.Center, HorizontalContentAlignment = HorizontalAlignment.Center, Margin = new Thickness(6, 0, 0, 0) };
        _textTimeoutBox.TextChanged += (_, _) => { if (int.TryParse(_textTimeoutBox.Text, out var val) && val >= 0) { _settings.TextTimeoutSeconds = val; AutoSaveConfig(); } };
        Grid.SetColumn(_textTimeoutBox, 1);
        timeoutRow.Children.Add(_textTimeoutBox);
        advPanel.Children.Add(timeoutRow);

        advPanel.Children.Add(new Rectangle { Height = 4, Fill = Brushes.Transparent });
        advPanel.Children.Add(new TextBlock { Text = "Guardrails:", Foreground = FgDim, FontSize = 11, Margin = new Thickness(0, 0, 0, 4) });

        var maxIterRow = MakeSubRow("Max Iters:");
        _maxIterSlider = new Slider { Minimum = 1, Maximum = 200, Value = _settings.MaxIterations, TickFrequency = 1, IsSnapToTickEnabled = true, Width = 140, Height = 20, VerticalAlignment = VerticalAlignment.Center, Style = SliderStyle, Foreground = Accent, Background = Border, BorderBrush = Brushes.Transparent };
        _maxIterValue = new TextBox { Text = _settings.MaxIterations.ToString(), Width = 55, Height = 22, FontSize = 11, Background = InputBg, Foreground = Fg, BorderBrush = Border, CaretBrush = Fg, VerticalContentAlignment = VerticalAlignment.Center, HorizontalContentAlignment = HorizontalAlignment.Center, Margin = new Thickness(6, 0, 0, 0) };
        _maxIterSlider.ValueChanged += (_, e) => { var val = (int)e.NewValue; _maxIterValue.Text = val.ToString(); _settings.MaxIterations = val; AutoSaveConfig(); };
        _maxIterValue.TextChanged += (_, _) => { if (int.TryParse(_maxIterValue.Text, out var val) && val >= 1 && val <= 200) { _maxIterSlider.Value = val; _settings.MaxIterations = val; AutoSaveConfig(); } };
        var maxIterInner = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0) };
        maxIterInner.Children.Add(_maxIterSlider);
        maxIterInner.Children.Add(_maxIterValue);
        Grid.SetColumn(maxIterInner, 1);
        maxIterRow.Children.Add(maxIterInner);
        advPanel.Children.Add(maxIterRow);

        var stallNudgeRow = MakeSubRow("Stall Nudge:");
        _stallNudgeSlider = new Slider { Minimum = 1, Maximum = 50, Value = _settings.StallNudgeThreshold, TickFrequency = 1, IsSnapToTickEnabled = true, Width = 140, Height = 20, VerticalAlignment = VerticalAlignment.Center, Style = SliderStyle, Foreground = Accent, Background = Border, BorderBrush = Brushes.Transparent };
        _stallNudgeValue = new TextBox { Text = _settings.StallNudgeThreshold.ToString(), Width = 55, Height = 22, FontSize = 11, Background = InputBg, Foreground = Fg, BorderBrush = Border, CaretBrush = Fg, VerticalContentAlignment = VerticalAlignment.Center, HorizontalContentAlignment = HorizontalAlignment.Center, Margin = new Thickness(6, 0, 0, 0) };
        _stallNudgeSlider.ValueChanged += (_, e) => { var val = (int)e.NewValue; _stallNudgeValue.Text = val.ToString(); _settings.StallNudgeThreshold = val; AutoSaveConfig(); };
        _stallNudgeValue.TextChanged += (_, _) => { if (int.TryParse(_stallNudgeValue.Text, out var val) && val >= 1 && val <= 50) { _stallNudgeSlider.Value = val; _settings.StallNudgeThreshold = val; AutoSaveConfig(); } };
        var stallNudgeInner = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0) };
        stallNudgeInner.Children.Add(_stallNudgeSlider);
        stallNudgeInner.Children.Add(_stallNudgeValue);
        Grid.SetColumn(stallNudgeInner, 1);
        stallNudgeRow.Children.Add(stallNudgeInner);
        advPanel.Children.Add(stallNudgeRow);

        var stallLockoutRow = MakeSubRow("Stall Lockout:");
        _stallLockoutSlider = new Slider { Minimum = 1, Maximum = 50, Value = _settings.StallLockoutThreshold, TickFrequency = 1, IsSnapToTickEnabled = true, Width = 140, Height = 20, VerticalAlignment = VerticalAlignment.Center, Style = SliderStyle, Foreground = Accent, Background = Border, BorderBrush = Brushes.Transparent };
        _stallLockoutValue = new TextBox { Text = _settings.StallLockoutThreshold.ToString(), Width = 55, Height = 22, FontSize = 11, Background = InputBg, Foreground = Fg, BorderBrush = Border, CaretBrush = Fg, VerticalContentAlignment = VerticalAlignment.Center, HorizontalContentAlignment = HorizontalAlignment.Center, Margin = new Thickness(6, 0, 0, 0) };
        _stallLockoutSlider.ValueChanged += (_, e) => { var val = (int)e.NewValue; _stallLockoutValue.Text = val.ToString(); _settings.StallLockoutThreshold = val; AutoSaveConfig(); };
        _stallLockoutValue.TextChanged += (_, _) => { if (int.TryParse(_stallLockoutValue.Text, out var val) && val >= 1 && val <= 50) { _stallLockoutSlider.Value = val; _settings.StallLockoutThreshold = val; AutoSaveConfig(); } };
        var stallLockoutInner = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0) };
        stallLockoutInner.Children.Add(_stallLockoutSlider);
        stallLockoutInner.Children.Add(_stallLockoutValue);
        Grid.SetColumn(stallLockoutInner, 1);
        stallLockoutRow.Children.Add(stallLockoutInner);
        advPanel.Children.Add(stallLockoutRow);

        var readNudgeRow = MakeSubRow("Read Nudge:");
        _readNudgeSlider = new Slider { Minimum = 1, Maximum = 50, Value = _settings.ReadFileNudgeThreshold, TickFrequency = 1, IsSnapToTickEnabled = true, Width = 140, Height = 20, VerticalAlignment = VerticalAlignment.Center, Style = SliderStyle, Foreground = Accent, Background = Border, BorderBrush = Brushes.Transparent };
        _readNudgeValue = new TextBox { Text = _settings.ReadFileNudgeThreshold.ToString(), Width = 55, Height = 22, FontSize = 11, Background = InputBg, Foreground = Fg, BorderBrush = Border, CaretBrush = Fg, VerticalContentAlignment = VerticalAlignment.Center, HorizontalContentAlignment = HorizontalAlignment.Center, Margin = new Thickness(6, 0, 0, 0) };
        _readNudgeSlider.ValueChanged += (_, e) => { var val = (int)e.NewValue; _readNudgeValue.Text = val.ToString(); _settings.ReadFileNudgeThreshold = val; AutoSaveConfig(); };
        _readNudgeValue.TextChanged += (_, _) => { if (int.TryParse(_readNudgeValue.Text, out var val) && val >= 1 && val <= 50) { _readNudgeSlider.Value = val; _settings.ReadFileNudgeThreshold = val; AutoSaveConfig(); } };
        var readNudgeInner = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0) };
        readNudgeInner.Children.Add(_readNudgeSlider);
        readNudgeInner.Children.Add(_readNudgeValue);
        Grid.SetColumn(readNudgeInner, 1);
        readNudgeRow.Children.Add(readNudgeInner);
        advPanel.Children.Add(readNudgeRow);

        var readHardStopRow = MakeSubRow("Read Hard Stop:");
        _readHardStopSlider = new Slider { Minimum = 1, Maximum = 50, Value = _settings.ReadFileHardStopThreshold, TickFrequency = 1, IsSnapToTickEnabled = true, Width = 140, Height = 20, VerticalAlignment = VerticalAlignment.Center, Style = SliderStyle, Foreground = Accent, Background = Border, BorderBrush = Brushes.Transparent };
        _readHardStopValue = new TextBox { Text = _settings.ReadFileHardStopThreshold.ToString(), Width = 55, Height = 22, FontSize = 11, Background = InputBg, Foreground = Fg, BorderBrush = Border, CaretBrush = Fg, VerticalContentAlignment = VerticalAlignment.Center, HorizontalContentAlignment = HorizontalAlignment.Center, Margin = new Thickness(6, 0, 0, 0) };
        _readHardStopSlider.ValueChanged += (_, e) => { var val = (int)e.NewValue; _readHardStopValue.Text = val.ToString(); _settings.ReadFileHardStopThreshold = val; AutoSaveConfig(); };
        _readHardStopValue.TextChanged += (_, _) => { if (int.TryParse(_readHardStopValue.Text, out var val) && val >= 1 && val <= 50) { _readHardStopSlider.Value = val; _settings.ReadFileHardStopThreshold = val; AutoSaveConfig(); } };
        var readHardStopInner = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0) };
        readHardStopInner.Children.Add(_readHardStopSlider);
        readHardStopInner.Children.Add(_readHardStopValue);
        Grid.SetColumn(readHardStopInner, 1);
        readHardStopRow.Children.Add(readHardStopInner);
        advPanel.Children.Add(readHardStopRow);

        var noShiftRow = new Grid { Margin = new Thickness(0, 0, 0, 6) };
        noShiftRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(80) });
        noShiftRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        noShiftRow.Children.Add(new TextBlock { Text = "Agentic No Shift:", Foreground = Fg, FontSize = 11, VerticalAlignment = VerticalAlignment.Center, ToolTip = "Force context shift and fast forwarding off during agentic workflow" });
        _textAgenticNoShift = new ComboBox { Height = 24, FontSize = 11, Template = SettingsWindow.DarkComboTemplate, Background = InputBg, Foreground = Fg, BorderBrush = Border, Margin = new Thickness(6, 0, 0, 0), Padding = new Thickness(6, 0, 6, 0) };
        var nsItemStyle = new Style(typeof(ComboBoxItem));
        nsItemStyle.Setters.Add(new Setter(Control.BackgroundProperty, Surface));
        nsItemStyle.Setters.Add(new Setter(Control.ForegroundProperty, Fg));
        var nsHover = new Trigger { Property = ComboBoxItem.IsMouseOverProperty, Value = true };
        nsHover.Setters.Add(new Setter(Control.BackgroundProperty, Highlight));
        nsItemStyle.Triggers.Add(nsHover);
        _textAgenticNoShift.ItemContainerStyle = nsItemStyle;
        _textAgenticNoShift.Items.Add("Disable");
        _textAgenticNoShift.Items.Add("Enable");
        _textAgenticNoShift.SelectedIndex = _settings.AgenticNoShift == "enable" ? 1 : 0;
        _textAgenticNoShift.SelectionChanged += (_, _) => { _settings.AgenticNoShift = _textAgenticNoShift.SelectedIndex == 1 ? "enable" : "disable"; AutoSaveConfig(); };
        Grid.SetColumn(_textAgenticNoShift, 1);
        noShiftRow.Children.Add(_textAgenticNoShift);
        advPanel.Children.Add(noShiftRow);

        advPanel.Children.Add(new Rectangle { Height = 6, Fill = Brushes.Transparent });
        var reloadBtn = new Button { Content = "Reload to Apply", FontSize = 12, Height = 28, HorizontalAlignment = HorizontalAlignment.Left, Background = Surface, Foreground = Fg, BorderBrush = Accent, BorderThickness = new Thickness(1), Cursor = Cursors.Hand, Padding = new Thickness(10, 0, 10, 0) };
        reloadBtn.Click += (_, _) => { AutoSaveConfig(); if (_isKoboldRunning) RestartKoboldIfRunning(); else Log("Settings saved."); };
        advPanel.Children.Add(reloadBtn);

        advExpander.Content = advPanel;
        textOptionsPanel.Children.Add(advExpander);

        s.Children.Add(textOptionsPanel);
        return s;
    }

    private StackPanel BuildSessionsPanel()
    {
        var s = new StackPanel { Margin = new Thickness(4, 8, 4, 8) };

        var headerRow = new Grid { Margin = new Thickness(0, 8, 0, 4) };
        headerRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        headerRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        headerRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        headerRow.Children.Add(new TextBlock { Text = "Sessions", FontSize = 11, FontWeight = FontWeight.FromOpenTypeWeight(600), Foreground = Accent, VerticalAlignment = VerticalAlignment.Center });
        var addBtn = new Button { Content = "+", Width = 22, Height = 20, FontSize = 13, Cursor = Cursors.Hand, Background = Surface, Foreground = Fg, BorderThickness = new Thickness(1), BorderBrush = Border, Padding = new Thickness(0), VerticalContentAlignment = VerticalAlignment.Center, HorizontalContentAlignment = HorizontalAlignment.Center, ToolTip = "New session" };
        addBtn.Click += (_, _) => CreateNewSession();
        Grid.SetColumn(addBtn, 1);
        headerRow.Children.Add(addBtn);
        var delBtn = new Button { Content = "\u2212", Width = 22, Height = 20, FontSize = 13, Cursor = Cursors.Hand, Background = Surface, Foreground = Fg, BorderThickness = new Thickness(1), BorderBrush = Border, Padding = new Thickness(0), VerticalContentAlignment = VerticalAlignment.Center, HorizontalContentAlignment = HorizontalAlignment.Center, Margin = new Thickness(4, 0, 0, 0), ToolTip = "Delete active session" };
        delBtn.Click += (_, _) => DeleteActiveSession();
        Grid.SetColumn(delBtn, 2);
        headerRow.Children.Add(delBtn);
        s.Children.Add(headerRow);
        var listScroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxHeight = 200, Margin = new Thickness(0, 0, 0, 8) };
        _sessionList = new ListBox { Background = InputBg, Foreground = Fg, BorderBrush = Border, BorderThickness = new Thickness(1), Padding = new Thickness(4) };
        _sessionList.SelectionChanged += (_, _) =>
        {
            if (_sessionList.SelectedItem is ListBoxItem li && li.Tag is AgentSession sess)
                SwitchToSession(sess);
        };
        listScroll.Content = _sessionList;
        s.Children.Add(listScroll);

        s.Children.Add(SectionLabel("Active Session"));
        var titleRow = new Grid { Margin = new Thickness(0, 0, 0, 6) };
        titleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(70) });
        titleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        titleRow.Children.Add(new TextBlock { Text = "Title:", Foreground = Fg, FontSize = 11, VerticalAlignment = VerticalAlignment.Center });
        _sessionTitleBox = new TextBox { Height = 24, FontSize = 12, Background = InputBg, Foreground = Fg, BorderBrush = Border, CaretBrush = Fg, VerticalContentAlignment = VerticalAlignment.Center, Padding = new Thickness(6, 0, 6, 0), Margin = new Thickness(6, 0, 0, 0) };
        _sessionTitleBox.TextChanged += (_, _) =>
        {
            if (_activeSession != null && _sessionTitleBox.Text != _activeSession.Title)
            {
                _activeSession.Title = _sessionTitleBox.Text;
                _activeSession.UpdatedAt = DateTime.UtcNow;
                UpdateSessionList();
                AutoSaveConfig();
            }
        };
        Grid.SetColumn(_sessionTitleBox, 1);
        titleRow.Children.Add(_sessionTitleBox);
        s.Children.Add(titleRow);

        var projRow = new Grid { Margin = new Thickness(0, 0, 0, 6) };
        projRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(70) });
        projRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        projRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        projRow.Children.Add(new TextBlock { Text = "Project:", Foreground = Fg, FontSize = 11, VerticalAlignment = VerticalAlignment.Center });
        _sessionProjectBox = new TextBox { Height = 24, FontSize = 11, Background = InputBg, Foreground = Fg, BorderBrush = Border, CaretBrush = Fg, VerticalContentAlignment = VerticalAlignment.Center, Padding = new Thickness(6, 0, 6, 0), Margin = new Thickness(6, 0, 0, 0) };
        var projBrowse = new Button { Content = "...", Width = 28, Height = 24, FontSize = 11, Cursor = Cursors.Hand, Background = Surface, Foreground = Fg, BorderBrush = Border, BorderThickness = new Thickness(1), Padding = new Thickness(0), Margin = new Thickness(4, 0, 0, 0) };
        projBrowse.Click += (_, _) =>
        {
            var dlg = new OpenFolderDialog { Title = "Select Project Folder" };
            if (dlg.ShowDialog(this) == true)
            {
                if (_activeSession != null)
                {
                    _activeSession.ProjectPath = dlg.FolderName;
                    _activeSession.ResetAgenticState();
                    _activeSession.UpdatedAt = DateTime.UtcNow;
                    _sessionProjectBox.Text = dlg.FolderName;
                    UpdateSessionList();
                    AutoSaveConfig();
                    Log($"Project folder set: {dlg.FolderName}");
                }
            }
        };
        Grid.SetColumn(_sessionProjectBox, 1);
        projRow.Children.Add(_sessionProjectBox);
        Grid.SetColumn(projBrowse, 2);
        projRow.Children.Add(projBrowse);
        s.Children.Add(projRow);

        // Trigger initial visibility
        Dispatcher.BeginInvoke(() => UpdateBackendUIVisibility());

        return s;
    }

    private void CreateNewSession()
    {
        var sess = new AgentSession { Title = $"Session {_sessions.Count + 1}", ProjectPath = "" };
        _settings.Sessions.Add(sess);
        _sessions.Add(sess);
        SwitchToSession(sess);
        Log($"Created session: {sess.Title}");
    }

    private void DeleteActiveSession()
    {
        if (_activeSession == null) return;
        if (_sessions.Count <= 1) { Log("Cannot delete the last session."); return; }
        var toRemove = _activeSession;
        _sessions.Remove(toRemove);
        _settings.Sessions.Remove(toRemove);
        SwitchToSession(_sessions[0]);
        Log($"Deleted session: {toRemove.Title}");
    }

    private void SwitchToSession(AgentSession session)
    {
        if (_activeSession != null)
        {
            _activeSession.Messages = _chatHistory.ToList();
            _activeSession.UpdatedAt = DateTime.UtcNow;
        }
        _activeSession = session;
        _settings.ActiveSessionId = session.Id;
        _chatHistory.Clear();
        foreach (var msg in session.Messages) _chatHistory.Add(msg);
        if (_sessionTitleBox != null) _sessionTitleBox.Text = session.Title;
        if (_sessionProjectBox != null) _sessionProjectBox.Text = session.ProjectPath;
        UpdateSessionList();
        ScrollChatToEnd();
        AutoSaveConfig();
    }

    private void UpdateSessionList()
    {
        if (_sessionList == null) return;
        _sessionList.Items.Clear();
        foreach (var s in _sessions)
        {
            var isActive = s.Id == _activeSession?.Id;
            var item = new ListBoxItem
            {
                Tag = s,
                Content = $"{s.Title}{(!string.IsNullOrEmpty(s.ProjectPath) ? "  📁" : "")}{(isActive ? "  ●" : "")}",
                Foreground = isActive ? Brushes.White : Fg,
                Background = isActive ? AccentBg : Brushes.Transparent,
                Padding = new Thickness(8, 6, 8, 6),
                Cursor = Cursors.Hand,
                FontSize = 12
            };
            _sessionList.Items.Add(item);
        }
    }

    private FrameworkElement CreateTabControl(Func<StackPanel> buildConfigPanel, Func<StackPanel> buildSessionsPanel)
    {
        var headerBrush = new SolidColorBrush(Color.FromRgb(0x26, 0x26, 0x2C));
        var dimFg = new SolidColorBrush(Color.FromRgb(0x96, 0x96, 0xA2));
        var tabBg = new SolidColorBrush(Color.FromRgb(0x1A, 0x1A, 0x20));

        var headerGrid = new Grid();
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var configLabel = new TextBlock
        {
            Text = "Config",
            FontSize = 13,
            Foreground = Brushes.White,
            Cursor = Cursors.Hand,
            Padding = new Thickness(14, 6, 14, 8)
        };
        Grid.SetColumn(configLabel, 0);
        headerGrid.Children.Add(configLabel);

        var sep = new Rectangle
        {
            Width = 1,
            Fill = BorderAlt,
            VerticalAlignment = VerticalAlignment.Stretch,
            Margin = new Thickness(0, 6, 0, 6)
        };
        Grid.SetColumn(sep, 1);
        headerGrid.Children.Add(sep);

        var sessionsLabel = new TextBlock
        {
            Text = "Sessions",
            FontSize = 13,
            Padding = new Thickness(14, 6, 4, 8),
            VerticalAlignment = VerticalAlignment.Center
        };
        var lockIcon = new TextBlock
        {
            Text = "\U0001F512",
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center,
            Padding = new Thickness(0, 6, 14, 8)
        };
        var sessionsStack = new StackPanel { Orientation = Orientation.Horizontal };
        sessionsStack.Children.Add(sessionsLabel);
        sessionsStack.Children.Add(lockIcon);
        Grid.SetColumn(sessionsStack, 2);
        headerGrid.Children.Add(sessionsStack);

        var headerBorder = new Border
        {
            Child = headerGrid,
            Background = headerBrush,
            BorderThickness = new Thickness(0, 0, 0, 1),
            BorderBrush = BorderAlt
        };

        var contentGrid = new Grid();
        var configContent = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = buildConfigPanel() };
        var sessionsContent = buildSessionsPanel();
        contentGrid.Children.Add(configContent);
        contentGrid.Children.Add(sessionsContent);

        bool IsAgenticEnabled() => _textAgenticWorkflow?.SelectedIndex == 1;

        var isConfig = true;
        void UpdateTabs()
        {
            bool enabled = IsAgenticEnabled();
            configLabel.Foreground = isConfig ? Brushes.White : dimFg;
            sessionsLabel.Foreground = (!enabled || isConfig) ? dimFg : Brushes.White;
            lockIcon.Visibility = enabled ? Visibility.Collapsed : Visibility.Visible;
            sessionsLabel.Cursor = enabled ? Cursors.Hand : Cursors.Arrow;
            configContent.Visibility = isConfig ? Visibility.Visible : Visibility.Collapsed;
            sessionsContent.Visibility = isConfig ? Visibility.Collapsed : Visibility.Visible;
            headerBorder.Background = isConfig ? tabBg : headerBrush;
        }
        configLabel.MouseLeftButtonDown += (_, _) => { isConfig = true; UpdateTabs(); };
        sessionsLabel.MouseLeftButtonDown += (_, _) => { if (IsAgenticEnabled()) { isConfig = false; UpdateTabs(); } };
        UpdateTabs();

        _textAgenticWorkflow.SelectionChanged += (_, _) =>
        {
            _settings.AgenticWorkflowMode = _textAgenticWorkflow.SelectedIndex == 1 ? "enable" : "disable";
            AutoSaveConfig();
            if (_confirmRow != null)
                _confirmRow.Visibility = _textAgenticWorkflow.SelectedIndex == 1 ? Visibility.Visible : Visibility.Collapsed;
            if (IsAgenticEnabled())
            {
                if (_activeSession != null)
                {
                    _chatHistory.Clear();
                    foreach (var msg in _activeSession.Messages) _chatHistory.Add(msg);
                    ScrollChatToEnd();
                }
            }
            else
            {
                _chatHistory.Clear();
                isConfig = true;
            }
            UpdateTabs();
        };

        var outer = new StackPanel { Margin = new Thickness(0, 4, 0, 0) };
        outer.Children.Add(headerBorder);
        outer.Children.Add(contentGrid);
        return outer;
    }

    private void SyncActiveSessionToHistory()
    {
        if (_activeSession != null)
        {
            _activeSession.Messages = _chatHistory.ToList();
            _activeSession.UpdatedAt = DateTime.UtcNow;
        }
    }

    private void LoadSessionsFromSettings()
    {
        _sessions.Clear();
        if (_settings.Sessions == null || _settings.Sessions.Count == 0)
        {
            var def = new AgentSession { Title = "Default" };
            _settings.Sessions = new List<AgentSession> { def };
            _settings.ActiveSessionId = def.Id;
        }
        foreach (var s in _settings.Sessions) _sessions.Add(s);
        var active = _settings.Sessions.FirstOrDefault(s => s.Id == _settings.ActiveSessionId) ?? _settings.Sessions[0];
        SwitchToSession(active);
    }

    private StackPanel BuildAudioPanel()
    {
        var s = new StackPanel { Margin = new Thickness(12, 8, 12, 8) };

        _audioModeCombo = new ComboBox
        {
            Height = 28,
            FontSize = 12,
            Template = SettingsWindow.DarkComboTemplate,
            Background = InputBg,
            Foreground = Fg,
            BorderBrush = Border,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Margin = new Thickness(0, 0, 0, 10)
        };
        var modeItemStyle = new Style(typeof(ComboBoxItem));
        modeItemStyle.Setters.Add(new Setter(Control.BackgroundProperty, Surface));
        modeItemStyle.Setters.Add(new Setter(Control.ForegroundProperty, Fg));
        modeItemStyle.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(6, 2, 6, 2)));
        var modeHover = new Trigger { Property = ComboBoxItem.IsMouseOverProperty, Value = true };
        modeHover.Setters.Add(new Setter(Control.BackgroundProperty, Highlight));
        modeItemStyle.Triggers.Add(modeHover);
        _audioModeCombo.ItemContainerStyle = modeItemStyle;
        _audioModeCombo.Items.Add("Realtime Listening");
        _audioModeCombo.Items.Add("Transcribe File");
        _audioModeCombo.Items.Add("Voice Clone(Experimental)");
        _audioModeCombo.Items.Add("Text to Music(Experimental)");
        _audioModeCombo.SelectedIndex = 0;
        s.Children.Add(_audioModeCombo);

        var livePanel = new StackPanel { Margin = new Thickness(0) };

        livePanel.Children.Add(SectionLabel("Live Capture"));

        var captureCard = new StackPanel { Margin = new Thickness(0, 0, 0, 6) };

        _audioCaptureBtn = new Button
        {
            Content = "Start Listening",
            Height = 38,
            FontSize = 13,
            Cursor = Cursors.Hand,
            Background = Accent,
            Foreground = Brushes.White,
            BorderThickness = new Thickness(0),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Margin = new Thickness(0, 0, 0, 6)
        };
        captureCard.Children.Add(_audioCaptureBtn);

        _audioSourceCombo = new ComboBox
        {
            Height = 28,
            FontSize = 12,
            Template = SettingsWindow.DarkComboTemplate,
            Background = InputBg,
            Foreground = Fg,
            BorderBrush = Border,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Margin = new Thickness(0, 0, 0, 6)
        };
        var srcItemStyle = new Style(typeof(ComboBoxItem));
        srcItemStyle.Setters.Add(new Setter(Control.BackgroundProperty, Surface));
        srcItemStyle.Setters.Add(new Setter(Control.ForegroundProperty, Fg));
        srcItemStyle.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(6, 2, 6, 2)));
        var srcHover = new Trigger { Property = ComboBoxItem.IsMouseOverProperty, Value = true };
        srcHover.Setters.Add(new Setter(Control.BackgroundProperty, Highlight));
        srcItemStyle.Triggers.Add(srcHover);
        _audioSourceCombo.ItemContainerStyle = srcItemStyle;
        _audioSourceCombo.Items.Add("Microphone");
        _audioSourceCombo.Items.Add("Speakers (Loopback)");
        _audioSourceCombo.SelectedIndex = 1;
        captureCard.Children.Add(_audioSourceCombo);

        var optionsRow = new StackPanel { Orientation = Orientation.Horizontal };
        _audioOverlayCheck = new CheckBox
        {
            Content = "Floating overlay",
            Foreground = Fg,
            FontSize = 12,
            IsChecked = true,
            Margin = new Thickness(0, 0, 16, 0)
        };
        _audioOverlayCheck.Checked += (_, _) =>
        {
            if (_transcriber?.IsRunning == true && (_transcriptionOverlay == null || !_transcriptionOverlay.IsVisible))
            {
                _transcriptionOverlay?.Close();
                _transcriptionOverlay = new TranscriptionOverlay();
                WireOverlayEvents(_transcriptionOverlay);
                _transcriptionOverlay.Show();
            }
        };
        _audioOverlayCheck.Unchecked += (_, _) =>
        {
            _transcriptionOverlay?.Close();
            _transcriptionOverlay = null;
        };
        optionsRow.Children.Add(_audioOverlayCheck);

        _audioToPromptCheck = new CheckBox
        {
            Content = "Fill active prompt",
            Foreground = Fg,
            FontSize = 12,
            IsChecked = false
        };
        optionsRow.Children.Add(_audioToPromptCheck);
        captureCard.Children.Add(optionsRow);

        var overlayDiv = new Border
        {
            BorderBrush = BorderTertiary,
            BorderThickness = new Thickness(0, 1, 0, 0),
            Margin = new Thickness(0, 6, 0, 4),
            Padding = new Thickness(0, 6, 0, 0),
            Child = new StackPanel()
        };
        var overlayOpts = (StackPanel)overlayDiv.Child;

        var colorRow = new Grid { Margin = new Thickness(0, 0, 0, 8) };
        colorRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(70) });
        colorRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        colorRow.Children.Add(new TextBlock { Text = "Text Color:", Foreground = Fg, FontSize = 11, VerticalAlignment = VerticalAlignment.Center });
        _audioColorCombo = new ComboBox
        {
            Height = 24,
            FontSize = 11,
            Template = SettingsWindow.DarkComboTemplate,
            Background = InputBg,
            Foreground = Fg,
            BorderBrush = Border,
            Margin = new Thickness(6, 0, 0, 0),
            Padding = new Thickness(6, 0, 6, 0)
        };
        var colorItemStyle = new Style(typeof(ComboBoxItem));
        colorItemStyle.Setters.Add(new Setter(Control.BackgroundProperty, Surface));
        colorItemStyle.Setters.Add(new Setter(Control.ForegroundProperty, Fg));
        colorItemStyle.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(4, 2, 4, 2)));
        var colorHover = new Trigger { Property = ComboBoxItem.IsMouseOverProperty, Value = true };
        colorHover.Setters.Add(new Setter(Control.BackgroundProperty, Highlight));
        colorItemStyle.Triggers.Add(colorHover);
        _audioColorCombo.ItemContainerStyle = colorItemStyle;
        _overlayColorPresets = new (string Name, Color Color)[]
        {
            ("White",     Color.FromArgb(240, 245, 245, 245)),
            ("Cyan",      Color.FromArgb(240,   0, 220, 255)),
            ("Lime",      Color.FromArgb(240, 100, 255,  80)),
            ("Gold",      Color.FromArgb(240, 255, 200,  50)),
            ("Coral",     Color.FromArgb(240, 255, 110,  90)),
            ("Magenta",   Color.FromArgb(240, 255,  60, 255)),
            ("Sky Blue",  Color.FromArgb(240,  90, 170, 255)),
            ("Orange",    Color.FromArgb(240, 255, 150,  30)),
        };
        foreach (var (name, c) in _overlayColorPresets)
        {
            var brush = AppTheme.F(new SolidColorBrush(c));
            var item = new StackPanel { Orientation = Orientation.Horizontal };
            item.Children.Add(new Rectangle { Width = 12, Height = 12, Fill = brush, RadiusX = 2, RadiusY = 2, Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center });
            item.Children.Add(new TextBlock { Text = name, Foreground = Fg, FontSize = 11, VerticalAlignment = VerticalAlignment.Center });
            _audioColorCombo.Items.Add(item);
        }
        _audioColorCombo.SelectedIndex = 0;
        _audioColorCombo.SelectionChanged += (_, _) =>
        {
            if (_syncingOverlay) return;
            var idx = _audioColorCombo.SelectedIndex;
            if (idx >= 0 && idx < _overlayColorPresets.Length)
            {
                var c = _overlayColorPresets[idx].Color;
                _transcriptionOverlay?.SetTextColor(c);
            }
        };
        Grid.SetColumn(_audioColorCombo, 1);
        colorRow.Children.Add(_audioColorCombo);
        overlayOpts.Children.Add(colorRow);

        var fontTypeRow = new Grid { Margin = new Thickness(0, 0, 0, 8) };
        fontTypeRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(70) });
        fontTypeRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        fontTypeRow.Children.Add(new TextBlock { Text = "Font:", Foreground = Fg, FontSize = 11, VerticalAlignment = VerticalAlignment.Center });
        _audioFontCombo = new ComboBox
        {
            Height = 24,
            FontSize = 11,
            Template = SettingsWindow.DarkComboTemplate,
            Background = InputBg,
            Foreground = Fg,
            BorderBrush = Border,
            Margin = new Thickness(6, 0, 0, 0),
            Padding = new Thickness(6, 0, 6, 0)
        };
        var comboItemStyle = new Style(typeof(ComboBoxItem));
        comboItemStyle.Setters.Add(new Setter(Control.BackgroundProperty, Surface));
        comboItemStyle.Setters.Add(new Setter(Control.ForegroundProperty, Fg));
        comboItemStyle.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(6, 2, 6, 2)));
        var comboHover = new Trigger { Property = ComboBoxItem.IsMouseOverProperty, Value = true };
        comboHover.Setters.Add(new Setter(Control.BackgroundProperty, Highlight));
        comboItemStyle.Triggers.Add(comboHover);
        _audioFontCombo.ItemContainerStyle = comboItemStyle;
        _audioFontCombo.Items.Add("Consolas");
        _audioFontCombo.Items.Add("Segoe UI");
        _audioFontCombo.Items.Add("Arial");
        _audioFontCombo.Items.Add("Calibri");
        _audioFontCombo.Items.Add("Georgia");
        _audioFontCombo.Items.Add("Courier New");
        _audioFontCombo.Items.Add("Tahoma");
        _audioFontCombo.Items.Add("Verdana");
        _audioFontCombo.SelectedIndex = 0;
        _audioFontCombo.SelectionChanged += (_, _) =>
        {
            if (_audioFontCombo.SelectedItem is string name)
                _transcriptionOverlay?.SetFontFamily(new FontFamily(name));
        };
        Grid.SetColumn(_audioFontCombo, 1);
        fontTypeRow.Children.Add(_audioFontCombo);
        overlayOpts.Children.Add(fontTypeRow);

        var fontRow = new Grid { Margin = new Thickness(0, 0, 0, 8) };
        fontRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(70) });
        fontRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        fontRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        fontRow.Children.Add(new TextBlock { Text = "Font Size:", Foreground = Fg, FontSize = 11, VerticalAlignment = VerticalAlignment.Center });
        _audioFontSlider = new Slider { Minimum = 8, Maximum = 72, Value = 14, Height = 20, Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, Style = SliderStyle, Foreground = Accent, Background = Border, BorderBrush = Brushes.Transparent };
        _audioFontLabel = new Label { Content = "14", Foreground = Fg, FontSize = 11, Padding = new Thickness(4, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        _audioFontSlider.ValueChanged += (_, _) =>
        {
            if (_syncingOverlay) return;
            var v = (int)_audioFontSlider.Value;
            _audioFontLabel.Content = v.ToString();
            _transcriptionOverlay?.SetFontSize(v);
        };
        Grid.SetColumn(_audioFontSlider, 1);
        fontRow.Children.Add(_audioFontSlider);
        Grid.SetColumn(_audioFontLabel, 2);
        fontRow.Children.Add(_audioFontLabel);
        overlayOpts.Children.Add(fontRow);

        var opRow = new Grid { Margin = new Thickness(0, 0, 0, 8) };
        opRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(70) });
        opRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        opRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        opRow.Children.Add(new TextBlock { Text = "Opacity:", Foreground = Fg, FontSize = 11, VerticalAlignment = VerticalAlignment.Center });
        _audioOpacitySlider = new Slider { Minimum = 30, Maximum = 240, Value = 200, Height = 20, Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, Style = SliderStyle, Foreground = Accent, Background = Border, BorderBrush = Brushes.Transparent };
        _audioOpacityLabel = new Label { Content = "80%", Foreground = Fg, FontSize = 11, Padding = new Thickness(4, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        _audioOpacitySlider.ValueChanged += (_, _) =>
        {
            if (_syncingOverlay) return;
            var v = (int)_audioOpacitySlider.Value;
            _audioOpacityLabel.Content = $"{(int)(v / 240.0 * 100)}%";
            _transcriptionOverlay?.SetBgOpacity(v);
        };
        Grid.SetColumn(_audioOpacitySlider, 1);
        opRow.Children.Add(_audioOpacitySlider);
        Grid.SetColumn(_audioOpacityLabel, 2);
        opRow.Children.Add(_audioOpacityLabel);
        overlayOpts.Children.Add(opRow);

        var historyRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        historyRow.Children.Add(new TextBlock { Text = "Max History:", Foreground = Fg, FontSize = 11, VerticalAlignment = VerticalAlignment.Center, Width = 60 });
        historyRow.Children.Add(new Rectangle { Width = 8, Fill = Brushes.Transparent });
        _maxHistoryBox = new TextBox
        {
            Text = _maxHistoryCount.ToString(),
            Width = 50,
            Height = 22,
            FontSize = 11,
            Background = InputBg,
            Foreground = Fg,
            BorderBrush = Border,
            CaretBrush = Fg,
            VerticalContentAlignment = VerticalAlignment.Center,
            HorizontalContentAlignment = HorizontalAlignment.Center
        };
        _maxHistoryBox.TextChanged += (_, _) =>
        {
            if (int.TryParse(_maxHistoryBox.Text, out var val) && val > 0)
                _maxHistoryCount = val;
        };
        historyRow.Children.Add(_maxHistoryBox);
        overlayOpts.Children.Add(historyRow);

        captureCard.Children.Add(overlayDiv);
        livePanel.Children.Add(Card(captureCard));
        _audioTranslateCheck = new CheckBox
        {
            Content = "Translate to English",
            Foreground = Fg,
            FontSize = 12,
            IsChecked = false,
            Margin = new Thickness(0, 0, 0, 4)
        };
        livePanel.Children.Add(_audioTranslateCheck);

        _audioLivePanel = livePanel;

        var transcribePanel = new StackPanel { Margin = new Thickness(0) };
        transcribePanel.Children.Add(SectionLabel("Transcribe File"));

        var fileCard = new StackPanel();
        var pickRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };

        var pickBtn = new Button
        {
            Content = "Browse...",
            Height = 30,
            Width = 90,
            FontSize = 12,
            Cursor = Cursors.Hand,
            Background = Surface,
            Foreground = Fg,
            BorderThickness = new Thickness(0),
            HorizontalAlignment = HorizontalAlignment.Left
        };

        pickRow.Children.Add(pickBtn);

        _audioStatusLabel = new Label
        {
            Content = "No file selected",
            FontSize = 12,
            Foreground = FgMuted,
            Padding = new Thickness(8, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        pickRow.Children.Add(_audioStatusLabel);
        fileCard.Children.Add(pickRow);

        _audioResultBox = new TextBox
        {
            Height = 180,
            FontSize = 13,
            Background = InputBg,
            Foreground = Fg,
            BorderBrush = Border,
            CaretBrush = Fg,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Padding = new Thickness(8, 6, 26, 6),
            VerticalContentAlignment = VerticalAlignment.Top,
            IsReadOnly = true
        };

        var audioResultGrid = new Grid();
        audioResultGrid.Children.Add(_audioResultBox);

        var copyBtn = new Button
        {
            Content = "📋",
            Width = 22,
            Height = 22,
            FontSize = 11,
            Background = Surface,
            Foreground = Fg,
            BorderThickness = new Thickness(0),
            Cursor = Cursors.Hand,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 2, 2, 0),
            ToolTip = "Copy to clipboard"
        };

        copyBtn.Click += (_, _) =>
        {
            if (!string.IsNullOrEmpty(_audioResultBox.Text))
                Clipboard.SetText(_audioResultBox.Text);
        };

        audioResultGrid.Children.Add(copyBtn);

        var audioSaveBtn = new Button
        {
            Content = "💾",
            Width = 22,
            Height = 22,
            FontSize = 11,
            Background = Surface,
            Foreground = Fg,
            BorderThickness = new Thickness(0),
            Cursor = Cursors.Hand,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(24, 2, 2, 0),
            ToolTip = "Save as .txt"
        };
        audioSaveBtn.Click += (_, _) =>
        {
            if (!string.IsNullOrEmpty(_audioResultBox.Text))
            {
                var dlg = new Microsoft.Win32.SaveFileDialog
                {
                    Filter = "Text file (*.txt)|*.txt|All files (*.*)|*.*",
                    DefaultExt = ".txt",
                    FileName = "transcription.txt"
                };
                if (dlg.ShowDialog(this) == true)
                {
                    try { File.WriteAllText(dlg.FileName, _audioResultBox.Text); }
                    catch (Exception ex) { MessageBox.Show($"Failed to save: {ex.Message}"); }
                }
            }
        };
        audioResultGrid.Children.Add(audioSaveBtn);
        fileCard.Children.Add(audioResultGrid);
        transcribePanel.Children.Add(Card(fileCard));

        _audioTranscribePanel = transcribePanel;
        var voiceClonePanel = new StackPanel { Margin = new Thickness(0) };
        voiceClonePanel.Children.Add(SectionLabel("Voice Clone"));

        var refRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };

        var refBrowseBtn = new Button
        {
            Content = "Samples +",
            Height = 30,
            Width = 100,
            FontSize = 12,
            Cursor = Cursors.Hand,
            Background = Surface,
            Foreground = Fg,
            BorderThickness = new Thickness(0),
            HorizontalAlignment = HorizontalAlignment.Left
        };

        refRow.Children.Add(refBrowseBtn);

        _voiceRecordBtn = new Button
        {
            Content = "Record Mic",
            Height = 30,
            Width = 100,
            FontSize = 12,
            Cursor = Cursors.Hand,
            Background = Surface,
            Foreground = Fg,
            BorderThickness = new Thickness(0),
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(6, 0, 0, 0)
        };

        refRow.Children.Add(_voiceRecordBtn);

        _voiceRefAudioBox = new TextBox
        {
            Height = 22,
            FontSize = 11,
            Background = InputBg,
            Foreground = FgMuted,
            BorderBrush = Border,
            CaretBrush = Fg,
            VerticalContentAlignment = VerticalAlignment.Center,
            Padding = new Thickness(6, 0, 6, 0),
            IsReadOnly = true,
            Text = "No file selected",
            Margin = new Thickness(8, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center
        };

        refRow.Children.Add(_voiceRefAudioBox);
        voiceClonePanel.Children.Add(refRow);

        _voiceRefHistoryList = new ListBox
        {
            MaxHeight = 120,
            FontSize = 11,
            Background = InputBg,
            Foreground = Fg,
            BorderBrush = Border,
            Margin = new Thickness(0, 0, 0, 6),
            Padding = new Thickness(4, 2, 4, 2),
            HorizontalContentAlignment = HorizontalAlignment.Stretch
        };

        ScrollViewer.SetVerticalScrollBarVisibility(_voiceRefHistoryList, ScrollBarVisibility.Auto);
        var histItemStyle = new Style(typeof(ListBoxItem));
        histItemStyle.Setters.Add(new Setter(Control.BackgroundProperty, Surface));
        histItemStyle.Setters.Add(new Setter(Control.ForegroundProperty, Fg));
        histItemStyle.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(6, 1, 6, 1)));
        histItemStyle.Setters.Add(new Setter(Control.CursorProperty, Cursors.Hand));
        var histHover = new Trigger { Property = ListBoxItem.IsMouseOverProperty, Value = true };
        histHover.Setters.Add(new Setter(Control.BackgroundProperty, Highlight));
        histItemStyle.Triggers.Add(histHover);
        _voiceRefHistoryList.ItemContainerStyle = histItemStyle;

        _voiceRefHistoryList.SelectionChanged += (_, _) =>
        {
            if (_voiceRefHistoryList.SelectedItem is ListBoxItem li && li.Tag is string p)
            {
                _voiceRefAudioBox.Text = p;
                _voiceRefAudioBox.Foreground = Fg;
            }
        };

        voiceClonePanel.Children.Add(_voiceRefHistoryList);

        _voiceTextInput = new TextBox
        {
            Height = 100,
            FontSize = 13,
            Background = InputBg,
            Foreground = Fg,
            BorderBrush = Border,
            CaretBrush = Fg,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Padding = new Thickness(8, 6, 8, 6),
            VerticalContentAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 0, 0, 6)
        };
        voiceClonePanel.Children.Add(_voiceTextInput);

        var actionRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 4) };
        _voiceCloneBtn = new Button
        {
            Content = "Clone & Speak",
            Height = 34,
            Width = 130,
            FontSize = 13,
            Cursor = Cursors.Hand,
            Background = Accent,
            Foreground = Brushes.White,
            BorderThickness = new Thickness(0),
            HorizontalAlignment = HorizontalAlignment.Left
        };
        actionRow.Children.Add(_voiceCloneBtn);
        _voiceStatusLabel = new Label
        {
            Content = "",
            FontSize = 12,
            Foreground = FgMuted,
            Padding = new Thickness(8, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        actionRow.Children.Add(_voiceStatusLabel);
        voiceClonePanel.Children.Add(actionRow);

        var watchRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 4) };
        _voiceWatchCheck = new CheckBox
        {
            Content = "Watch .txt",
            Foreground = Fg,
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 6, 0)
        };
        watchRow.Children.Add(_voiceWatchCheck);
        _voiceWatchPathBox = new TextBox
        {
            Height = 22,
            FontSize = 11,
            Background = InputBg,
            Foreground = FgMuted,
            BorderBrush = Border,
            CaretBrush = Fg,
            VerticalContentAlignment = VerticalAlignment.Center,
            Padding = new Thickness(6, 0, 6, 0),
            IsReadOnly = true,
            Text = "No file selected",
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 6, 0)
        };
        watchRow.Children.Add(_voiceWatchPathBox);
        var watchBrowseBtn = new Button
        {
            Content = "Browse",
            Height = 24,
            Width = 70,
            FontSize = 11,
            Cursor = Cursors.Hand,
            Background = Surface,
            Foreground = Fg,
            BorderThickness = new Thickness(0)
        };
        watchRow.Children.Add(watchBrowseBtn);
        voiceClonePanel.Children.Add(watchRow);

        _voiceWatchCheck.Checked += (_, _) =>
        {
            if (_voiceWatchSuppressCheck) return;
            if (string.IsNullOrEmpty(_voiceWatchPathBox.Text) || !File.Exists(_voiceWatchPathBox.Text))
            {
                BrowseWatchFile();
                if (!File.Exists(_voiceWatchPathBox.Text))
                {
                    _voiceWatchSuppressCheck = true;
                    _voiceWatchCheck.IsChecked = false;
                    _voiceWatchSuppressCheck = false;
                    _voiceStatusLabel.Content = "";
                    return;
                }
            }
            StartWatchFile();
        };
        _voiceWatchCheck.Unchecked += (_, _) =>
        {
            if (_voiceWatchSuppressCheck) return;
            StopWatchFile();
        };

        watchBrowseBtn.Click += (_, _) =>
        {
            var wasWatching = _voiceWatchCheck.IsChecked == true;
            if (wasWatching) StopWatchFile();
            BrowseWatchFile();
            if (wasWatching && File.Exists(_voiceWatchPathBox.Text))
                StartWatchFile();
        };

        _audioVoiceClonePanel = voiceClonePanel;
        _voiceResultPath = null;

        refBrowseBtn.Click += (_, _) =>
        {
            var dlg = new OpenFileDialog { Filter = "Audio Files|*.wav;*.mp3;*.ogg;*.flac", Multiselect = false };
            if (dlg.ShowDialog() == true)
            {
                _voiceRefAudioBox.Text = dlg.FileName;
                _voiceRefAudioBox.Foreground = Fg;
                AddVoiceRefItem(dlg.FileName);
            }
        };
        _voiceCloneBtn.Click += async (_, _) =>
        {
            var refPath = _voiceRefAudioBox.Text;
            var hasRef = File.Exists(refPath);
            var text = _voiceTextInput.Text.Trim();
            if (string.IsNullOrEmpty(text))
            {
                _voiceStatusLabel.Content = "Enter text to speak";
                _voiceStatusLabel.Foreground = Error;
                return;
            }

            if (hasRef)
            {
                var ttsDir = _settings.VoiceTtsDir;
                if (!string.IsNullOrWhiteSpace(ttsDir))
                {
                    Directory.CreateDirectory(ttsDir);
                    var destFile = Path.Combine(ttsDir, Path.GetFileName(refPath));
                    if (!string.Equals(refPath, destFile, StringComparison.OrdinalIgnoreCase))
                        File.Copy(refPath, destFile, overwrite: true);
                    refPath = destFile;
                }
                if (!string.IsNullOrWhiteSpace(ttsDir) && _isKoboldRunning)
                {
                    _voiceStatusLabel.Content = "Restarting server to load voice...";
                    _voiceStatusLabel.Foreground = Fg;
                    // Stop() does a blocking HTTP abort call plus up to two 5s WaitForExit
                    // calls - up to ~13s of blocking work. Must not run on the UI thread.
                    var processToStop = _koboldProcess;
                    await Task.Run(() => { try { processToStop?.Stop(); } catch { } });
                    _koboldProcess?.Dispose();
                    _koboldClient?.Dispose();
                    _koboldClient = null;
                    _openRouterClient?.Dispose();
                    _openRouterClient = null;
                    _isKoboldStarting = false;
                    SetKoboldRunning(false);
                }
            }

            var requiredMode = KoboldMode.Audio;
            if (!await EnsureKoboldModeReadyAsync(requiredMode))
            {
                _voiceCloneBtn.IsEnabled = true;
                return;
            }
            _voiceCloneBtn.IsEnabled = false;
            _voiceStatusLabel.Content = hasRef ? "Cloning voice..." : "Generating speech...";
            _voiceStatusLabel.Foreground = Fg;
            try
            {
                var audioData = hasRef
                    ? await _koboldClient.CloneVoiceAsync(refPath, text)
                    : await _koboldClient.TextToSpeechAsync(text);
                var outDir = _settings.OutputPath;
                Directory.CreateDirectory(outDir);
                var outPath = Path.Combine(outDir, $"voice_{DateTime.Now:yyyyMMdd_HHmmss}.wav");
                await File.WriteAllBytesAsync(outPath, audioData);
                _voiceResultPath = outPath;
                _voiceStatusLabel.Content = "Done";
                _voiceStatusLabel.Foreground = Fg;
                try { Process.Start(new ProcessStartInfo(outPath) { UseShellExecute = true }); }
                catch { }
            }
            catch (Exception ex)
            {
                var detail = ex.InnerException != null ? $"{ex.Message} ({ex.InnerException.Message})" : ex.Message;
                if (_koboldProcess != null && !_koboldProcess.IsRunning)
                {
                    detail = "KoboldCpp process crashed during synthesis. " + detail;
                    SetKoboldRunning(false);
                }
                _voiceStatusLabel.Content = $"Error: {detail}";
                Log($"Voice clone error: {detail}");
                _voiceStatusLabel.Foreground = Error;
            }
            finally
            {
                _voiceCloneBtn.IsEnabled = true;
            }
        };

        _voiceRecordBtn.Click += (_, _) =>
        {
            if (_voiceIsRecording)
            {
                StopVoiceRecording();
                return;
            }
            var dir = !string.IsNullOrWhiteSpace(_settings.VoiceTtsDir)
                ? _settings.VoiceTtsDir
                : Path.Combine(Path.GetTempPath(), "MyAiGen");
            Directory.CreateDirectory(dir);
            _voiceRecorderFile = Path.Combine(dir, $"mic_ref_{DateTime.Now:yyyyMMdd_HHmmss}.wav");
            try
            {
                _voiceRecorderWriter = new WaveFileWriter(_voiceRecorderFile, new WaveFormat(16000, 16, 1));
                _voiceRecorder = new WaveInEvent
                {
                    WaveFormat = new WaveFormat(16000, 16, 1),
                    BufferMilliseconds = 100
                };
                _voiceRecorder.DataAvailable += (_, e) =>
                {
                    lock (_voiceRecorderLock)
                    {
                        try { _voiceRecorderWriter?.Write(e.Buffer, 0, e.BytesRecorded); }
                        catch { }
                    }
                };
                _voiceRecorder.RecordingStopped += (_, _) =>
                {
                    lock (_voiceRecorderLock)
                    {
                        _voiceRecorder?.Dispose();
                        _voiceRecorder = null;
                        _voiceRecorderWriter?.Dispose();
                        _voiceRecorderWriter = null;
                        _voiceIsRecording = false;
                    }
                    Dispatcher.BeginInvoke(() =>
                    {
                        if (_voiceRecorderFile != null && File.Exists(_voiceRecorderFile))
                        {
                            _voiceRefAudioBox.Text = _voiceRecorderFile;
                            _voiceRefAudioBox.Foreground = Fg;
                            AddVoiceRefItem(_voiceRecorderFile);
                            _voiceStatusLabel.Content = "Mic recorded";
                            _voiceStatusLabel.Foreground = Fg;
                        }
                        _voiceRecordBtn.Content = "Record Mic";
                        _voiceRecordBtn.Background = Surface;
                    });
                };
                _voiceRecorder.StartRecording();
                _voiceIsRecording = true;
                _voiceRecordBtn.Content = "Stop";
                _voiceRecordBtn.Background = Error;
                _voiceStatusLabel.Content = "Recording...";
                _voiceStatusLabel.Foreground = Fg;
            }
            catch
            {
                _voiceRecorder?.Dispose();
                _voiceRecorder = null;
                _voiceRecorderWriter?.Dispose();
                _voiceRecorderWriter = null;
                _voiceIsRecording = false;
                _voiceStatusLabel.Content = "Mic unavailable";
                _voiceStatusLabel.Foreground = Error;
            }
        };

        var musicPanel = new StackPanel { Margin = new Thickness(0) };
        musicPanel.Children.Add(SectionLabel("Text to Music"));

        var musicPromptBox = new TextBox
        {
            Height = 80,
            FontSize = 13,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Background = InputBg,
            Foreground = Fg,
            BorderBrush = Border,
            CaretBrush = Fg,
            Padding = new Thickness(8, 6, 8, 6),
            VerticalContentAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 0, 0, 6)
        };
        musicPanel.Children.Add(musicPromptBox);

        var musicOutRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 4) };
        var musicOutLabel = new Label { Content = "Output:", FontSize = 12, Foreground = FgMuted, Padding = new Thickness(0, 0, 4, 0), VerticalAlignment = VerticalAlignment.Center };
        var musicOutBox = new TextBox
        {
            Height = 22,
            FontSize = 11,
            Background = InputBg,
            Foreground = Fg,
            BorderBrush = Border,
            CaretBrush = Fg,
            VerticalContentAlignment = VerticalAlignment.Center,
            Padding = new Thickness(6, 0, 6, 0),
            Text = _settings.OutputPath,
            Margin = new Thickness(0, 0, 4, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        var musicOutBrowseBtn = new Button
        {
            Content = "...",
            Height = 22,
            Width = 28,
            FontSize = 11,
            Cursor = Cursors.Hand,
            Background = Surface,
            Foreground = Fg,
            BorderBrush = Border,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(2, 0, 2, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        musicOutBrowseBtn.Click += (_, _) =>
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                ValidateNames = false,
                CheckFileExists = false,
                CheckPathExists = true,
                FileName = "Select Folder",
                Title = "Select output folder"
            };
            if (dlg.ShowDialog() == true)
            {
                var folder = Path.GetDirectoryName(dlg.FileName);
                if (!string.IsNullOrEmpty(folder))
                    musicOutBox.Text = folder;
            }
        };
        musicOutRow.Children.Add(musicOutLabel);
        musicOutRow.Children.Add(musicOutBox);
        musicOutRow.Children.Add(musicOutBrowseBtn);
        musicPanel.Children.Add(musicOutRow);

        var musicActionRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 4) };
        var musicGenBtn = new Button
        {
            Content = "Generate Music",
            Height = 34,
            Width = 130,
            FontSize = 13,
            Cursor = Cursors.Hand,
            Background = Accent,
            Foreground = Brushes.White,
            BorderThickness = new Thickness(0)
        };
        var musicStatusLabel = new Label { Content = "", FontSize = 12, Foreground = FgMuted, Padding = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        musicGenBtn.Click += async (_, _) =>
        {
            var prompt = musicPromptBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(prompt)) return;
            if (_koboldClient == null) { musicStatusLabel.Content = "Starting server..."; }
            var requiredMode = KoboldMode.Audio;
            if (!await EnsureKoboldModeReadyAsync(requiredMode))
            {
                musicGenBtn.IsEnabled = true;
                return;
            }
            if (_koboldClient == null) { musicStatusLabel.Content = "Server not ready."; return; }
            musicGenBtn.IsEnabled = false;
            musicStatusLabel.Content = "Preparing...";
            musicStatusLabel.Foreground = Fg;
            try
            {
                var codes = await _koboldClient.MusicPrepareAsync(prompt);
                musicStatusLabel.Content = "Generating audio...";
                var audioData = await _koboldClient.MusicGenerateAsync(codes);
                var outDir = musicOutBox.Text.Trim();
                if (string.IsNullOrWhiteSpace(outDir)) outDir = _settings.OutputPath;
                Directory.CreateDirectory(outDir);
                var outPath = Path.Combine(outDir, $"music_{DateTime.Now:yyyyMMdd_HHmmss}.wav");
                await File.WriteAllBytesAsync(outPath, audioData);
                musicStatusLabel.Content = "Done";
                try { Process.Start(new ProcessStartInfo(outPath) { UseShellExecute = true }); }
                catch { }
            }
            catch (Exception ex)
            {
                musicStatusLabel.Content = $"Error: {ex.Message}";
                musicStatusLabel.Foreground = Error;
            }
            finally { musicGenBtn.IsEnabled = true; }
        };
        musicActionRow.Children.Add(musicGenBtn);
        musicActionRow.Children.Add(musicStatusLabel);
        musicPanel.Children.Add(musicActionRow);
        _audioMusicPanel = musicPanel;

        s.Children.Add(_audioLivePanel);
        s.Children.Add(_audioTranscribePanel);
        s.Children.Add(_audioVoiceClonePanel);
        s.Children.Add(_audioMusicPanel);

        _audioModeCombo.SelectionChanged += (_, _) =>
        {
            var idx = _audioModeCombo.SelectedIndex;
            _audioLivePanel.Visibility = idx == 0 ? Visibility.Visible : Visibility.Collapsed;
            _audioTranscribePanel.Visibility = idx == 1 ? Visibility.Visible : Visibility.Collapsed;
            _audioVoiceClonePanel.Visibility = idx == 2 ? Visibility.Visible : Visibility.Collapsed;
            _audioMusicPanel.Visibility = idx == 3 ? Visibility.Visible : Visibility.Collapsed;
            if (idx != 2) { StopVoiceRecording(); StopWatchFile(); }
        };
        _audioTranscribePanel.Visibility = Visibility.Collapsed;
        _audioVoiceClonePanel.Visibility = Visibility.Collapsed;
        _audioMusicPanel.Visibility = Visibility.Collapsed;

        _audioCaptureBtn.Click += OnToggleCapture;
        pickBtn.Click += async (_, _) =>
        {
            var dlg = new OpenFileDialog { Filter = "Audio Files|*.wav;*.mp3;*.ogg;*.flac", Multiselect = false };
            if (dlg.ShowDialog() == true)
            {
                _audioStatusLabel.Content = $"Transcribing {Path.GetFileName(dlg.FileName)}...";
                _audioStatusLabel.Foreground = Fg;
                _audioResultBox.Clear();
                var requiredMode = KoboldMode.Audio;
                if (!await EnsureKoboldModeReadyAsync(requiredMode))
                    return;
                try
                {
                    var text = await _koboldClient.TranscribeAudioAsync(dlg.FileName, _audioTranslateCheck.IsChecked == true);
                    _audioResultBox.Text = text;
                    _audioStatusLabel.Content = _audioTranslateCheck.IsChecked == true ? "Translated to English" : "Done";
                }
                catch (Exception ex)
                {
                    _audioStatusLabel.Content = "Error";
                    _audioResultBox.Text = $"Error: {ex.Message}";
                }
            }
        };

        void AddVoiceRefItem(string path)
        {
            foreach (var item in _voiceRefHistoryList.Items)
            {
                if (item is ListBoxItem existing && existing.Tag is string ep && string.Equals(ep, path, StringComparison.OrdinalIgnoreCase))
                {
                    _voiceRefHistoryList.SelectedItem = existing;
                    return;
                }
            }
            var li = new ListBoxItem
            {
                Content = Path.GetFileName(path),
                Tag = path,
                ToolTip = path,
                Foreground = Fg,
                Background = Brushes.Transparent
            };
            var ctx = new ContextMenu();
            var useItem = new MenuItem { Header = "Use" };
            useItem.Click += (_, _) =>
            {
                _voiceRefAudioBox.Text = path;
                _voiceRefAudioBox.Foreground = Fg;
                _voiceRefHistoryList.SelectedItem = li;
            };
            ctx.Items.Add(useItem);
            var showItem = new MenuItem { Header = "Show in folder" };
            showItem.Click += (_, _) =>
            {
                try { Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true }); }
                catch { }
            };
            ctx.Items.Add(showItem);
            ctx.Items.Add(new Separator());
            var renameItem = new MenuItem { Header = "Rename..." };
            renameItem.Click += (_, _) =>
            {
                var dir = Path.GetDirectoryName(path)!;
                var ext = Path.GetExtension(path);
                var newName = Microsoft.VisualBasic.Interaction.InputBox("New file name:", "Rename", Path.GetFileNameWithoutExtension(path));
                if (!string.IsNullOrWhiteSpace(newName))
                {
                    var newPath = Path.Combine(dir, newName + ext);
                    try
                    {
                        File.Move(path, newPath);
                        li.Content = Path.GetFileName(newPath);
                        li.Tag = newPath;
                        li.ToolTip = newPath;
                        if (_voiceRefAudioBox.Text == path) { _voiceRefAudioBox.Text = newPath; }
                    }
                    catch (Exception ex) { MessageBox.Show($"Rename failed: {ex.Message}"); }
                }
            };

            ctx.Items.Add(renameItem);
            var removeItem = new MenuItem { Header = "Remove from list" };
            removeItem.Click += (_, _) =>
            {
                _voiceRefHistoryList.Items.Remove(li);
                if (li.Tag is string rp && _voiceRefAudioBox.Text == rp)
                {
                    _voiceRefAudioBox.Text = "No file selected";
                    _voiceRefAudioBox.Foreground = FgMuted;
                }
            };

            ctx.Items.Add(removeItem);
            var deleteItem = new MenuItem { Header = "Delete file" };

            deleteItem.Click += (_, _) =>
            {
                try
                {
                    var dp = li.Tag as string;
                    if (dp != null) File.Delete(dp);
                    _voiceRefHistoryList.Items.Remove(li);
                    if (_voiceRefAudioBox.Text == dp)
                    {
                        _voiceRefAudioBox.Text = "No file selected";
                        _voiceRefAudioBox.Foreground = FgMuted;
                    }
                }
                catch (Exception ex) { MessageBox.Show($"Delete failed: {ex.Message}"); }
            };
            ctx.Items.Add(deleteItem);
            li.ContextMenu = ctx;
            _voiceRefHistoryList.Items.Add(li);
            _voiceRefHistoryList.SelectedItem = li;
        }

        void BrowseWatchFile()
        {
            var dlg = new OpenFileDialog { Filter = "Text Files|*.txt", Multiselect = false };
            if (dlg.ShowDialog() == true)
            {
                _voiceWatchPathBox.Text = dlg.FileName;
                _voiceWatchPathBox.Foreground = Fg;
            }
        }

        void StartWatchFile()
        {
            StopWatchFile();
            var path = _voiceWatchPathBox.Text;
            if (!File.Exists(path)) return;
            try
            {
                _voiceWatchLineCount = File.ReadLines(path).Count();
                _voiceWatchLastChange = DateTime.UtcNow;
                _voiceFileWatcher = new FileSystemWatcher
                {
                    Path = Path.GetDirectoryName(path)!,
                    Filter = Path.GetFileName(path),
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size,
                    EnableRaisingEvents = true
                };
                _voiceFileWatcher.Changed += OnWatchFileChanged;
                _voiceStatusLabel.Content = $"Watching {Path.GetFileName(path)}";
                _voiceStatusLabel.Foreground = Fg;
            }
            catch (Exception ex)
            {
                _voiceStatusLabel.Content = $"Watch error: {ex.Message}";
                _voiceStatusLabel.Foreground = Error;
            }
        }

        void StopWatchFile()
        {
            _voiceWatchDebounce?.Dispose();
            _voiceWatchDebounce = null;
            if (_voiceFileWatcher != null)
            {
                _voiceFileWatcher.EnableRaisingEvents = false;
                _voiceFileWatcher.Changed -= OnWatchFileChanged;
                _voiceFileWatcher.Dispose();
                _voiceFileWatcher = null;
            }
        }

        return s;
    }

    private StackPanel BuildVisionPanel()
    {
        var s = new StackPanel { Margin = new Thickness(12, 8, 12, 8) };

        s.Children.Add(SectionLabel("Translate To"));
        _visionTargetLang = new ComboBox
        {
            Height = 26,
            FontSize = 12,
            Template = SettingsWindow.DarkComboTemplate,
            Background = InputBg,
            Foreground = Fg,
            BorderBrush = Border,
        };
        var langItemStyle = new Style(typeof(ComboBoxItem));
        langItemStyle.Setters.Add(new Setter(Control.BackgroundProperty, Surface));
        langItemStyle.Setters.Add(new Setter(Control.ForegroundProperty, Fg));
        langItemStyle.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(6, 2, 6, 2)));
        var langHover = new Trigger { Property = ComboBoxItem.IsMouseOverProperty, Value = true };
        langHover.Setters.Add(new Setter(Control.BackgroundProperty, Highlight));
        langItemStyle.Triggers.Add(langHover);
        _visionTargetLang.ItemContainerStyle = langItemStyle;
        _visionTargetLang.ItemsSource = new[] { "Just Ask", "English", "Arabic", "Chinese (Simplified)", "Chinese (Traditional)", "Czech", "Danish", "Dutch", "Finnish", "French", "German", "Greek", "Hebrew", "Hindi", "Hungarian", "Indonesian", "Italian", "Japanese", "Korean", "Malay", "Norwegian", "Persian", "Polish", "Portuguese", "Romanian", "Russian", "Spanish", "Swahili", "Swedish", "Tagalog", "Thai", "Turkish", "Ukrainian", "Urdu", "Vietnamese" };
        _visionTargetLang.SelectedIndex = 0;
        s.Children.Add(Card(_visionTargetLang));

        var liveRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };

        var liveLabel = new TextBlock
        {
            Text = "LIVE/Realtime Translation: ",
            Foreground = FgDim,
            FontSize = 14,
            FontWeight = FontWeight.FromOpenTypeWeight(600),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 6, 0)
        };

        liveRow.Children.Add(liveLabel);
        var addBtn = new Button
        {
            Content = "+",
            Width = 28,
            Height = 26,
            FontSize = 14,
            Cursor = Cursors.Hand,
            Background = new SolidColorBrush(Color.FromRgb(80, 60, 160)),
            Foreground = Brushes.White,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0)
        };
        addBtn.Click += OnLiveTranslationAdd;
        liveRow.Children.Add(addBtn);
        var removeBtn = new Button
        {
            Content = "\u2212",
            Width = 28,
            Height = 26,
            FontSize = 14,
            Cursor = Cursors.Hand,
            Background = Surface,
            Foreground = Fg,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0),
            Margin = new Thickness(4, 0, 0, 0)
        };
        removeBtn.Click += OnLiveTranslationRemove;
        liveRow.Children.Add(removeBtn);
        s.Children.Add(liveRow);

        _liveOverlayList = new ListBox
        {
            Height = 100,
            Background = InputBg,
            Foreground = Fg,
            BorderBrush = Border,
            FontSize = 12,
            Margin = new Thickness(0, 4, 0, 0)
        };
        s.Children.Add(_liveOverlayList);

        var overlayDiv = new Border
        {
            BorderBrush = BorderTertiary,
            BorderThickness = new Thickness(0, 1, 0, 0),
            Margin = new Thickness(0, 6, 0, 0),
            Padding = new Thickness(0, 6, 0, 0),
            Child = new StackPanel()
        };
        var overlayOpts = (StackPanel)overlayDiv.Child;

        var textColorRow = new Grid { Margin = new Thickness(0, 0, 0, 8) };
        textColorRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(70) });
        textColorRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        textColorRow.Children.Add(new TextBlock { Text = "Text Color:", Foreground = Fg, FontSize = 11, VerticalAlignment = VerticalAlignment.Center });
        _visionTextColorCombo = new ComboBox
        {
            Height = 24,
            FontSize = 11,
            Template = SettingsWindow.DarkComboTemplate,
            Background = InputBg,
            Foreground = Fg,
            BorderBrush = Border,
            Margin = new Thickness(6, 0, 0, 0),
            Padding = new Thickness(6, 0, 6, 0)
        };
        var tcItemStyle = new Style(typeof(ComboBoxItem));
        tcItemStyle.Setters.Add(new Setter(Control.BackgroundProperty, Surface));
        tcItemStyle.Setters.Add(new Setter(Control.ForegroundProperty, Fg));
        tcItemStyle.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(4, 2, 4, 2)));
        var tcHover = new Trigger { Property = ComboBoxItem.IsMouseOverProperty, Value = true };
        tcHover.Setters.Add(new Setter(Control.BackgroundProperty, Highlight));
        tcItemStyle.Triggers.Add(tcHover);
        _visionTextColorCombo.ItemContainerStyle = tcItemStyle;
        (string Name, Color Color)[] textPresets = new[] { ("White", Color.FromArgb(240, 245, 245, 245)), ("Cyan", Color.FromArgb(240, 0, 220, 255)), ("Lime", Color.FromArgb(240, 100, 255, 80)), ("Gold", Color.FromArgb(240, 255, 200, 50)), ("Coral", Color.FromArgb(240, 255, 110, 90)), ("Magenta", Color.FromArgb(240, 255, 60, 255)), ("Sky Blue", Color.FromArgb(240, 90, 170, 255)), ("Orange", Color.FromArgb(240, 255, 150, 30)) };
        foreach (var (name, c) in textPresets)
        {
            var brush = AppTheme.F(new SolidColorBrush(c));
            var item = new StackPanel { Orientation = Orientation.Horizontal };
            item.Children.Add(new Rectangle { Width = 12, Height = 12, Fill = brush, RadiusX = 2, RadiusY = 2, Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center });
            item.Children.Add(new TextBlock { Text = name, Foreground = Fg, FontSize = 11, VerticalAlignment = VerticalAlignment.Center });
            _visionTextColorCombo.Items.Add(item);
        }
        _visionTextColorCombo.SelectedIndex = 0;
        _visionTextColorCombo.SelectionChanged += (_, _) =>
        {
            if (_visionSyncingOverlay) return;
            var idx = _visionTextColorCombo.SelectedIndex;
            if (idx >= 0 && idx < textPresets.Length)
            {
                var c = textPresets[idx].Color;
                foreach (var o in _screenOcrOverlays) o.SetTextColor(c);
            }
        };
        Grid.SetColumn(_visionTextColorCombo, 1);
        textColorRow.Children.Add(_visionTextColorCombo);
        overlayOpts.Children.Add(textColorRow);

        var bgColorRow = new Grid { Margin = new Thickness(0, 0, 0, 8) };
        bgColorRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(70) });
        bgColorRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        bgColorRow.Children.Add(new TextBlock { Text = "Bg Color:", Foreground = Fg, FontSize = 11, VerticalAlignment = VerticalAlignment.Center });
        _visionBgColorCombo = new ComboBox
        {
            Height = 24,
            FontSize = 11,
            Template = SettingsWindow.DarkComboTemplate,
            Background = InputBg,
            Foreground = Fg,
            BorderBrush = Border,
            Margin = new Thickness(6, 0, 0, 0),
            Padding = new Thickness(6, 0, 6, 0)
        };
        var bgItemStyle = new Style(typeof(ComboBoxItem));
        bgItemStyle.Setters.Add(new Setter(Control.BackgroundProperty, Surface));
        bgItemStyle.Setters.Add(new Setter(Control.ForegroundProperty, Fg));
        bgItemStyle.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(4, 2, 4, 2)));
        var bgHover = new Trigger { Property = ComboBoxItem.IsMouseOverProperty, Value = true };
        bgHover.Setters.Add(new Setter(Control.BackgroundProperty, Highlight));
        bgItemStyle.Triggers.Add(bgHover);
        _visionBgColorCombo.ItemContainerStyle = bgItemStyle;
        (string Name, Color Color)[] bgPresets = new[] { ("Dark", Color.FromArgb(255, 20, 20, 30)), ("Black", Color.FromArgb(255, 10, 10, 10)), ("Navy", Color.FromArgb(255, 15, 15, 50)), ("Maroon", Color.FromArgb(255, 45, 15, 15)), ("Forest", Color.FromArgb(255, 10, 35, 15)), ("Slate", Color.FromArgb(255, 25, 30, 40)) };
        foreach (var (name, c) in bgPresets)
        {
            var brush = AppTheme.F(new SolidColorBrush(c));
            var item = new StackPanel { Orientation = Orientation.Horizontal };
            item.Children.Add(new Rectangle { Width = 12, Height = 12, Fill = brush, RadiusX = 2, RadiusY = 2, Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center });
            item.Children.Add(new TextBlock { Text = name, Foreground = Fg, FontSize = 11, VerticalAlignment = VerticalAlignment.Center });
            _visionBgColorCombo.Items.Add(item);
        }
        _visionBgColorCombo.SelectedIndex = 0;
        _visionBgColorCombo.SelectionChanged += (_, _) =>
        {
            if (_visionSyncingOverlay) return;
            var idx = _visionBgColorCombo.SelectedIndex;
            if (idx >= 0 && idx < bgPresets.Length)
            {
                var c = bgPresets[idx].Color;
                foreach (var o in _screenOcrOverlays) o.SetBgColor(c);
            }
        };
        Grid.SetColumn(_visionBgColorCombo, 1);
        bgColorRow.Children.Add(_visionBgColorCombo);
        overlayOpts.Children.Add(bgColorRow);

        var fontTypeRow = new Grid { Margin = new Thickness(0, 0, 0, 8) };
        fontTypeRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(70) });
        fontTypeRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        fontTypeRow.Children.Add(new TextBlock { Text = "Font:", Foreground = Fg, FontSize = 11, VerticalAlignment = VerticalAlignment.Center });
        _visionFontCombo = new ComboBox
        {
            Height = 24,
            FontSize = 11,
            Template = SettingsWindow.DarkComboTemplate,
            Background = InputBg,
            Foreground = Fg,
            BorderBrush = Border,
            Margin = new Thickness(6, 0, 0, 0),
            Padding = new Thickness(6, 0, 6, 0)
        };
        var fontItemStyle = new Style(typeof(ComboBoxItem));
        fontItemStyle.Setters.Add(new Setter(Control.BackgroundProperty, Surface));
        fontItemStyle.Setters.Add(new Setter(Control.ForegroundProperty, Fg));
        fontItemStyle.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(6, 2, 6, 2)));
        var fontHover = new Trigger { Property = ComboBoxItem.IsMouseOverProperty, Value = true };
        fontHover.Setters.Add(new Setter(Control.BackgroundProperty, Highlight));
        fontItemStyle.Triggers.Add(fontHover);
        _visionFontCombo.ItemContainerStyle = fontItemStyle;
        _visionFontCombo.Items.Add("Consolas");
        _visionFontCombo.Items.Add("Segoe UI");
        _visionFontCombo.Items.Add("Arial");
        _visionFontCombo.Items.Add("Calibri");
        _visionFontCombo.Items.Add("Georgia");
        _visionFontCombo.Items.Add("Courier New");
        _visionFontCombo.Items.Add("Tahoma");
        _visionFontCombo.Items.Add("Verdana");
        _visionFontCombo.SelectedIndex = 0;
        _visionFontCombo.SelectionChanged += (_, _) =>
        {
            if (_visionSyncingOverlay) return;
            if (_visionFontCombo.SelectedItem is string name)
            {
                foreach (var o in _screenOcrOverlays) o.SetFontFamily(name);
            }
        };
        Grid.SetColumn(_visionFontCombo, 1);
        fontTypeRow.Children.Add(_visionFontCombo);
        overlayOpts.Children.Add(fontTypeRow);

        var fontRow = new Grid { Margin = new Thickness(0, 0, 0, 8) };
        fontRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(70) });
        fontRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        fontRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        fontRow.Children.Add(new TextBlock { Text = "Font Size:", Foreground = Fg, FontSize = 11, VerticalAlignment = VerticalAlignment.Center });
        _visionFontSlider = new Slider { Minimum = 8, Maximum = 72, Value = 14, Height = 20, Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, Style = SliderStyle, Foreground = Accent, Background = Border, BorderBrush = Brushes.Transparent };
        _visionFontLabel = new Label { Content = "14", Foreground = Fg, FontSize = 11, Padding = new Thickness(4, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        _visionFontSlider.ValueChanged += (_, _) =>
        {
            if (_visionSyncingOverlay) return;
            var v = (int)_visionFontSlider.Value;
            _visionFontLabel.Content = v.ToString();
            foreach (var o in _screenOcrOverlays) o.SetFontSize(v);
        };
        Grid.SetColumn(_visionFontSlider, 1);
        fontRow.Children.Add(_visionFontSlider);
        Grid.SetColumn(_visionFontLabel, 2);
        fontRow.Children.Add(_visionFontLabel);
        overlayOpts.Children.Add(fontRow);

        var opRow = new Grid { Margin = new Thickness(0, 0, 0, 8) };
        opRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(70) });
        opRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        opRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        opRow.Children.Add(new TextBlock { Text = "Opacity:", Foreground = Fg, FontSize = 11, VerticalAlignment = VerticalAlignment.Center });
        _visionOpacitySlider = new Slider { Minimum = 30, Maximum = 240, Value = 200, Height = 20, Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, Style = SliderStyle, Foreground = Accent, Background = Border, BorderBrush = Brushes.Transparent };
        _visionOpacityLabel = new Label { Content = "80%", Foreground = Fg, FontSize = 11, Padding = new Thickness(4, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        _visionOpacitySlider.ValueChanged += (_, _) =>
        {
            if (_visionSyncingOverlay) return;
            var v = (int)_visionOpacitySlider.Value;
            _visionOpacityLabel.Content = $"{(int)(v / 240.0 * 100)}%";
            foreach (var o in _screenOcrOverlays) o.SetBgOpacity(v);
        };
        Grid.SetColumn(_visionOpacitySlider, 1);
        opRow.Children.Add(_visionOpacitySlider);
        Grid.SetColumn(_visionOpacityLabel, 2);
        opRow.Children.Add(_visionOpacityLabel);
        overlayOpts.Children.Add(opRow);

        s.Children.Add(overlayDiv);

        return s;
    }

    private Grid BuildRightPanel()
    {
        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star), MinHeight = 80 });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(150), MinHeight = 40 });

        // Text conversation (list + files panel)
        _chatControl = new ChatConversationControl(Bg, Fg);
        _chatControl.MessageList.ItemTemplate = CreateUnifiedChatTemplate();
        _chatControl.MessageList.ItemsSource = _chatHistory;
        _chatControl.Panel.Visibility = Visibility.Collapsed;
        Grid.SetRow(_chatControl.Panel, 0);
        grid.Children.Add(_chatControl.Panel);

        // Audio conversation (list only — runs directly on the grid)
        _audioHistoryList = new ListBox
        {
            Background = Bg,
            Foreground = Fg,
            BorderThickness = new Thickness(0),
            Margin = new Thickness(8, 8, 8, 4),
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Visibility = Visibility.Collapsed,
            ItemTemplate = CreateUnifiedChatTemplate(),
            ItemsSource = _audioHistory
        };
        VirtualizingStackPanel.SetIsVirtualizing(_audioHistoryList, false);
        ScrollViewer.SetHorizontalScrollBarVisibility(_audioHistoryList, System.Windows.Controls.ScrollBarVisibility.Disabled);
        ScrollViewer.SetCanContentScroll(_audioHistoryList, false);
        _audioHistoryList.Loaded += (_, _) => _audioScrollViewer ??= FindScrollViewer(_audioHistoryList);
        Grid.SetRow(_audioHistoryList, 0);
        grid.Children.Add(_audioHistoryList);

        // Vision conversation (list + files panel + input area)
        _visionChatPanel = new Grid { Visibility = Visibility.Collapsed };
        _visionChatPanel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        _visionChatPanel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        _visionChatControl = new ChatConversationControl(Bg, Fg);
        _visionChatControl.MessageList.ItemTemplate = CreateUnifiedChatTemplate();
        _visionChatControl.MessageList.ItemsSource = _visionChatHistory;
        _visionChatPanel.Children.Add(_visionChatControl.Panel);

        var visionInputRow = new StackPanel { Margin = new Thickness(8, 2, 8, 8) };
        _visionChatInput = new TextBox
        {
            Height = 100,
            FontSize = 13,
            Background = InputBg,
            Foreground = Fg,
            BorderBrush = Border,
            CaretBrush = Fg,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Padding = new Thickness(8, 6, 8, 6),
            VerticalContentAlignment = VerticalAlignment.Top
        };
        _visionChatInput.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && !Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
            { e.Handled = true; OnVisionChatSend(null, null); }
        };
        visionInputRow.Children.Add(_visionChatInput);
        var visionSendRow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 4, 0, 0) };
        var visionBrowseBtn = new Button
        {
            Content = "+",
            Width = 32,
            Height = 32,
            FontSize = 16,
            FontWeight = FontWeight.FromOpenTypeWeight(600),
            Cursor = Cursors.Hand,
            Background = new SolidColorBrush(Color.FromRgb(60, 130, 60)),
            Foreground = Brushes.White,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0),
            Margin = new Thickness(0, 0, 4, 0)
        };
        visionBrowseBtn.Click += (_, _) =>
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Filter = "Images (*.png;*.jpg;*.jpeg;*.webp;*.gif;*.bmp)|*.png;*.jpg;*.jpeg;*.webp;*.gif;*.bmp",
                Title = "Select an image to analyze"
            };
            if (dlg.ShowDialog(this) == true)
                _visionImagePath = dlg.FileName;
        };
        var visionSendBtn = new Button
        {
            Content = "Ask",
            Width = 60,
            Height = 32,
            FontSize = 12,
            Cursor = Cursors.Hand,
            Background = BrVisionUnlocked,
            Foreground = Brushes.White,
            BorderThickness = new Thickness(0)
        };
        visionSendBtn.Click += OnVisionChatSend;
        visionSendRow.Children.Add(visionSendBtn);
        visionSendRow.Children.Add(visionBrowseBtn);
        visionInputRow.Children.Add(visionSendRow);
        _visionChatPanel.Children.Add(visionInputRow);
        Grid.SetRow(visionInputRow, 1);
        Grid.SetRow(_visionChatPanel, 0);
        grid.Children.Add(_visionChatPanel);

        _rightImageBorder = new Border
        {
            Background = CardBg,
            BorderBrush = Border,
            BorderThickness = new Thickness(1),
            Margin = new Thickness(8, 8, 8, 4),
            Visibility = Visibility.Collapsed
        };

        _resultImage = new Image
        {
            Stretch = Stretch.Uniform,
            MaxWidth = 2048,
            MaxHeight = 2048,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };

        _zoomTransform = new ScaleTransform(1.0, 1.0);
        var imageContainer = new Grid();
        imageContainer.LayoutTransform = _zoomTransform;
        imageContainer.Children.Add(_resultImage);

        _imageScrollViewer = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = imageContainer
        };

        imageContainer.MouseLeftButtonDown += (_, e) =>
        {
            if (_modeCombo.SelectedIndex != 0) return;
            _isDragging = true;
            _dragStart = e.GetPosition(_imageScrollViewer);
            _dragHOffset = _imageScrollViewer.HorizontalOffset;
            _dragVOffset = _imageScrollViewer.VerticalOffset;
            imageContainer.CaptureMouse();
            _imageScrollViewer.Cursor = Cursors.ScrollAll;
            e.Handled = true;
        };
        imageContainer.MouseMove += (_, e) =>
        {
            if (!_isDragging) return;
            var pt = e.GetPosition(_imageScrollViewer);
            double dx = _dragStart.X - pt.X;
            double dy = _dragStart.Y - pt.Y;
            double newH = Math.Max(0, Math.Min(_imageScrollViewer.ScrollableWidth, _dragHOffset + dx));
            double newV = Math.Max(0, Math.Min(_imageScrollViewer.ScrollableHeight, _dragVOffset + dy));
            _imageScrollViewer.ScrollToHorizontalOffset(newH);
            _imageScrollViewer.ScrollToVerticalOffset(newV);
        };
        imageContainer.MouseLeftButtonUp += (_, _) =>
        {
            if (!_isDragging) return;
            _isDragging = false;
            imageContainer.ReleaseMouseCapture();
            _imageScrollViewer.Cursor = Cursors.Arrow;
        };

        _placeholder = new TextBlock
        {
            Text = "Ready",
            Foreground = FgMuted,
            FontSize = 16,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false
        };

        var tb = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 8, 8, 0)
        };

        _zoomLabel = new Label
        {
            Content = "100%",
            Foreground = FgDim,
            FontSize = 11,
            VerticalContentAlignment = VerticalAlignment.Center,
            Padding = new Thickness(4, 0, 4, 0),
            Width = 50,
            HorizontalContentAlignment = HorizontalAlignment.Right
        };

        var zoomInBtn = MakeBtn("+", 28, 26, 13, (_, _) => ChangeZoom(1.25), Surface, Fg);
        zoomInBtn.BorderBrush = Border; zoomInBtn.BorderThickness = new Thickness(1);
        zoomInBtn.Margin = new Thickness(2, 0, 0, 0);

        var zoomOutBtn = MakeBtn("\u2212", 28, 26, 13, (_, _) => ChangeZoom(0.8), Surface, Fg);
        zoomOutBtn.BorderBrush = Border; zoomOutBtn.BorderThickness = new Thickness(1);
        zoomOutBtn.Margin = new Thickness(2, 0, 0, 0);

        var zoomResetBtn = MakeBtn("1:1", 36, 26, 11, (_, _) => SetZoom(1.0), Surface, Fg);
        zoomResetBtn.BorderBrush = Border; zoomResetBtn.BorderThickness = new Thickness(1);
        zoomResetBtn.Margin = new Thickness(2, 0, 0, 0);

        var saveBtn = new Button
        {
            Content = "Save",
            Width = 50,
            Height = 26,
            FontSize = 11,
            Cursor = Cursors.Hand,
            Background = Surface,
            Foreground = Fg,
            BorderBrush = Border,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(0)
        };
        saveBtn.Click += OnSaveImageClick;

        _videoSaveBtn = new Button
        {
            Content = "Save Vid",
            Width = 60,
            Height = 26,
            FontSize = 11,
            Cursor = Cursors.Hand,
            Background = Surface,
            Foreground = Fg,
            BorderBrush = Border,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(0),
            Visibility = Visibility.Collapsed
        };
        _videoSaveBtn.Click += OnSaveVideoClick;

        _modeCombo = new ComboBox
        {
            Width = 90,
            Height = 24,
            FontSize = 11,
            Margin = new Thickness(0, 0, 6, 0),
            Background = InputBg,
            Foreground = Fg,
            BorderBrush = Border,
            BorderThickness = new Thickness(1),
            SelectedIndex = 1,
            Cursor = Cursors.Hand,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            VerticalContentAlignment = VerticalAlignment.Center,
            Padding = new Thickness(6, 0, 0, 0)
        };
        try
        {
            _modeCombo.Template = (System.Windows.Controls.ControlTemplate)
                System.Windows.Markup.XamlReader.Parse(@"
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
        }
        catch { }
        _modeCombo.Items.Add("Free Mode");
        _modeCombo.Items.Add("Fixed");

        tb.Children.Add(_modeCombo);
        tb.Children.Add(saveBtn);
        tb.Children.Add(_videoSaveBtn);
        tb.Children.Add(zoomOutBtn);
        tb.Children.Add(zoomInBtn);
        tb.Children.Add(zoomResetBtn);
        tb.Children.Add(_zoomLabel);

        _videoPlayer = new MediaElement
        {
            Stretch = Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Visibility = Visibility.Collapsed,
            LoadedBehavior = MediaState.Manual,
            UnloadedBehavior = MediaState.Manual
        };
        _videoPlayer.MediaEnded += (_, _) =>
        {
            _videoPlayBtn.Content = "\u25B6";
            _videoPlayer.Position = TimeSpan.Zero;
        };
        _videoPlayer.MediaOpened += (_, _) =>
        {
            _videoSeekSlider.Maximum = _videoPlayer.NaturalDuration.TimeSpan.TotalSeconds;
            _videoSeekSlider.Value = 0;
            UpdateVideoTimeLabel();
            _videoPlayBtn.Content = "\u23F8";
            _videoPlayer.Play();
        };

        _videoTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(250)
        };
        _videoTimer.Tick += (_, _) =>
        {
            if (_videoPlayer.Source != null && _videoPlayer.NaturalDuration.HasTimeSpan)
            {
                _videoSeekSlider.Value = _videoPlayer.Position.TotalSeconds;
                UpdateVideoTimeLabel();
            }
        };

        _videoPlayBtn = new Button
        {
            Content = "\u25B6",
            Width = 32,
            Height = 26,
            FontSize = 12,
            Cursor = Cursors.Hand,
            Background = Surface,
            Foreground = Fg,
            BorderBrush = Border,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(0)
        };
        _videoPlayBtn.Click += (_, _) =>
        {
            if (_videoPlayer.Source == null) return;
            if (_videoPlayer.Position.TotalSeconds >= _videoSeekSlider.Maximum - 0.5)
            {
                _videoPlayer.Position = TimeSpan.Zero;
            }
            if (_videoPlayBtn.Content.ToString() == "\u23F8")
            {
                _videoPlayer.Pause();
                _videoPlayBtn.Content = "\u25B6";
                _videoTimer.Stop();
            }
            else
            {
                _videoPlayer.Play();
                _videoPlayBtn.Content = "\u23F8";
                _videoTimer.Start();
            }
        };

        _videoSeekSlider = new Slider
        {
            Minimum = 0,
            Maximum = 1,
            Value = 0,
            Height = 20,
            TickFrequency = 0.1,
            IsSnapToTickEnabled = false,
            Foreground = Accent,
            Background = Border,
            BorderBrush = Brushes.Transparent,
            Style = SliderStyle,
            Margin = new Thickness(4, 0, 4, 0),
            IsEnabled = false
        };
        _videoSeekSlider.ValueChanged += (_, e) =>
        {
            if (_videoPlayer.Source != null && _videoPlayer.NaturalDuration.HasTimeSpan &&
                _videoPlayer.NaturalDuration.TimeSpan.TotalSeconds > 0 &&
                Math.Abs(e.NewValue - _videoPlayer.Position.TotalSeconds) > 0.5)
            {
                _videoPlayer.Position = TimeSpan.FromSeconds(e.NewValue);
            }
        };

        _videoTimeLabel = new Label
        {
            Content = "00:00 / 00:00",
            Foreground = FgDim,
            FontSize = 11,
            Padding = new Thickness(4, 0, 4, 0),
            VerticalContentAlignment = VerticalAlignment.Center,
            MinWidth = 90,
            HorizontalContentAlignment = HorizontalAlignment.Center
        };

        var volIcon = new TextBlock { Text = "\uD83D\uDD0A", FontSize = 11, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 2, 0) };
        _videoVolumeSlider = new Slider
        {
            Minimum = 0,
            Maximum = 1,
            Value = 0.8,
            Width = 60,
            Height = 20,
            TickFrequency = 0.05,
            IsSnapToTickEnabled = true,
            Foreground = Accent,
            Background = Border,
            BorderBrush = Brushes.Transparent,
            Style = SliderStyle,
            VerticalAlignment = VerticalAlignment.Center
        };
        _videoVolumeSlider.ValueChanged += (_, e) =>
        {
            if (_videoPlayer.Source != null)
                _videoPlayer.Volume = e.NewValue;
        };

        var transportBar = new Border
        {
            Background = InputBgAlt,
            BorderBrush = Border,
            BorderThickness = new Thickness(0, 1, 0, 0),
            Height = 32,
            Visibility = Visibility.Collapsed
        };
        var transportGrid = new Grid { Margin = new Thickness(4, 0, 4, 0) };
        transportGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        transportGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        transportGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        transportGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        transportGrid.Children.Add(_videoPlayBtn);
        Grid.SetColumn(_videoSeekSlider, 1);
        transportGrid.Children.Add(_videoSeekSlider);
        Grid.SetColumn(_videoTimeLabel, 2);
        transportGrid.Children.Add(_videoTimeLabel);
        Grid.SetColumn(volIcon, 3);
        transportGrid.Children.Add(volIcon);
        Grid.SetColumn(_videoVolumeSlider, 4);
        transportGrid.Children.Add(_videoVolumeSlider);
        transportBar.Child = transportGrid;

        var videoContainer = new Grid();
        videoContainer.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        videoContainer.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        videoContainer.Children.Add(_videoPlayer);
        Grid.SetRow(transportBar, 1);
        videoContainer.Children.Add(transportBar);

        var imgPanel = new Grid();
        _thumbColumn = new ColumnDefinition { Width = new GridLength(136) };
        imgPanel.ColumnDefinitions.Add(_thumbColumn);
        imgPanel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        _thumbnailBox = new ListBox
        {
            Background = InputBg,
            BorderThickness = new Thickness(1),
            BorderBrush = BorderDim,
            Padding = new Thickness(0),
            Focusable = true,
        };
        var spFactory = new FrameworkElementFactory(typeof(StackPanel));
        spFactory.SetValue(StackPanel.OrientationProperty, Orientation.Vertical);
        _thumbnailBox.ItemsPanel = new ItemsPanelTemplate { VisualTree = spFactory };
        ScrollViewer.SetVerticalScrollBarVisibility(_thumbnailBox, ScrollBarVisibility.Auto);
        ScrollViewer.SetCanContentScroll(_thumbnailBox, false);
        _thumbnailBox.Loaded += (_, _) =>
        {
            var sv = FindScrollViewer(_thumbnailBox);
            if (sv != null)
                sv.ScrollChanged += OnThumbScrollChanged;
        };
        _thumbnailBox.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Delete && _thumbnailBox.SelectedItem is ListBoxItem li)
                DeleteThumbnail(li);
        };
        Grid.SetColumn(_thumbnailBox, 0);
        imgPanel.Children.Add(_thumbnailBox);
        if (_thumbPreviewCombo != null)
            _thumbnailBox.Visibility = _thumbPreviewCombo.SelectedIndex == 1 ? Visibility.Visible : Visibility.Collapsed;

        var rightContent = new Grid();
        rightContent.Children.Add(_imageScrollViewer);
        rightContent.Children.Add(videoContainer);
        rightContent.Children.Add(_placeholder);
        rightContent.Children.Add(tb);
        Grid.SetColumn(rightContent, 1);
        imgPanel.Children.Add(rightContent);

        _imageScrollViewer.PreviewMouseWheel += (_, e) =>
        {
            if (_resultImage.Source == null) return;
            double factor = e.Delta > 0 ? 1.1 : 1.0 / 1.1;
            SetZoom(Math.Round(_zoomLevel * factor * 10) / 10.0);
            e.Handled = true;
        };

        _rightImageBorder.Child = imgPanel;
        grid.Children.Add(_rightImageBorder);

        var logSplitter = new GridSplitter
        {
            Height = 5,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Center,
            Background = Brushes.Transparent,
            ResizeBehavior = GridResizeBehavior.PreviousAndNext,
            ResizeDirection = GridResizeDirection.Rows,
            Cursor = Cursors.SizeNS
        };
        grid.Children.Add(logSplitter);
        Grid.SetRow(logSplitter, 1);

        _logBox = new TextBox
        {
            Background = new SolidColorBrush(Color.FromRgb(14, 14, 18)),
            Foreground = new SolidColorBrush(Color.FromRgb(170, 200, 170)),
            BorderBrush = Border,
            FontFamily = new FontFamily("Cascadia Code, JetBrains Mono, Consolas"),
            FontSize = 11,
            IsReadOnly = true,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Padding = new Thickness(8, 6, 8, 6),
            Margin = new Thickness(8, 4, 8, 8),
            TextWrapping = TextWrapping.Wrap
        };
        grid.Children.Add(_logBox);
        Grid.SetRow(_logBox, 2);
        Grid.SetRow(_rightImageBorder, 0);

        var initIdx = _tabControl?.SelectedIndex ?? 3;
        _rightImageBorder.Visibility = initIdx == 0 ? Visibility.Visible : Visibility.Collapsed;
        _chatControl.Panel.Visibility = initIdx == 3 ? Visibility.Visible : Visibility.Collapsed;
        _visionChatPanel.Visibility = initIdx == 2 ? Visibility.Visible : Visibility.Collapsed;
        _audioHistoryList.Visibility = initIdx == 4 ? Visibility.Visible : Visibility.Collapsed;
        _placeholder.Visibility = Visibility.Collapsed;

        return grid;
    }

    private void ChangeZoom(double factor)
    {
        SetZoom(Math.Round(_zoomLevel * factor * 10) / 10.0);
    }

    private void SetZoom(double level)
    {
        _zoomLevel = Math.Max(0.1, Math.Min(5.0, level));
        _zoomTransform.ScaleX = _zoomTransform.ScaleY = _zoomLevel;
        _zoomLabel.Content = $"{_zoomLevel * 100:F0}%";
    }

    private DataTemplate CreateUnifiedChatTemplate()
    {
        var template = new DataTemplate(typeof(ChatMessage));

        var border = new FrameworkElementFactory(typeof(System.Windows.Controls.Border));
        border.SetValue(FrameworkElement.MarginProperty, new Thickness(4, 2, 4, 2));
        border.SetValue(System.Windows.Controls.Border.PaddingProperty, new Thickness(12, 8, 12, 10));
        border.SetValue(System.Windows.Controls.Border.CornerRadiusProperty, new CornerRadius(0));

        border.SetValue(Control.BackgroundProperty, new System.Windows.Data.Binding("Role") { Converter = new RoleToBubbleBrushConverter() });

        var outerStack = new FrameworkElementFactory(typeof(StackPanel));

        // ── Image thumbnail ──
        var imageElement = new FrameworkElementFactory(typeof(Image));
        imageElement.SetValue(Image.SourceProperty, new System.Windows.Data.Binding("ImagePath") { Converter = new PathToBitmapImageConverter() });
        imageElement.SetValue(Image.StretchProperty, Stretch.Uniform);
        imageElement.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 0, 0, 4));
        imageElement.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Left);
        imageElement.SetValue(FrameworkElement.CursorProperty, Cursors.Hand);
        var imageStyle = new Style(typeof(Image));
        imageStyle.Setters.Add(new Setter(FrameworkElement.WidthProperty, 80.0));
        imageStyle.Setters.Add(new Setter(FrameworkElement.HeightProperty, 80.0));
        var assistImgTrigger = new DataTrigger { Binding = new System.Windows.Data.Binding("Role"), Value = "assistant" };
        assistImgTrigger.Setters.Add(new Setter(FrameworkElement.WidthProperty, Double.NaN));
        assistImgTrigger.Setters.Add(new Setter(FrameworkElement.MaxWidthProperty, 360.0));
        assistImgTrigger.Setters.Add(new Setter(FrameworkElement.HeightProperty, Double.NaN));
        assistImgTrigger.Setters.Add(new Setter(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center));
        imageStyle.Triggers.Add(assistImgTrigger);
        imageElement.SetValue(FrameworkElement.StyleProperty, imageStyle);
        imageElement.SetValue(UIElement.VisibilityProperty, new System.Windows.Data.Binding("ImagePath") { Converter = new NullToVisibilityConverter() });
        imageElement.AddHandler(Image.MouseLeftButtonDownEvent, new MouseButtonEventHandler((s, _) =>
        {
            if (s is Image img && img.DataContext is ChatMessage msg && !string.IsNullOrEmpty(msg.ImagePath) && File.Exists(msg.ImagePath))
            {
                var win = System.Windows.Window.GetWindow(img);
                if (win == null) return;
                var popup = new Window
                {
                    WindowStyle = WindowStyle.None,
                    AllowsTransparency = true,
                    Background = Brushes.Transparent,
                    WindowStartupLocation = WindowStartupLocation.CenterOwner,
                    Owner = win,
                    ShowInTaskbar = false,
                    ResizeMode = ResizeMode.NoResize,
                    SizeToContent = SizeToContent.WidthAndHeight,
                };
                var grid = new Grid { Background = new SolidColorBrush(Color.FromArgb(200, 0, 0, 0)) };
                var fullImg = new Image
                {
                    Source = img.Source,
                    Stretch = Stretch.Uniform,
                    MaxWidth = SystemParameters.WorkArea.Width * 0.85,
                    MaxHeight = SystemParameters.WorkArea.Height * 0.85,
                    Margin = new Thickness(40),
                    Cursor = Cursors.Hand
                };
                fullImg.MouseLeftButtonDown += (_, _) => { popup.Close(); };
                grid.Children.Add(fullImg);
                popup.Content = grid;
                popup.ShowDialog();
            }
        }));
        outerStack.AppendChild(imageElement);

        // ── Attachment chips (all files: images get icon, code/text get icon) ──
        var attItemTemplate = new DataTemplate();
        var attBorder = new FrameworkElementFactory(typeof(Border));
        attBorder.SetValue(Control.BackgroundProperty, new SolidColorBrush(Color.FromRgb(0x30, 0x30, 0x48)));
        attBorder.SetValue(System.Windows.Controls.Border.CornerRadiusProperty, new CornerRadius(0));
        attBorder.SetValue(Control.PaddingProperty, new Thickness(5, 2, 5, 2));
        attBorder.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 0, 6, 4));
        attBorder.SetValue(FrameworkElement.CursorProperty, Cursors.Hand);
        attBorder.SetValue(FrameworkElement.ToolTipProperty, "Click to save");
        attBorder.AddHandler(UIElement.MouseLeftButtonUpEvent, new MouseButtonEventHandler(OnAttachmentChipClick));
        var attStack = new FrameworkElementFactory(typeof(StackPanel));
        attStack.SetValue(StackPanel.OrientationProperty, Orientation.Horizontal);
        var attIcon = new FrameworkElementFactory(typeof(TextBlock));
        attIcon.SetValue(TextBlock.TextProperty, new System.Windows.Data.Binding("Icon"));
        attIcon.SetValue(TextBlock.FontSizeProperty, 16.0);
        attIcon.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        attIcon.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 0, 4, 0));
        attStack.AppendChild(attIcon);
        var attName = new FrameworkElementFactory(typeof(TextBlock));
        attName.SetValue(TextBlock.TextProperty, new System.Windows.Data.Binding("FileName"));
        attName.SetValue(TextBlock.FontSizeProperty, 11.0);
        attName.SetValue(Control.ForegroundProperty, new SolidColorBrush(Color.FromRgb(180, 180, 190)));
        attName.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        attStack.AppendChild(attName);
        var attSaveHint = new FrameworkElementFactory(typeof(TextBlock));
        attSaveHint.SetValue(TextBlock.TextProperty, "\u2B07");
        attSaveHint.SetValue(TextBlock.FontSizeProperty, 11.0);
        attSaveHint.SetValue(Control.ForegroundProperty, new SolidColorBrush(Color.FromRgb(130, 130, 140)));
        attSaveHint.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        attSaveHint.SetValue(FrameworkElement.MarginProperty, new Thickness(5, 0, 0, 0));
        attStack.AppendChild(attSaveHint);
        attBorder.AppendChild(attStack);
        attItemTemplate.VisualTree = attBorder;

        var attCtrl = new FrameworkElementFactory(typeof(ItemsControl));
        attCtrl.SetValue(ItemsControl.ItemsSourceProperty, new System.Windows.Data.Binding("Attachments"));
        attCtrl.SetValue(ItemsControl.ItemTemplateProperty, attItemTemplate);
        var wrapPanelFactory = new FrameworkElementFactory(typeof(WrapPanel));
        wrapPanelFactory.SetValue(WrapPanel.OrientationProperty, Orientation.Horizontal);
        var itemsPanelTemplate = new ItemsPanelTemplate();
        itemsPanelTemplate.VisualTree = wrapPanelFactory;
        attCtrl.SetValue(ItemsControl.ItemsPanelProperty, itemsPanelTemplate);
        attCtrl.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 0, 0, 4));
        attCtrl.SetValue(UIElement.VisibilityProperty, new System.Windows.Data.Binding("Attachments") { Converter = new NullToVisibilityConverter() });
        outerStack.AppendChild(attCtrl);

        var roleText = new FrameworkElementFactory(typeof(TextBlock));
        roleText.SetValue(TextBlock.TextProperty, new System.Windows.Data.Binding("Role"));
        roleText.SetValue(TextBlock.FontSizeProperty, 10.0);
        roleText.SetValue(TextBlock.FontWeightProperty, FontWeights.Bold);
        roleText.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 0, 0, 2));

        roleText.SetValue(Control.ForegroundProperty, new System.Windows.Data.Binding("Role") { Converter = new RoleToLabelBrushConverter() });
        roleText.SetValue(UIElement.VisibilityProperty, new System.Windows.Data.Binding("IsCollapsible") { Converter = new InverseBoolToVisibilityConverter() });

        outerStack.AppendChild(roleText);

        // ── Collapsible header (visible when IsCollapsible, clickable to toggle) ──
        var collapsibleHeader = new FrameworkElementFactory(typeof(StackPanel));
        collapsibleHeader.SetValue(StackPanel.OrientationProperty, Orientation.Horizontal);
        collapsibleHeader.SetValue(FrameworkElement.CursorProperty, Cursors.Hand);
        collapsibleHeader.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 0, 0, 2));
        collapsibleHeader.SetValue(UIElement.VisibilityProperty, new System.Windows.Data.Binding("IsCollapsible") { Converter = new BoolToVisibilityConverter() });

        // Toggling collapsible messages (e.g. planner analysis) lives on the header row
        // only — clicking the message body is for text selection and must not collapse it.
        collapsibleHeader.AddHandler(UIElement.MouseLeftButtonDownEvent, new MouseButtonEventHandler((s, e) =>
        {
            if (s is StackPanel sp && sp.DataContext is ChatMessage msg && msg.IsCollapsible)
            {
                msg.IsExpanded = !msg.IsExpanded;
                e.Handled = true;
            }
        }));

        var toggleIcon = new FrameworkElementFactory(typeof(TextBlock));
        toggleIcon.SetValue(TextBlock.TextProperty, new System.Windows.Data.Binding("IsExpanded") { Converter = new ExpandedToToggleIconConverter() });
        toggleIcon.SetValue(TextBlock.FontSizeProperty, 10.0);
        toggleIcon.SetValue(Control.ForegroundProperty, new System.Windows.Data.Binding("Role") { Converter = new RoleToLabelBrushConverter() });
        toggleIcon.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        collapsibleHeader.AppendChild(toggleIcon);

        var headerLabel = new FrameworkElementFactory(typeof(TextBlock));
        headerLabel.SetValue(TextBlock.TextProperty, "Planner Analysis");
        headerLabel.SetValue(TextBlock.FontSizeProperty, 10.0);
        headerLabel.SetValue(TextBlock.FontWeightProperty, FontWeights.Bold);
        headerLabel.SetValue(Control.ForegroundProperty, new System.Windows.Data.Binding("Role") { Converter = new RoleToLabelBrushConverter() });
        collapsibleHeader.AppendChild(headerLabel);

        outerStack.AppendChild(collapsibleHeader);

        var contentText = new FrameworkElementFactory(typeof(MarkdownView));
        contentText.SetValue(MarkdownView.TextProperty, new System.Windows.Data.Binding("Content"));
        contentText.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 0, 20, 0));
        contentText.SetValue(FrameworkElement.FocusVisualStyleProperty, null);

        // Collapsible content wrapper: hidden when IsCollapsible && !IsExpanded
        var contentContainer = new FrameworkElementFactory(typeof(StackPanel));
        var containerStyle = new Style(typeof(StackPanel));
        var hiddenTrigger = new MultiDataTrigger();
        hiddenTrigger.Conditions.Add(new Condition(new System.Windows.Data.Binding("IsCollapsible"), true));
        hiddenTrigger.Conditions.Add(new Condition(new System.Windows.Data.Binding("IsExpanded"), false));
        hiddenTrigger.Setters.Add(new Setter(UIElement.VisibilityProperty, Visibility.Collapsed));
        containerStyle.Triggers.Add(hiddenTrigger);
        contentContainer.SetValue(FrameworkElement.StyleProperty, containerStyle);
        contentContainer.AppendChild(contentText);
        outerStack.AppendChild(contentContainer);

        var rootStack = new FrameworkElementFactory(typeof(StackPanel));
        rootStack.SetValue(StackPanel.OrientationProperty, Orientation.Vertical);

        border.AppendChild(outerStack);
        rootStack.AppendChild(border);

        // Clickable confirmation options (manual confirm mode / ask_user tool).
        // ItemsControl bound to ChatMessage.Options (ObservableCollection<string>);
        // each item is a button whose DataContext is the option string. Null when no
        // question is pending, so the whole block collapses via NullToVisibilityConverter.
        var optionsHost = new FrameworkElementFactory(typeof(StackPanel));
        optionsHost.SetValue(FrameworkElement.MarginProperty, new Thickness(2, 4, 0, 0));
        optionsHost.SetValue(UIElement.VisibilityProperty, new System.Windows.Data.Binding("Options") { Converter = new NullToVisibilityConverter() });
        var optionsList = new FrameworkElementFactory(typeof(ItemsControl));
        optionsList.SetValue(ItemsControl.ItemsSourceProperty, new System.Windows.Data.Binding("Options"));
        var optionTemplate = new FrameworkElementFactory(typeof(Button));
        optionTemplate.SetValue(Control.BackgroundProperty, Surface);
        optionTemplate.SetValue(Control.ForegroundProperty, Fg);
        optionTemplate.SetValue(Control.BorderBrushProperty, Border);
        optionTemplate.SetValue(Control.BorderThicknessProperty, new Thickness(1));
        optionTemplate.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 3, 0, 3));
        optionTemplate.SetValue(Control.PaddingProperty, new Thickness(10, 4, 10, 4));
        optionTemplate.SetValue(Control.FontSizeProperty, 12.0);
        optionTemplate.SetValue(Control.CursorProperty, System.Windows.Input.Cursors.Hand);
        optionTemplate.SetValue(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Left);
        optionTemplate.SetValue(ContentControl.ContentProperty, new System.Windows.Data.Binding());
        optionTemplate.AddHandler(System.Windows.Controls.Primitives.ButtonBase.ClickEvent, new RoutedEventHandler(OnChatOptionClick));
        optionsList.SetValue(ItemsControl.ItemTemplateProperty, new System.Windows.DataTemplate { VisualTree = optionTemplate });
        optionsHost.AppendChild(optionsList);
        rootStack.AppendChild(optionsHost);

        var btnPanel = new FrameworkElementFactory(typeof(StackPanel));
        btnPanel.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Right);
        btnPanel.SetValue(StackPanel.OrientationProperty, Orientation.Horizontal);
        btnPanel.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 2, 4, 0));

        var downloadAllBtn = MakeChatIconButton("\u2B07", "Download all", (s, e) =>
        {
            if (s is Button btn && btn.DataContext is ChatMessage msg && msg.Attachments != null && msg.Attachments.Count > 0)
            {
                var dlg = new OpenFolderDialog { Title = "Select download folder" };
                if (dlg.ShowDialog(System.Windows.Window.GetWindow(btn)) == true)
                {
                    int saved = 0;
                    foreach (var att in msg.Attachments)
                    {
                        if (!string.IsNullOrEmpty(att.FullPath) && File.Exists(att.FullPath))
                        {
                            var dest = System.IO.Path.Combine(dlg.FolderName, att.FileName);
                            try { File.Copy(att.FullPath, dest, true); saved++; }
                            catch { }
                        }
                    }
                }
            }
        });
        downloadAllBtn.SetValue(UIElement.VisibilityProperty, new System.Windows.Data.Binding("Attachments") { Converter = new NullToVisibilityConverter() });
        btnPanel.AppendChild(downloadAllBtn);

        var copyBtn = MakeChatIconButton("\U0001F4CB", "Copy content", (s, e) =>
        {
            if (s is Button btn && btn.Tag is string content && !string.IsNullOrEmpty(content))
            {
                try { System.Windows.Clipboard.SetText(content); } catch { }
            }
        });
        copyBtn.SetValue(FrameworkElement.TagProperty, new System.Windows.Data.Binding("Content"));
        btnPanel.AppendChild(copyBtn);

        var saveBtn = MakeChatIconButton("\U0001F4BE", "Save as .txt", (s, e) =>
        {
            if (s is Button btn && btn.Tag is string content && !string.IsNullOrEmpty(content))
            {
                var dlg = new Microsoft.Win32.SaveFileDialog
                {
                    Filter = "Text file (*.txt)|*.txt|All files (*.*)|*.*",
                    DefaultExt = ".txt",
                    FileName = "response.txt"
                };
                if (dlg.ShowDialog(System.Windows.Window.GetWindow(btn)) == true)
                {
                    try { System.IO.File.WriteAllText(dlg.FileName, content); }
                    catch (Exception ex) { System.Windows.MessageBox.Show($"Failed to save: {ex.Message}"); }
                }
            }
        });
        saveBtn.SetValue(FrameworkElement.TagProperty, new System.Windows.Data.Binding("Content"));
        btnPanel.AppendChild(saveBtn);

        rootStack.AppendChild(btnPanel);
        template.VisualTree = rootStack;
        return template;
    }

    /// <summary>
    /// Builds one of the small transparent 22x22 icon buttons under a chat bubble
    /// (download/copy/save). Pulls the ~10 lines of identical Width/Height/FontSize/
    /// Cursor/Background/Foreground/BorderThickness/Padding setup that used to be
    /// repeated for each button into one place — same rendered button every time,
    /// callers only supply what actually differs (icon, tooltip, click handler).
    /// </summary>
    private static FrameworkElementFactory MakeChatIconButton(string icon, string tooltip, RoutedEventHandler onClick)
    {
        var btn = new FrameworkElementFactory(typeof(Button));
        btn.SetValue(ContentControl.ContentProperty, icon);
        btn.SetValue(FrameworkElement.WidthProperty, 22.0);
        btn.SetValue(FrameworkElement.HeightProperty, 22.0);
        btn.SetValue(Control.FontSizeProperty, 12.0);
        btn.SetValue(Control.CursorProperty, Cursors.Hand);
        btn.SetValue(Control.BackgroundProperty, Brushes.Transparent);
        btn.SetValue(Control.ForegroundProperty, Brushes.Gray);
        btn.SetValue(Control.BorderThicknessProperty, new Thickness(0));
        btn.SetValue(Control.PaddingProperty, new Thickness(0));
        btn.SetValue(Control.ToolTipProperty, tooltip);
        btn.AddHandler(System.Windows.Controls.Primitives.ButtonBase.ClickEvent, onClick);
        return btn;
    }

    private void OnChatOptionClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.DataContext is not string option) return;
        var optionsControl = FindVisualAncestor<ItemsControl>(btn);
        if (optionsControl?.DataContext is not ChatMessage msg) return;
        var handler = msg.OptionChosen;
        msg.Options = null;
        handler?.Invoke(option);
    }

    private static T? FindVisualAncestor<T>(DependencyObject? node) where T : DependencyObject
    {
        while (node != null && node is not T)
            node = System.Windows.Media.VisualTreeHelper.GetParent(node);
        return node as T;
    }

    private async Task<string> HandleAskUserAsync(ToolCall tc, ChatMessage agentChatMsg, bool confirmManual)
    {
        if (!confirmManual)
            return "BLOCKED: ask_user is only available in Manual confirm mode. Proceed autonomously without asking the user.";

        var argsJson = tc.Function?.Arguments ?? "{}";
        var question = (AgenticWorkflow.GetArgFromCall(argsJson, "question") ?? "").Trim();
        var options = ParseAskUserOptions(argsJson);

        var violation = ValidateAskUserCall(question, options);
        if (violation != null)
            return $"ERROR: ask_user call rejected: {violation} Retry with a corrected call.";

        agentChatMsg.AppendContent($"\n\n**❓ {question}**");
        var tcs = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var optionCol = new System.Collections.ObjectModel.ObservableCollection<string>(options);
        agentChatMsg.Options = optionCol;
        agentChatMsg.OptionChosen = choice =>
        {
            agentChatMsg.Options = null;
            tcs.TrySetResult(choice);
        };
        using var cancelReg = _cts!.Token.Register(() => tcs.TrySetResult(null));
        ScrollChatToEnd();
        _statusLabel.Content = "Waiting for your answer...";

        var answer = await tcs.Task;
        if (string.IsNullOrEmpty(answer))
            return "USER DID NOT ANSWER: the user cancelled the question. Decide autonomously based on your best judgment and continue.";
        return $"USER ANSWER: {answer}";
    }

    private static string? ValidateAskUserCall(string question, List<string> options)
    {
        if (string.IsNullOrWhiteSpace(question))
            return "'question' must be a non-empty string.";
        if (question.Length > 300)
            return "'question' is too long (max 300 chars).";
        if (options.Count < 2)
            return "'options' must contain at least 2 choices (2-4 short, mutually-exclusive options).";
        if (options.Count > 4)
            return $"'options' must contain at most 4 choices — got {options.Count}. Pick only the 2-4 most relevant ones.";
        var tooLong = options.FirstOrDefault(o => o.Length > 80);
        if (tooLong != null)
            return $"each option must be SHORT (max 80 chars) — rephrase option '{tooLong.Truncate(40)}'.";
        return null;
    }

    private static List<string> ParseAskUserOptions(string argsJson)
    {
        var result = new List<string>();
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(argsJson);
            if (doc.RootElement.TryGetProperty("options", out var el) && el.ValueKind == System.Text.Json.JsonValueKind.Array)
            {
                foreach (var item in el.EnumerateArray())
                {
                    if (item.ValueKind != System.Text.Json.JsonValueKind.String) continue;
                    var s = (item.GetString() ?? "").Trim();
                    if (string.IsNullOrWhiteSpace(s)) continue;
                    if (result.Any(existing => string.Equals(existing, s, StringComparison.OrdinalIgnoreCase))) continue;
                    result.Add(s);
                }
            }
        }
        catch { }
        return result;
    }
    bool wroteFilesSinceBuild = false;
    bool nudgedForBuild = false;
    bool wroteAnyFileThisTurn = false;
    int buildNudgeCount = 0;
    const int maxBuildNudges = 3;
    private async void OnChatSendClick(object sender, RoutedEventArgs e)
    {
        if (_isGenerating)
        {
            Log("Cancelling...");
            _cts?.Cancel();
            if (_koboldClient != null)
                _ = _koboldClient.AbortGenerationAsync();
            return;
        }

        try
        {
            if (string.Equals(_settings.AgenticWorkflowMode, "enable", StringComparison.OrdinalIgnoreCase))
            {
                var projPath = _activeSession?.ProjectPath ?? "";
                if (string.IsNullOrWhiteSpace(projPath) || !Directory.Exists(projPath))
                {
                    Log("Agentic: Set a project folder in the session first.");
                    return;
                }
            }
            var text = _chatInputBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(text) && _textAttachments.Count == 0) return;
            _chatInputBox.Clear();

            var textFileContents = new List<string>();
            var imagePaths = new List<string>();
            var attachmentInfos = new ObservableCollection<AttachmentInfo>();
            foreach (var att in _textAttachments)
            {
                var ext = Path.GetExtension(att).ToLowerInvariant();
                if (ext is ".txt" or ".md" or ".cs" or ".py" or ".js" or ".ts" or ".json" or ".xml" or ".html" or ".css" or ".yaml" or ".yml" or ".ini" or ".cfg" or ".sh" or ".bat" or ".ps1")
                    textFileContents.Add(await File.ReadAllTextAsync(att));
                else
                    imagePaths.Add(att);
                attachmentInfos.Add(new AttachmentInfo
                {
                    FileName = Path.GetFileName(att),
                    FullPath = att,
                    IsImage = ext is ".png" or ".jpg" or ".jpeg" or ".webp" or ".gif" or ".bmp",
                    Icon = GetFileIcon(ext)
                });
            }
            if (textFileContents.Count > 0)
                text = string.Join("\n\n---\n\n", textFileContents) + "\n\n---\n\n" + text;
            _textAttachments.Clear();
            RebuildTextAttachChips();

            _chatHistory.Add(new ChatMessage { Role = "user", Content = text, ImagePath = imagePaths.Count > 0 ? imagePaths[0] : null, Attachments = attachmentInfos.Count > 0 ? attachmentInfos : null });
            while (_chatHistory.Count > _maxHistoryCount)
                _chatHistory.RemoveAt(0);
            ScrollChatToEnd();

            if (_settings.BackendMode == "local" && !await EnsureKoboldModeReadyAsync(KoboldMode.Text))
                return;

            var enableThinking = _textEnableThinking.SelectedIndex == 1;
            var reasoningEffort = enableThinking ? _settings.ThinkingEffort : null;

            var maxTokens = CalcMaxTokens();

            _statusLabel.Content = "Thinking...";
            _generateBtn.IsEnabled = false;
            _isGenerating = true;
            UpdateTabLockState();
            _textSendBtn.Content = "\u23F9";
            _textSendBtn.Background = Error;
            _cts?.Dispose();
            _cts = new CancellationTokenSource();

            string reply;
            if (imagePaths.Count > 0)
            {
                var historyForVision = _chatHistory.ToList();
                var activeProjectPath = _activeSession?.ProjectPath ?? "";
                var agenticEnabled = _settings.AgenticWorkflowMode == "enable" && !string.IsNullOrWhiteSpace(activeProjectPath) && Directory.Exists(activeProjectPath);
                if (agenticEnabled)
                {
                    AgenticWorkflow.TreeSitterSyntaxCheckEnabled = _settings.EnableTreeSitterCheck;
                    TreeSitterChecker.EnablePlaceholderCheck = _settings.EnableTreeSitterPlaceholderCheck;
                    TreeSitterChecker.EnableDeadCodeCheck = _settings.EnableTreeSitterDeadCodeCheck;
                    // Start each agentic query fresh — only include the current user message,
                    // not old regular-chat history that would pollute the agent's context.
                    var historyForApi = new List<ChatMessage> { _chatHistory[^1] };

                    // ── RESET THE LEDGER AND DISCOVERY GATE ON EVERY NEW PROMPT ──
                    _activeSession?.ReadLedger.Clear();
                    _activeSession?.DiscoveredPaths.Clear();

                    // Seed the discovery gate with any file paths the user explicitly
                    // mentioned in their message so they can be read without first searching.
                    if (_activeSession != null)
                    {
                        var userPaths = ExtractPathsFromText(_chatHistory[^1].Content ?? "");
                        foreach (var up in userPaths)
                        {
                            var normalized = up.Replace('\\', '/').TrimStart('.', '/');
                            if (!string.IsNullOrWhiteSpace(normalized))
                                _activeSession.DiscoveredPaths.Add(normalized);
                        }
                    }
                }
                if (!string.IsNullOrWhiteSpace(_settings.TextSystemPrompt))
                    historyForVision.Insert(0, new ChatMessage { Role = "system", Content = _settings.TextSystemPrompt });
                reply = await Task.Run(async () =>
                    await _koboldClient!.SendMultimodalChatAsync(historyForVision, text, imagePaths, maxTokens,
                        _settings.TextTemperature, _settings.TextTopP, _settings.TextTopK, _settings.TextRepeatPenalty,
                        _cts.Token));
                _chatHistory.Add(new ChatMessage { Role = "assistant", Content = reply });
                ScrollChatToEnd();
            }
            else
            {
                var activeProjectPath = _activeSession?.ProjectPath ?? "";
                var agenticEnabled = _settings.AgenticWorkflowMode == "enable" && !string.IsNullOrWhiteSpace(activeProjectPath) && Directory.Exists(activeProjectPath);
                string? projectContext = null;
                if (agenticEnabled)
                {
                    AgenticWorkflow.TreeSitterSyntaxCheckEnabled = _settings.EnableTreeSitterCheck;
                    TreeSitterChecker.EnablePlaceholderCheck = _settings.EnableTreeSitterPlaceholderCheck;
                    TreeSitterChecker.EnableDeadCodeCheck = _settings.EnableTreeSitterDeadCodeCheck;
                    var aw = new AgenticWorkflow { ProjectPath = activeProjectPath };
                    projectContext = aw.GetProjectContext();
                    if (!string.IsNullOrWhiteSpace(projectContext))
                        Log("Agentic: Project context loaded.");
                }

                if (agenticEnabled)
                {
                    _tpsLabel.Content = "0.0 t/s";
                    var historyForApi = new List<ChatMessage> { _chatHistory[^1] };
                    var confirmManual = string.Equals(_settings.ConfirmMode, "manual", StringComparison.OrdinalIgnoreCase);

                    _activeSession?.ReadLedger.Clear();
                    _activeSession?.DiscoveredPaths.Clear();
                    if (_activeSession != null) { _activeSession.UserIntent = ""; _activeSession.TodoList.Clear(); _activeSession.Notes.Clear(); _activeSession.NextTodoId = 1; _activeSession.BlindReadCount = 0; _activeSession.HasRunCommandThisTask = false; _activeSession.WritesSinceLastNotesUpdate = 0; _activeSession.MutatedPathsSinceNotesUpdate.Clear(); }

                    if (_activeSession != null)
                    {
                        var userPaths = ExtractPathsFromText(_chatHistory[^1].Content ?? "");
                        foreach (var up in userPaths)
                        {
                            var normalized = up.Replace('\\', '/').TrimStart('.', '/');
                            if (!string.IsNullOrWhiteSpace(normalized))
                                _activeSession.DiscoveredPaths.Add(normalized);
                        }
                    }
                    historyForApi.Insert(0, new ChatMessage { Role = "system", Content = _settings.CompactPrompt ? AgenticWorkflow.GetAgenticInstruction(activeProjectPath, confirmManual) : AgenticWorkflow.GetAgenticInstructionExtended(activeProjectPath, confirmManual) }); if (!string.IsNullOrWhiteSpace(_settings.TextSystemPrompt))
                        historyForApi.Insert(0, new ChatMessage { Role = "system", Content = _settings.TextSystemPrompt });
                    if (!string.IsNullOrWhiteSpace(projectContext))
                        historyForApi.Insert(0, new ChatMessage { Role = "system", Content = projectContext });

                    string? initialPlan = null;

                    // ── Planner: secondary local GGUF model generates project-context summary ──
                    if (!_settings.PlannerEnabled)
                    { }
                    else if (_settings.BackendMode != "local")
                        Log("Agentic: Planner skipped — requires BackendMode = local");
                    else if (string.IsNullOrWhiteSpace(_settings.PlannerModelPath))
                        Log("Agentic: Planner skipped — PlannerModelPath is empty");
                    else if (!File.Exists(_settings.PlannerModelPath))
                        Log($"Agentic: Planner skipped — model not found at: {_settings.PlannerModelPath}");
                    else if (string.IsNullOrWhiteSpace(projectContext))
                        Log("Agentic: Planner skipped — project context is empty (set project folder in Sessions tab)");
                    else if (string.IsNullOrWhiteSpace(_settings.PlannerTemplatePath) || !File.Exists(_settings.PlannerTemplatePath))
                        Log("Agentic: Planner skipped — PlannerTemplatePath is empty or file not found (disable Planner in advanced panel or set a template in Settings → Text)");
                    else
                    {
                        Log("Agentic: Planner triggered — loading planner model...");
                        var origUserPrompt = _chatHistory[^1];
                        _statusLabel.Content = "Exploring...";

                        var accumulatedPlan = await GeneratePlannerAnalysisAsync(activeProjectPath, projectContext, origUserPrompt.Content ?? "", _cts.Token);

                        if (!string.IsNullOrWhiteSpace(accumulatedPlan))
                        {
                            initialPlan = accumulatedPlan;
                            historyForApi.Insert(0, new ChatMessage
                            {
                                Role = "system",
                                Content = $"[PLANNER]\n{accumulatedPlan}\n[/PLANNER]\n\nThis is your project blueprint — analysis is already done. Use it to pick which files to read and plan your work. " +
                                    "Do NOT re-analyze, list dirs, search files, or write your own \"## Planner Analysis\" / \"Project Summary\" / architecture breakdown — that step is complete and repeating it wastes time. " +
                                    "Go straight to reading/editing the specific files this request needs, or answer directly if no files are needed."
                            });
                            // Auto-store planner output into session notes so the agent
                            // can recall them via get_notes() after compaction.
                            if (_activeSession != null)
                            {
                                _activeSession.UserIntent = accumulatedPlan.Truncate(2000);
                                _plannerPopulatedNotes = true;
                            }
                        }
                        else
                        {
                            Log("Agentic: Planner finished — no analysis generated.");
                        }

                        _statusLabel.Content = "Thinking...";

                        // Label the user's original request so the agent model can distinguish
                        // it from the planner analysis injected above.
                        historyForApi[^1] = new ChatMessage
                        {
                            Role = "user",
                            Content = $"## User Request\n{origUserPrompt.Content}",
                            ImagePath = origUserPrompt.ImagePath,
                            Attachments = origUserPrompt.Attachments
                        };
                    }

                    if (string.IsNullOrWhiteSpace(activeProjectPath) || !Directory.Exists(activeProjectPath))
                    {
                        historyForApi.Insert(0, new ChatMessage
                        {
                            Role = "system",
                            Content = "NOTE: No project folder is configured for this session. Your tools cannot operate without a project folder. Tell the user to set one in the 'Sessions' tab and try again."
                        });
                    }

                    if (_settings.BackendMode != "external" && _settings.SendToolsToLocalBackend
                        && _koboldProcess?.UseJinjaTools != true)
                    {
                        historyForApi.Insert(0, new ChatMessage
                        {
                            Role = "system",
                            Content = "## Structured Tool Definitions\n" + AgenticWorkflow.GetToolDefinitionsJson(confirmManual)
                        });
                    }

                    int maxIterations = _settings.MaxIterations;
                    const int maxConsecutiveBackendErrors = 3;
                    const int maxConfirmationNudges = 3;
                    int consecutiveBackendErrors = 0;
                    bool contextBudgetWarned = false;
                    int confirmationNudges = 0;
                    bool wroteFilesSinceBuild = false;
                    bool nudgedForBuild = false;
                    bool wroteAnyFileThisTurn = false;
                    int nudgedForCodeInChatCount = 0;
                    bool statedExplicitPlan = false;
                    string? lastPlanSnippet = null;
                    int noChangesContradictionNudges = 0;
                    const int maxNoChangesContradictionNudges = 2;
                    bool hasNudgedForRedundantRead = false;
                    int emptyResponseNudges = 0;
                    string? finalContent = null;

                    int lastCompactIter = -3;

                    _statusLabel.Content = "Running agent...";

                    var agentChatMsg = new ChatMessage { Role = "assistant", Content = "", Attachments = new ObservableCollection<AttachmentInfo>() };
                    if (!string.IsNullOrWhiteSpace(initialPlan))
                        agentChatMsg.AppendContent($"\n\n<<COLLAPSE:Planner Analysis>>{initialPlan}<</COLLAPSE>>\n");
                    _chatHistory.Add(agentChatMsg);
                    ScrollChatToEnd();

                    var filesChangedThisTurn = new List<string>();
                    var filesReadThisTurn = new List<string>();
                    var toolCallsThisTurn = new Dictionary<string, int>();
                    var baseHistoryCount = historyForApi.Count;

                    var agenticSession = agenticEnabled ? _activeSession : null;
                    var redundantReadCount = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                    var redundantReadNudgeSent = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

                    // Tool-agnostic stall detector: counts consecutive tool calls (of ANY kind —
                    // read_file, search_files, run_command, list_directory, analyze_method...)
                    // since the last successful file-modifying call. This is the general
                    // replacement for trying to detect "stalling" inside each individual tool's
                    // own logic (which is exactly what the ReadLedger window-shift patch was
                    // doing, and which is fundamentally gameable — there's always another tool
                    // or a slightly different argument that looks like "new" progress). One
                    // counter, tool-agnostic, catches all of them the same way.
                    int StallNudgeThreshold = _settings.StallNudgeThreshold;
                    int StallLockoutThreshold = _settings.StallLockoutThreshold;
                    int callsSinceLastWrite = 0;
                    bool stallNudgeSent = false;
                    var progressTools = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                        { "write_file", "delete_file", "move_file", "rename_file", "copy_file" };
                    var neutralTools = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                        { "update_notes", "get_notes" };

                    // Unified read_file counter — global (like callsSinceLastWrite above), and
                    // counts BOTH full reads and ranged reads together into one number. This
                    // replaces an earlier version that kept full-reads and ranged-reads as
                    // separate PER-FILE dictionaries with their own thresholds — that was three
                    // counters (full/ranged/nudge-sent) doing a job one counter can do, and
                    // per-file tracking meant a model could dodge it by spreading reads across
                    // many different paths. One number, one pair of thresholds: softly nudge
                    // past 4 read_file calls since the last write, hard-block past 8. Resets to
                    // 0 the moment ANY write_file/delete_file/move_file/rename_file/copy_file
                    // call succeeds (see the reset alongside callsSinceLastWrite below).
                    int ReadFileNudgeThreshold = _settings.ReadFileNudgeThreshold;
                    int ReadFileHardStopThreshold = _settings.ReadFileHardStopThreshold;
                    int readFileCount = 0;
                    bool readFileNudgeSent = false;

                    // Intent gate — mirrors GATE A/B/C for the batch checklist: the prompt has told
                    // the model since forever that update_notes(intent=..., todo_add=...) at the START
                    // of a task is [CRITICAL], but that's prompt-only and gets skipped once the
                    // auto-logged write_file summaries make it feel unnecessary. This makes the
                    // very first write_file/run_command of a task actually wait for one update_notes
                    // call first. Local to this task's loop, so it resets every task like the
                    // counters above it — it is NOT meant to persist across tasks or turns.
                    bool hasDeclaredIntent = false;
                    int completionReprompts = 0;

                    for (int iter = 0; iter < maxIterations; iter++)
                    {
                        ChatCompletionResponse? response = null;
                        _statusLabel.Content = $"Agent iteration {iter + 1}...";

                        // Run BEFORE PruneStaleToolResults: dedupe removes the earlier pair
                        // wholesale (assistant call + tool result), so the stale-stub never
                        // spends effort stubbing something we are about to delete anyway.
                        var dupChars = PruneDuplicateToolCalls(historyForApi, baseHistoryCount);
                        if (dupChars > 0)
                            Log($"Agentic: pruned {dupChars} duplicated call chars (same function + same args re-executed) from context.");

                        var prunedChars = PruneStaleToolResults(historyForApi);
                        if (prunedChars > 0)
                            Log($"Agentic: pruned {prunedChars} stale chars (superseded read_file results) from context.");

                        if (iter > lastCompactIter + 1)
                        {
                            var garbageRemoved = 0;

                            for (int gi = historyForApi.Count - 1; gi >= baseHistoryCount; gi--)
                            {
                                var gm = historyForApi[gi];
                                var gc = gm.Content ?? "";

                                if (gm.Role == "tool" && AgenticWorkflow.IsToolFailure(gc))
                                {
                                    historyForApi.RemoveAt(gi);
                                    garbageRemoved++;
                                }
                                // FIX: !gc.StartsWith(NudgeMarker) — this used to delete EVERY
                                // system/user message added during the turn (nearly every iteration,
                                // since lastCompactIter only advances on an actual compaction). That
                                // silently erased every AgentNudgeRole warning (stall/read/redundant-
                                // read/loop-break/build-reminder/etc.) before the model's next request
                                // ever went out, leaving only hard "tool" role BLOCKED results with no
                                // graduated warning beforehand. All nudges are now tagged with
                                // NudgeMarker (see TagIfNudgeIsUserRole) so they're preserved here.
                                else if ((gm.Role is "system" or "user") && !gc.Contains("Web search results for") && !gc.StartsWith(NudgeMarker))
                                {
                                    historyForApi.RemoveAt(gi);
                                    garbageRemoved++;
                                }
                            }
                            if (garbageRemoved > 0)
                            {
                                Log($"Agentic: pruned {garbageRemoved} garbage messages from context.");
                            }
                        }

                        var promptTokensNow = EstimateTokenCount(historyForApi);

                        if (promptTokensNow > _settings.ContextSize * 0.80 && iter > lastCompactIter + 2)
                        {
                            lastCompactIter = iter;
                            Log($"Agentic: context at ~{promptTokensNow}/{_settings.ContextSize} — compacting...");
                            agentChatMsg.AppendContent($"\n\n[Context at ~{promptTokensNow / 1000}k/{_settings.ContextSize / 1000}k — compacting...]");

                            try
                            {
                                var compacted = await CompactHistoryAsync(historyForApi, reasoningEffort, _cts.Token);
                                if (!string.IsNullOrWhiteSpace(compacted))
                                {
                                    var newHistory = new List<ChatMessage>();
                                    newHistory.AddRange(historyForApi.Take(baseHistoryCount));

                                    // Re-run the planner so the blueprint reflects the project's
                                    // current state — the one generated at turn start may now be
                                    // stale after however many file edits happened since then.
                                    if (_settings.PlannerEnabled)
                                    {
                                        Log("Agentic: compaction triggered — re-running planner for a fresh blueprint...");
                                        var replanRequestText = historyForApi.Take(baseHistoryCount)
                                            .LastOrDefault(m => m.Role == "user")?.Content ?? "Continue the current task.";
                                        var freshPlan = await GeneratePlannerAnalysisAsync(activeProjectPath, projectContext, replanRequestText, _cts.Token);
                                        if (!string.IsNullOrWhiteSpace(freshPlan))
                                        {
                                            newHistory.Add(new ChatMessage
                                            {
                                                Role = "system",
                                                Content = $"[PLANNER]\n{freshPlan}\n[/PLANNER]\n\nThis is your refreshed project blueprint, re-generated after compaction to reflect the project's current state. " +
                                                    "Use it to pick which files to read and plan your work. Do NOT re-analyze, list dirs, search files, or write your own \"## Planner Analysis\" section — that step is complete."
                                            });
                                            agentChatMsg.AppendContent($"\n\n<<COLLAPSE:Planner re-analyzed after compaction ({freshPlan.Length} chars)>>{freshPlan}<</COLLAPSE>>\n");
                                        }
                                    }

                                    newHistory.Add(new ChatMessage { Role = AgentNudgeRole, Content = TagIfNudgeIsUserRole($"## Session Compacted\n\n{compacted}") });
                                    // Re-inject the recorded notes + todo checklist so the agent knows what it was working on
                                    if (agenticSession != null && (!string.IsNullOrWhiteSpace(agenticSession.UserIntent) || agenticSession.TodoList.Count > 0 || agenticSession.Notes.Count > 0))
                                    {
                                        var noteBlock = $"## Your notes\n\n{AgenticWorkflow.RenderNotesBlock(agenticSession)}\n\nContinue working on the task above.";
                                        newHistory.Add(new ChatMessage { Role = AgentNudgeRole, Content = TagIfNudgeIsUserRole(noteBlock) });
                                    }
                                    newHistory.Add(new ChatMessage { Role = "user", Content = "Session was compacted and summarized above. Continue working with these steps in-orderly : (1) Read the compacted session properly, those are your traces to continue where you left off. (2) call the next tool that's appropriate for the 'Next Move' stated in the compaction NOW, read also the user prompt/intents in the compaction above (3) MUST call get_notes to re-read your notes after and read what files have written so far AND see the written reasons as well, these are your traces to continue where you left off. Call a tool NOW, DO NOT stop." });

                                    historyForApi = newHistory;
                                    Log($"Agentic: compaction complete ({compacted.Length} chars)");

                                    // ── RESET LEDGER AND DISCOVERY ON COMPACTION ──
                                    // The raw file content was just summarized and removed from context.
                                    // The ledger must be wiped, or the agent will be blocked from reading files 
                                    // it "already saw" but no longer has in its context window!
                                    agenticSession?.ReadLedger.Clear();
                                    agenticSession?.DiscoveredPaths.Clear();

                                    // The unified read_file counter (and the generic stall counter) count
                                    // calls since the last WRITE, not since the last compaction — but
                                    // compaction just wiped the ReadLedger specifically so the model can
                                    // legitimately re-read files whose raw content was summarized out of
                                    // context. Leaving these counters at their pre-compaction values would
                                    // immediately hard-stop or lockout a model that does exactly what the
                                    // compaction message above asks it to do (re-read via startLine/endLine
                                    // if needed) — the budget has to restart with the fresh context.
                                    readFileCount = 0;
                                    readFileNudgeSent = false;
                                    callsSinceLastWrite = 0;
                                    stallNudgeSent = false;

                                    agentChatMsg.AppendContent(
                                        $"\n\n<<COLLAPSE:Context compacted ({compacted.Length} chars)>>{compacted}<</COLLAPSE>>\n");
                                    ScrollChatToEnd();

                                    consecutiveBackendErrors = 0;
                                    contextBudgetWarned = false;
                                    confirmationNudges = 0;
                                    wroteFilesSinceBuild = false;
                                    nudgedForBuild = false;
                                    wroteAnyFileThisTurn = false;
                                    nudgedForCodeInChatCount = 0;
                                    statedExplicitPlan = false;
                                    noChangesContradictionNudges = 0;
                                    hasNudgedForRedundantRead = false;
                                    filesChangedThisTurn.Clear();
                                    filesReadThisTurn.Clear();
                                    redundantReadCount.Clear();
                                    redundantReadNudgeSent.Clear();
                                    toolCallsThisTurn.Clear();

                                    // Compaction restarts the context, so the iteration counter
                                    // restarts with it: the old +1 budget bump left iter at its
                                    // pre-compaction value, giving the model at most one or two
                                    // iterations to act on the freshly compacted history. But the
                                    // budget also HALVES per compaction — a plain reset would let a
                                    // runaway model compact → refill → compact forever, each round
                                    // burning a full 2048-token summarization call. Halving bounds
                                    // the turn to roughly 2x its original budget across all
                                    // compactions; the for-loop's iter++ turns the -1 into a fresh
                                    // 0 for the next iteration.
                                    lastCompactIter = -3; // re-arm the anti-back-to-back compaction gate against the fresh counter
                                    iter = -1;
                                    if (maxIterations > 2)
                                    {
                                        maxIterations /= 2;
                                        Log($"Agentic: compaction reset iteration counter and halved budget to {maxIterations}.");
                                    }
                                    else
                                    {
                                        Log($"Agentic: compaction reset iteration counter (budget stays {maxIterations}).");
                                    }
                                    continue;
                                }
                            }
                            catch (Exception ex)
                            {
                                Log($"Agentic: compaction failed ({ex.Message}) — continuing without compaction");
                            }
                        }

                        if (promptTokensNow > _settings.ContextSize * 0.65 && !contextBudgetWarned)
                        {
                            contextBudgetWarned = true;
                            historyForApi.Add(new ChatMessage
                            {
                                Role = AgentNudgeRole,
                                Content = TagIfNudgeIsUserRole($"CONTEXT BUDGET WARNING: the conversation is at ~{promptTokensNow}/{_settings.ContextSize} tokens. " +
                                    "Stop reading more files. If you already know what to change, write_file now with the full updated content. " +
                                    "Do not call read_file, list_directory, or search_files again this turn.")
                            });
                            agentChatMsg.AppendContent("\n\n[Ready for compaction — forcing agent to wrap up]");
                        }

                        // First-turn nudge: prompt the agent to initialize its scratchpad
                        if (iter == 0 && _activeSession != null)
                        {
                            if (_plannerPopulatedNotes)
                            {
                                historyForApi.Add(new ChatMessage
                                {
                                    Role = AgentNudgeRole,
                                    Content = TagIfNudgeIsUserRole("The planner analysis has been stored in your notes. Before making other tool calls, call get_notes() to read the planner output, then call update_notes(intent=\"<your plan derived from planner>\") to record your structured work plan. Note: todo_add is only accepted once you've explored the code (list_directory/search_files/read_file or a build error) — after that, formalize the planner output into actionable TODOs with update_notes(todo_add=\"one step per line\").")
                                });
                            }
                            else if (string.IsNullOrWhiteSpace(_activeSession.UserIntent) && _activeSession.TodoList.Count == 0)
                            {
                                historyForApi.Add(new ChatMessage
                                {
                                    Role = AgentNudgeRole,
                                    Content = TagIfNudgeIsUserRole("REMINDER: record intent with update_notes(intent=\"what you're doing and why\") now. Once you've explored the code, declare your work checklist with update_notes(todo_add=\"one item per line\") — every item gets an id, close each one with todo_complete=\"<id>\" after a real write. Builds are blocked until the checklist is declared and every item is closed, so don't skip it.")
                                });
                            }
                        }

                        var iterMaxTokens = CalcMaxTokensForPrompt(historyForApi);

                        try
                        {
                            _tpsTimestamp = Stopwatch.GetTimestamp();
                            response = await SendChatCompletionViaBackendAsync(historyForApi,
                                reasoningEffort, iterMaxTokens,
                                _settings.TextTemperature, _settings.TextTopP, _settings.TextTopK, _settings.TextRepeatPenalty,
                                AgenticWorkflow.GetToolDefinitions(confirmManual), null, _cts.Token);
                        }
                        catch (Exception ex)
                        {
                            bool isUserCancel = (ex is OperationCanceledException || ex is TaskCanceledException)
                                                 && _cts?.IsCancellationRequested == true;
                            if (isUserCancel)
                            {
                                // The user hit Stop (or clicked Send again mid-generation, which doubles as
                                // Stop). _cts.Cancel() was called but _cts itself isn't replaced until the
                                // NEXT prompt is submitted, so it stays cancelled for the rest of this turn.
                                // The old code fell through to the generic retry path below, which retried
                                // against that same already-cancelled token — guaranteed to fail instantly,
                                // three times in a row, then printed "[Giving up after repeated backend
                                // failures.]" as if something had actually gone wrong on the backend. Nothing
                                // did; the user just wanted to stop. Exit quietly instead.
                                Log("Agentic: generation cancelled by user.");
                                agentChatMsg.AppendContent("\n\n[Stopped]");
                                break;
                            }

                            bool isTimeout = ex is OperationCanceledException || ex is TaskCanceledException;
                            var timeoutInfo = _settings.TextTimeoutSeconds > 0 ? $" ({_settings.TextTimeoutSeconds}s limit)" : "";
                            var reason = isTimeout ? $"timed out{timeoutInfo}" : $"failed ({ex.Message})";
                            Log($"Agentic error (iteration {iter + 1}): request {reason}");
                            if (consecutiveBackendErrors < maxConsecutiveBackendErrors)
                            {
                                Log("Agentic: retrying API call...");
                                await Task.Delay(1000 * (consecutiveBackendErrors + 1));
                                try
                                {
                                    _tpsTimestamp = Stopwatch.GetTimestamp();
                                    response = await SendChatCompletionViaBackendAsync(historyForApi,
                                        reasoningEffort, iterMaxTokens,
                                        _settings.TextTemperature, _settings.TextTopP, _settings.TextTopK, _settings.TextRepeatPenalty,
                                        AgenticWorkflow.GetToolDefinitions(confirmManual), null, _cts.Token);
                                    consecutiveBackendErrors = 0;
                                    goto afterSend;
                                }
                                catch { }
                            }

                            consecutiveBackendErrors++;
                            agentChatMsg.AppendContent($"\n\n[Backend {reason}]");

                            if (consecutiveBackendErrors >= maxConsecutiveBackendErrors)
                            {
                                agentChatMsg.AppendContent("\n\n[Giving up after repeated backend failures.]");
                                Log("Agentic: aborting after repeated consecutive backend errors.");
                                break;
                            }

                            historyForApi.Add(new ChatMessage
                            {
                                Role = AgentNudgeRole,
                                Content = TagIfNudgeIsUserRole("NOTE: The previous backend request failed. "
                                    + "Your last response was lost — re-send it. "
                                    + "IMPORTANT: Do NOT repeat read_file calls, you have enough context to use write_file to apply fixes "
                                    + "(listed under Files Tracked This Session). Use the content you already retrieved. "
                                    + "Considering write_file now.")
                            });
                            continue;
                        }

                    afterSend:;

                        var choice = response?.Choices?.FirstOrDefault();

                        if (choice?.Message == null || (string.IsNullOrWhiteSpace(choice.Message.Content) && choice.Message.ToolCalls is not { Count: > 0 }))
                        {
                            consecutiveBackendErrors++;
                            try
                            {
                                var diag = System.Text.Json.JsonSerializer.Serialize(response, new System.Text.Json.JsonSerializerOptions { WriteIndented = false });
                                Log($"[Agentic EMPTY] iter={iter} finish_reason={choice?.FinishReason ?? "(null)"} raw={diag}");
                            }
                            catch (Exception diagEx)
                            {
                                Log($"[Agentic EMPTY] iter={iter} (diagnostic serialize failed: {diagEx.Message})");
                            }

                            Log($"Agentic: Empty response received from model.");
                            agentChatMsg.AppendContent("\n\n[Empty response received — nudging model to retry]");

                            if (consecutiveBackendErrors >= maxConsecutiveBackendErrors)
                            {
                                agentChatMsg.AppendContent("\n\n[Giving up after repeated empty responses.]");
                                Log("Agentic: aborting after repeated consecutive empty responses.");
                                break;
                            }

                            historyForApi.Add(new ChatMessage
                            {
                                Role = AgentNudgeRole,
                                Content = TagIfNudgeIsUserRole("You returned an empty response. If you are finished, reply with NO_CHANGES_NEEDED or provide a summary. If you need to do more work, call tool now.")
                            });
                            continue;
                        }

                        var msg = choice.Message;
                        consecutiveBackendErrors = 0;

                        var elapsed = Stopwatch.GetElapsedTime(_tpsTimestamp).TotalSeconds;
                        var content = msg.Content ?? "";
                        var totalChars = content.Length;
                        if (totalChars == 0 && msg.ToolCalls is { Count: > 0 })
                        {
                            foreach (var tc in msg.ToolCalls)
                                totalChars += tc.Function.Name.Length + tc.Function.Arguments.Length;
                        }
                        // Prefer the backend's actual completion_tokens count when the response
                        // reports one — a chars/4 guess is routinely 20-30% off on tool-call JSON
                        // and code (lots of short symbol/punctuation tokens), which is a big part
                        // of why this number looked "wrong" rather than just approximate.
                        var reportedTokens = response?.Usage?.CompletionTokens;
                        if (elapsed > 0 && (reportedTokens is > 0 || totalChars > 0))
                        {
                            var approxTokens = reportedTokens is > 0 ? reportedTokens.Value : totalChars / 4.0;
                            _tpsLabel.Content = $"{approxTokens / elapsed:F1} t/s";
                        }

                        List<ToolCall>? toolCalls = msg.ToolCalls;
                        string displayContent = msg.Content ?? "";

                        if (!string.IsNullOrWhiteSpace(displayContent))
                        {
                            var parsedFromContent = AgenticWorkflow.TryParseAllFunctionCalls(displayContent, out var cleaned);
                            displayContent = cleaned;
                            if (parsedFromContent.Count > 0)
                            {
                                displayContent = cleaned;
                                if (toolCalls is not { Count: > 0 })
                                    toolCalls = parsedFromContent;
                            }
                        }

                        if (toolCalls is { Count: > 0 })
                            AgenticWorkflow.PairWriteFileWithCodeBlocks(toolCalls, msg.Content ?? "");

                        if (toolCalls is not { Count: > 0 } && !wroteAnyFileThisTurn && !string.IsNullOrWhiteSpace(displayContent) && !SaysNoChangesNeeded(displayContent))
                        {
                            var extracted = AgenticWorkflow.ExtractCodeBlocksFromText(displayContent);
                            if (extracted.Count > 0)
                            {
                                displayContent = System.Text.RegularExpressions.Regex.Replace(displayContent, @"```(\w+)\s+(?:filename|file)=[""']([^""']+)[""']\s*\n.*?```", "", System.Text.RegularExpressions.RegexOptions.Singleline).Trim();

                                toolCalls = new List<ToolCall>();
                                foreach (var (fnName, argsJson) in extracted)
                                {
                                    toolCalls.Add(new ToolCall
                                    {
                                        Id = "call_extracted_" + Guid.NewGuid().ToString("N")[..12],
                                        Type = "function",
                                        Function = new ToolCallFunction { Name = fnName, Arguments = argsJson }
                                    });
                                }
                            }
                        }

                        if (!string.IsNullOrWhiteSpace(displayContent))
                            statedExplicitPlan = LooksLikeExplicitEditPlan(displayContent);

                        if (!string.IsNullOrWhiteSpace(displayContent))
                        {
                            agentChatMsg.AppendContent(displayContent);
                            ScrollChatToEnd();
                        }

                        if (toolCalls is { Count: > 0 })
                        {
                            // HARD CAP: If the model outputs multiple tool calls in one response,
                            // only execute the first one. This forces it to wait for the result
                            // and prevents blind batch-reading.
                            // FIX: this MUST happen before the history message below is built.
                            // ChatMessage.ToolCalls is init-only — if the untruncated list is stored
                            // first and this variable is rebound afterward, the assistant turn keeps
                            // declaring tool_calls that never get a matching "tool" role result. That
                            // leaves dangling tool_call_ids in history for the rest of the session,
                            // which strict OpenAI-compatible backends reject outright.
                            if (toolCalls.Count > 1)
                            {
                                Log($"Agentic: Model output {toolCalls.Count} tool calls. Executing only the first one.");
                                toolCalls = new List<ToolCall> { toolCalls[0] };
                            }

                            historyForApi.Add(new ChatMessage
                            {
                                Role = "assistant",
                                Content = displayContent,
                                ToolCalls = toolCalls
                            });

                            bool wasAskUserCall = false;

                            foreach (var tc in toolCalls)
                            {
                                if (string.IsNullOrWhiteSpace(tc.Id))
                                    tc.Id = "call_" + Guid.NewGuid().ToString("N")[..12];

                                var fnName = tc.Function?.Name ?? "?";
                                var argsJson = tc.Function?.Arguments ?? "{}";
                                var displayName = AgenticWorkflow.FormatToolCallDisplay(fnName, argsJson);
                                var isMono = AgenticWorkflow.IsMonospaceTool(fnName);
                                toolCallsThisTurn.TryGetValue(fnName, out var prevCount);
                                toolCallsThisTurn[fnName] = prevCount + 1;
                                Log($"Agentic: {fnName}({argsJson.Truncate(80)})");

                                string result = "";
                                bool isProgressTool = progressTools.Contains(fnName);
                                bool isNeutralTool = neutralTools.Contains(fnName);
                                bool stallLockoutActive = !isProgressTool && !isNeutralTool && callsSinceLastWrite >= StallLockoutThreshold;

                                if (!isProgressTool && !isNeutralTool && callsSinceLastWrite == StallNudgeThreshold && !stallNudgeSent)
                                {
                                    stallNudgeSent = true;
                                    historyForApi.Add(new ChatMessage
                                    {
                                        Role = AgentNudgeRole,
                                        Content = TagIfNudgeIsUserRole(
                                            $"You've made {StallNudgeThreshold} tool calls in a row without writing any file changes. " +
                                            "If you already have enough information to make the fix, stop exploring and call write_file now. " +
                                            $"After {StallLockoutThreshold} calls without a write, further exploration will be blocked entirely. " +
                                            "If you feel lost, call get_notes() to re-read your notes.")
                                    });
                                }

                                if (stallLockoutActive)
                                {
                                    result = $"BLOCKED: {callsSinceLastWrite} tool calls in a row without a successful write_file/delete_file/" +
                                             "move_file/rename_file/copy_file. No more exploration is allowed until you make an actual change. " +
                                             "Call get_notes() to re-read your notes, then write_file (or the appropriate file-modifying tool) now with what you already know.";
                                    Log($"Agentic: stall lockout — rejected {fnName} after {callsSinceLastWrite} non-progress calls.");
                                }
                                else if (!hasDeclaredIntent && (fnName == "write_file" || fnName == "run_command"))
                                {
                                    result = "BLOCKED: No update_notes(intent=...) call yet this task. Before your " +
                                             "first write_file or run_command, call update_notes once to record intent " +
                                             "(\"what you're doing and why\"), and once you've explored, declare your work " +
                                             "checklist with update_notes(todo_add=\"one item per line\"). " +
                                             "Then retry this exact call.";
                                    Log($"Agentic: intent gate — rejected {fnName} before update_notes was called this task.");
                                }
                                else
                                    if (fnName == "read_file")
                                    {
                                        var rawReadPath = AgenticWorkflow.GetPathFromCall(fnName, argsJson) ?? "";
                                        var readPath = NormalizePathForTracking(rawReadPath, activeProjectPath);
                                        bool isRangedRead = AgenticWorkflow.HasLineRangeArgs(argsJson);
                                        var readPathKey = string.IsNullOrEmpty(readPath) ? rawReadPath : readPath;

                                        readFileCount++;

                                        if (readFileCount > ReadFileHardStopThreshold)
                                        {
                                            result = $"BLOCKED: This is read_file call #{readFileCount} ({(isRangedRead ? "ranged" : "full")}, on '{readPathKey}') " +
                                                     "without a successful write in between. You've read enough — whether full files or line ranges, more " +
                                                     "reading will not tell you anything new. Your NEXT tool call MUST be write_file.";
                                            Log($"Agentic: read_file hard stop — rejected {fnName}('{readPathKey}') as call #{readFileCount} since last write.");
                                        }
                                        else
                                        {
                                            if (readFileCount > ReadFileNudgeThreshold && !readFileNudgeSent)
                                            {
                                                readFileNudgeSent = true;
                                                historyForApi.Add(new ChatMessage
                                                {
                                                    Role = AgentNudgeRole,
                                                    Content = TagIfNudgeIsUserRole(
                                                        $"You've made {readFileCount} read_file calls without writing any file changes " +
                                                        $"(hard limit is {ReadFileHardStopThreshold}). If you have enough context to make the fix, stop reading and call write_file now.")
                                                });
                                            }

                                            result = await Task.Run(() => AgenticWorkflow.ExecuteToolCall(tc, activeProjectPath, agenticSession));
                                        }

                                        if (!result.StartsWith("BLOCKED:") && !result.StartsWith("ERROR:") && !result.StartsWith("PATH_NOT_DISCOVERED:")
                                            && !result.StartsWith("REDUNDANT_READ_BLOCKED:")
                                            && !string.IsNullOrEmpty(readPath) && !filesReadThisTurn.Contains(readPath, StringComparer.OrdinalIgnoreCase))
                                        {
                                            filesReadThisTurn.Add(readPath);
                                        }

                                        bool isFailure = AgenticWorkflow.IsToolFailure(result);

                                        if (isFailure && !string.IsNullOrEmpty(readPath))
                                        {
                                            redundantReadCount.TryGetValue(readPath, out var count);
                                            count++;
                                            redundantReadCount[readPath] = count;

                                            if (count >= 4)
                                            {
                                                // Previously this called RecordWrite(readPath) here, which wipes the
                                                // ledger's recorded COVERAGE for the path, not just its escalation
                                                // counters — but nothing was actually written. That let a model that
                                                // ignores the nudge below simply read_file the same untouched file
                                                // again next turn: CheckRead would see zero prior coverage, allow the
                                                // full content through again, and the redundant-read counter had
                                                // already been reset to 0 too — so the entire block→nudge→block cycle
                                                // could restart indefinitely without a single write ever happening.
                                                // ResetBlockCounters only clears the escalation counters (so the
                                                // model isn't permanently deadlocked on this path if it genuinely
                                                // needs a different, non-overlapping range later) while leaving
                                                // coverage intact, so a same-range re-read is still correctly blocked.
                                                agenticSession?.ReadLedger.ResetBlockCounters(readPath);
                                                redundantReadCount.Remove(readPath);
                                                redundantReadNudgeSent.Remove(readPath);
                                                historyForApi.Add(new ChatMessage
                                                {
                                                    Role = AgentNudgeRole,
                                                    Content = TagIfNudgeIsUserRole(
                                                        $"STOP. You have tried to read '{readPath}' {count} times without writing a single change to it. " +
                                                        "You already have this file's content from your earlier reads in this conversation — reading it again, " +
                                                        "even with an overlapping line range, will not reveal anything new. " +
                                                        "Your NEXT tool call MUST be write_file for this exact path with the complete corrected file content. " +
                                                        "Do not call read_file, list_directory, search_files, or analyze_method again this turn.")
                                                });
                                            }
                                            else if (count >= 2)
                                            {
                                                redundantReadNudgeSent.TryGetValue(readPath, out var nudgeSentAt);
                                                if (nudgeSentAt < count)
                                                {
                                                    redundantReadNudgeSent[readPath] = count;
                                                    historyForApi.Add(new ChatMessage
                                                    {
                                                        Role = AgentNudgeRole,
                                                        Content = TagIfNudgeIsUserRole(
                                                            $"read_file on '{readPath}' has failed {count} times in a row — you have enough context to apply fixes. " +
                                                            "Stop requesting new line ranges of the same file and use write_file directly with your fix. " +
                                                            "If you're chasing a specific method's exact bounds, use analyze_method once to get the precise line range.")
                                                    });
                                                }
                                            }
                                        }
                                    }
                                    else if (fnName == "websearch")
                                    {
                                        var query = AgenticWorkflow.GetArgFromCall(argsJson, "query");
                                        if (string.IsNullOrWhiteSpace(query))
                                            result = "ERROR: 'query' argument is required for websearch.";
                                        else
                                        {
                                            try
                                            {
                                                var webResults = await Task.Run(async () =>
                                                    await _koboldClient.WebSearchAsync(query, _cts.Token));
                                                result = webResults.Count > 0
                                                    ? string.Join("\n\n", webResults.Select(r =>
                                                        $"Title: {r.Title}\nURL: {r.Url}\nDescription: {r.Description}\nContent: {r.Content.Truncate(2000)}"))
                                                    : "No web search results found.";
                                            }
                                            catch (Exception ex) { result = $"ERROR: Web search failed: {ex.Message}"; }
                                        }
                                    }
                                    else if (fnName == "ask_user")
                                    {
                                        result = await HandleAskUserAsync(tc, agentChatMsg, confirmManual);
                                        wasAskUserCall = true;
                                    }
                                    else
                                    {
                                        result = await Task.Run(() => AgenticWorkflow.ExecuteToolCall(tc, activeProjectPath, agenticSession));
                                    }

                                if (result.Length > 8000)
                                    result = result[..8000] + $"\n... (truncated, full: {result.Length} chars)";
                                Log($"Agentic: {fnName} -> {result[..Math.Min(result.Length, 120)]}");

                                if (fnName == "run_command" && agenticSession != null && !result.StartsWith("ERROR:"))
                                {
                                    var cmdPaths = ExtractPathsFromText(result);
                                    foreach (var cp in cmdPaths)
                                    {
                                        var normalized = NormalizePathForTracking(cp, activeProjectPath);
                                        if (!string.IsNullOrWhiteSpace(normalized))
                                            agenticSession.DiscoveredPaths.Add(normalized);
                                    }
                                }

                                if (fnName is "list_directory" or "search_files" or "write_file" && agenticSession != null && !result.StartsWith("ERROR:") && !result.StartsWith("SKIPPED:") && !result.StartsWith("BLOCKED:"))
                                {
                                    var pathsToSeed = ExtractPathsFromText(result);

                                    if (fnName == "write_file")
                                    {
                                        var writtenPath = AgenticWorkflow.GetPathFromCall(fnName, argsJson);
                                        if (!string.IsNullOrWhiteSpace(writtenPath))
                                            pathsToSeed.Add(writtenPath);
                                    }

                                    foreach (var p in pathsToSeed)
                                    {
                                        var normalized = p.Replace('\\', '/');
                                        if (!string.IsNullOrWhiteSpace(activeProjectPath) && normalized.Contains(activeProjectPath.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase))
                                        {
                                            var idx = normalized.IndexOf(activeProjectPath.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase);
                                            var suffix = normalized[(idx + activeProjectPath.Length)..].TrimStart('/');
                                            if (!string.IsNullOrWhiteSpace(suffix))
                                                normalized = suffix;
                                        }
                                        normalized = normalized.TrimStart('.', '/');
                                        if (!string.IsNullOrWhiteSpace(normalized))
                                            agenticSession.DiscoveredPaths.Add(normalized);
                                    }
                                }

                                // ── Intent gate: one successful update_notes satisfies it for the rest of the task ──
                                if (fnName == "update_notes" && !result.StartsWith("ERROR:") && !result.StartsWith("BLOCKED:"))
                                    hasDeclaredIntent = true;

                                // ── Enrich search_files to encourage targeted reads ──
                                if (fnName == "search_files" && !result.StartsWith("ERROR:"))
                                {
                                    if (string.IsNullOrWhiteSpace(result))
                                        result = "No matches found.";

                                    result += "\n\n[SYSTEM NOTE:\n- Use these results to narrow down your read_file calls. When reading, specify startLine and endLine to read only the relevant sections.]";
                                }

                                if (fnName == "write_file" && !result.StartsWith("ERROR:") && !result.StartsWith("SKIPPED:") && !result.StartsWith("BLOCKED:"))
                                {
                                    var rawChangedPath = AgenticWorkflow.GetPathFromCall(fnName, argsJson);
                                    var changedPath = rawChangedPath.Replace('\\', '/').TrimStart('.', '/');
                                    if (!string.IsNullOrEmpty(changedPath) && !filesChangedThisTurn.Contains(changedPath, StringComparer.OrdinalIgnoreCase))
                                    {
                                        filesChangedThisTurn.Add(changedPath);
                                    }
                                }

                                if (fnName == "write_file" && !result.StartsWith("ERROR:") && !result.StartsWith("SKIPPED:") && !result.StartsWith("BLOCKED:"))
                                {
                                    wroteFilesSinceBuild = true;
                                    wroteAnyFileThisTurn = true;
                                    nudgedForBuild = false;
                                }
                                else if (fnName == "run_command")
                                    wroteFilesSinceBuild = false;

                                // Stall counter update — generalized across every tool, not just read_file.
                                // A successful write/delete/move/rename/copy is real progress and resets the
                                // clock; anything else (including a blocked/failed call) just ticks it forward.
                                // readFileCount (the unified full+ranged read_file counter) resets here too —
                                // global, same as callsSinceLastWrite: ANY successful write clears it, not
                                // just a write to the specific file that was being re-read.
                                if (isProgressTool && !result.StartsWith("ERROR:") && !result.StartsWith("SKIPPED:") && !result.StartsWith("BLOCKED:"))
                                {
                                    callsSinceLastWrite = 0;
                                    stallNudgeSent = false;
                                    readFileCount = 0;
                                    readFileNudgeSent = false;
                                }
                                else if (!stallLockoutActive && fnName != "update_notes" && fnName != "get_notes")
                                {
                                    callsSinceLastWrite++;
                                }

                                if (!IsSuppressedImageGenFailure(fnName, result))
                                    AppendToolResult(agentChatMsg, fnName, displayName, result, isMono, argsJson, activeProjectPath);
                                SetAgentImage(agentChatMsg, fnName, result, activeProjectPath);
                                SetAgentAttachment(agentChatMsg, fnName, result, activeProjectPath);
                                ScrollChatToEnd();
                                FlushAgentChatRender();

                                if (result.StartsWith("ERROR:"))
                                    result += "\n\nGuidance: If the project folder is not set, set it in the Sessions tab. If a file was not found, check the path. If an argument is missing, correct your call.";
                                else if (result.StartsWith("SKIPPED:"))
                                    result += "\n\nGuidance: write_file needs the file content either inline as `\"content\": \"...\"` in the JSON, "
                                        + "or — for large files — as a fenced ```code block``` immediately after the JSON call (path-only JSON). "
                                        + "Re-send write_file for this file using one of those two forms; do not call it again with an empty content.";

                                historyForApi.Add(new ChatMessage
                                {
                                    Role = "tool",
                                    ToolCallId = tc.Id,
                                    Content = result
                                });

                                await Dispatcher.Yield(DispatcherPriority.Background);
                            }

                            if (string.IsNullOrWhiteSpace(displayContent) && toolCallsThisTurn.Count > 0 && !wasAskUserCall)
                            {
                                historyForApi.Add(new ChatMessage
                                {
                                    Role = AgentNudgeRole,
                                    Content = TagIfNudgeIsUserRole("You made tool calls above. Now provide a summary of what was accomplished. If the work is complete, reply with NO_CHANGES_NEEDED or describe the changes made.")
                                });
                            }
                            if (!string.IsNullOrWhiteSpace(displayContent))
                            {
                                finalContent = displayContent;
                            }

                            if (iter >= maxIterations - 1 && maxIterations < 100)
                            {
                                maxIterations++;
                                Log($"Agentic: extended iteration budget to {maxIterations}");
                            }

                            continue;
                        }

                        if (!string.IsNullOrWhiteSpace(msg.Content) && IsDegenerateRepetition(msg.Content))
                        {
                            Log($"Agentic: degenerate repetition detected and discarded ({msg.Content.Length} chars, sample: {msg.Content.Truncate(60)})");
                            historyForApi.Add(new ChatMessage { Role = AgentNudgeRole, Content = TagIfNudgeIsUserRole(DegenerateRepetitionMessage) });
                            agentChatMsg.AppendContent("\n\n[Degenerate repetition detected — response discarded, retrying]");
                            continue;
                        }

                        if (!string.IsNullOrWhiteSpace(msg.Content) && IsAssistantLoop(historyForApi, msg.Content))
                        {
                            historyForApi.Add(new ChatMessage { Role = AgentNudgeRole, Content = TagIfNudgeIsUserRole(LoopBreakMessage) });
                            agentChatMsg.AppendContent("\n\n[Stuck in loop — injected break instruction]");
                            continue;
                        }

                        if (string.IsNullOrWhiteSpace(msg.Content) && toolCalls is not { Count: > 0 })
                        {
                            emptyResponseNudges++;
                            if (emptyResponseNudges >= 2)
                            {
                                Log("Agentic: empty response threshold reached — breaking to fallback summary");
                                break;
                            }
                            var nudgeText = toolCallsThisTurn.Count > 0
                                ? "You already called tools this turn. Provide a summary of what was accomplished. Reply with NO_CHANGES_NEEDED only if the work is done, or call another tool if more work is needed."
                                : "You returned an empty response. If you are finished, reply with NO_CHANGES_NEEDED or provide a summary. If you need to do more work considering write_file or call other tool you need now.";
                            historyForApi.Add(new ChatMessage
                            {
                                Role = AgentNudgeRole,
                                Content = TagIfNudgeIsUserRole(nudgeText)
                            });
                            agentChatMsg.AppendContent("\n\n[Empty response received — nudging model to finish]");
                            continue;
                        }

                        if (!string.IsNullOrWhiteSpace(msg.Content) && LooksLikeAskingForConfirmation(msg.Content)
                            && confirmationNudges < maxConfirmationNudges)
                        {
                            confirmationNudges++;
                            var nudgeText = confirmManual
                                ? "You asked the user a question in plain text. If a decision genuinely needs their input, ask via the ask_user tool instead (one question, 2-4 concrete options) — that renders clickable answers. Otherwise proceed autonomously with a tool call."
                                : "User WILL NOT answer confirmation questions, do your own reasoning based on your progress so far OR Proceed immediately with a tool call.";
                            historyForApi.Add(new ChatMessage
                            {
                                Role = AgentNudgeRole,
                                Content = TagIfNudgeIsUserRole(nudgeText)
                            });
                            agentChatMsg.AppendContent(confirmManual
                                ? "\n\n[Confirmation question in text — prompted to use ask_user]"
                                : "\n\n[Asked for confirmation instead of acting — forcing it to proceed]");
                            continue;
                        }

                        // NEW
                        if (wroteFilesSinceBuild)
                        {
                            if (buildNudgeCount < maxBuildNudges)
                            {
                                buildNudgeCount++;
                                nudgedForBuild = true;
                                historyForApi.Add(new ChatMessage
                                {
                                    Role = AgentNudgeRole,
                                    Content = TagIfNudgeIsUserRole($"You wrote files but haven't verified with a build since the last write (reminder {buildNudgeCount}/{maxBuildNudges}). Call run_command to build/compile now — do not summarize until the build has been re-checked.")
                                });
                                agentChatMsg.AppendContent($"\n\n[Skipped build after writing files — forcing a build check ({buildNudgeCount}/{maxBuildNudges})]");
                                continue;
                            }

                            // Model refused to build after repeated reminders — don't let it claim success unverified.
                            finalContent = (msg.Content ?? "") + "\n\n*(Note: files were changed but the agent never re-verified the build — run it manually before trusting this.)*";
                            break;
                        }

                        if (!wroteAnyFileThisTurn && !string.IsNullOrWhiteSpace(msg.Content)
                            && SaysNoChangesNeeded(msg.Content)
                            && noChangesContradictionNudges < maxNoChangesContradictionNudges)
                        {
                            noChangesContradictionNudges++;
                            statedExplicitPlan = false;
                            var mentionedFiles = ExtractMentionedFileNames(msg.Content);
                            var fileHint = mentionedFiles.Count > 0
                                ? $" You already referenced {string.Join(", ", mentionedFiles.Select(f => $"`{f}`"))} above."
                                : "";
                            historyForApi.Add(new ChatMessage
                            {
                                Role = AgentNudgeRole,
                                Content = TagIfNudgeIsUserRole("You said NO_CHANGES_NEEDED but earlier you mentioned specific files and changes."
                                    + fileHint
                                    + " If you genuinely believe no changes are needed, respond with NO_CHANGES_NEEDED again and it will be accepted. "
                                    + "Otherwise, call write_file to apply the changes.")
                            });
                            agentChatMsg.AppendContent("\n\n[NO_CHANGES_NEEDED noted — will accept if confirmed]");
                            continue;
                        }

                        if (!wroteAnyFileThisTurn && !string.IsNullOrWhiteSpace(msg.Content)
                            && !SaysNoChangesNeeded(msg.Content))
                        {
                            if (msg.Content.Contains("## Summary", StringComparison.OrdinalIgnoreCase))
                            {
                                if (wroteFilesSinceBuild)
                                {
                                    historyForApi.Add(new ChatMessage
                                    {
                                        Role = AgentNudgeRole,
                                        Content = TagIfNudgeIsUserRole("You wrote files but haven't verified with a build since your last write. Call run_command to build before summarizing — a summary is not accepted until the build has been re-checked.")
                                    });
                                    agentChatMsg.AppendContent("\n\n[Summary rejected — build not verified since last write]");
                                    continue;
                                }
                                finalContent = msg.Content;
                                break;
                            }

                            var extracted = AgenticWorkflow.ExtractCodeBlocksFromText(msg.Content);
                            if (extracted.Count > 0)
                            {
                                // HARD CAP: same policy as the real tool_calls path above — only
                                // execute the first one. Also: build the ToolCall list and declare
                                // it in a preceding "assistant" history message BEFORE executing
                                // anything, so the "tool" result messages appended below always have
                                // a matching declaration. Previously this loop appended "tool" role
                                // messages with no assistant message ever declaring their tool_call_ids
                                // — a guaranteed dangling-tool_call_id on every use of this fallback,
                                // for strict OpenAI-compatible backends.
                                if (extracted.Count > 1)
                                {
                                    Log($"Agentic: Model embedded {extracted.Count} tool calls in text. Executing only the first one.");
                                    extracted = extracted.Take(1).ToList();
                                }

                                var extractedToolCalls = extracted.Select(e => new ToolCall
                                {
                                    Id = "call_extracted_" + Guid.NewGuid().ToString("N")[..12],
                                    Type = "function",
                                    Function = new ToolCallFunction { Name = e.FunctionName, Arguments = e.ArgumentsJson }
                                }).ToList();

                                historyForApi.Add(new ChatMessage
                                {
                                    Role = "assistant",
                                    Content = msg.Content ?? "",
                                    ToolCalls = extractedToolCalls
                                });

                                foreach (var tc in extractedToolCalls)
                                {
                                    var fnName = tc.Function.Name;
                                    var argsJson = tc.Function.Arguments;
                                    var displayName = AgenticWorkflow.FormatToolCallDisplay(fnName, argsJson);
                                    var isMono = AgenticWorkflow.IsMonospaceTool(fnName);
                                    Log($"Agentic: {fnName}(extracted from code block)");

                                    // This fallback handles a model that embedded a tool call as JSON in
                                    // plain text instead of a real tool_calls entry. Without the same
                                    // readFileCount gate as the main tool-call path below, this was a
                                    // clean bypass: a model could keep "reading" indefinitely just by
                                    // putting read_file JSON in its message body instead of a proper call,
                                    // since this branch previously went straight to ExecuteToolCall with
                                    // no counting, no nudge, and no hard stop at all.
                                    string result;
                                    if (!hasDeclaredIntent && (fnName == "write_file" || fnName == "run_command"))
                                    {
                                        result = "BLOCKED: No update_notes(intent=...) call yet this task. Before your " +
                                                 "first write_file or run_command, call update_notes once to record intent " +
                                                 "(\"what you're doing and why\"), and once you've explored, declare your work " +
                                                 "checklist with update_notes(todo_add=\"one item per line\"). " +
                                                 "Then retry this exact call.";
                                        Log($"Agentic: intent gate — rejected {fnName} (extracted from code block) before update_notes was called this task.");
                                    }
                                    else if (fnName == "read_file")
                                    {
                                        readFileCount++;
                                        if (readFileCount > ReadFileHardStopThreshold)
                                        {
                                            result = $"BLOCKED: This is read_file call #{readFileCount} without a successful write in between. " +
                                                     "You've read enough — more reading will not tell you anything new. Your NEXT tool call MUST be write_file.";
                                            Log($"Agentic: read_file hard stop (extracted call) — rejected as call #{readFileCount} since last write.");
                                        }
                                        else
                                        {
                                            if (readFileCount > ReadFileNudgeThreshold && !readFileNudgeSent)
                                            {
                                                readFileNudgeSent = true;
                                                historyForApi.Add(new ChatMessage
                                                {
                                                    Role = AgentNudgeRole,
                                                    Content = TagIfNudgeIsUserRole(
                                                        $"You've made {readFileCount} read_file calls without writing any file changes " +
                                                        $"(hard limit is {ReadFileHardStopThreshold}). If you have enough context to make the fix, stop reading and call write_file now.")
                                                });
                                            }
                                            result = await Task.Run(() => AgenticWorkflow.ExecuteToolCall(tc, activeProjectPath, agenticSession));
                                        }
                                    }
                                    else if (fnName == "ask_user")
                                    {
                                        result = await HandleAskUserAsync(tc, agentChatMsg, confirmManual);
                                    }
                                    else
                                    {
                                        result = await Task.Run(() => AgenticWorkflow.ExecuteToolCall(tc, activeProjectPath, agenticSession));
                                    }
                                    if (result.Length > 8000)
                                        result = result[..8000] + $"\n... (truncated, full: {result.Length} chars)";
                                    Log($"Agentic: {fnName} -> {result[..Math.Min(result.Length, 120)]}");

                                    if (fnName == "update_notes" && !result.StartsWith("ERROR:") && !result.StartsWith("BLOCKED:"))
                                        hasDeclaredIntent = true;

                                    if (fnName == "write_file" && !result.StartsWith("ERROR:") && !result.StartsWith("SKIPPED:") && !result.StartsWith("BLOCKED:"))
                                    {
                                        var rawChangedPath = AgenticWorkflow.GetPathFromCall(fnName, argsJson) ?? "";
                                        var changedPath = NormalizePathForTracking(rawChangedPath, activeProjectPath);

                                        if (!string.IsNullOrEmpty(changedPath) && !filesChangedThisTurn.Contains(changedPath, StringComparer.OrdinalIgnoreCase))
                                            filesChangedThisTurn.Add(changedPath);

                                        wroteFilesSinceBuild = true;
                                        wroteAnyFileThisTurn = true;
                                        nudgedForBuild = false;

                                        // Same reset as the main tool-call path: any successful write clears
                                        // the read_file budget, since it's global rather than per-file.
                                        readFileCount = 0;
                                        readFileNudgeSent = false;
                                        callsSinceLastWrite = 0;
                                        stallNudgeSent = false;
                                    }
                                    else if (fnName == "read_file" && !result.StartsWith("ERROR:") && !result.StartsWith("PATH_NOT_DISCOVERED:") && !result.StartsWith("BLOCKED:"))
                                    {

                                        var rawReadPath = AgenticWorkflow.GetPathFromCall(fnName, argsJson) ?? "";
                                        var readPath = NormalizePathForTracking(rawReadPath, activeProjectPath);

                                        readPath = readPath.TrimStart('.', '/');

                                        if (!string.IsNullOrEmpty(readPath) && !filesReadThisTurn.Contains(readPath, StringComparer.OrdinalIgnoreCase))
                                            filesReadThisTurn.Add(readPath);
                                    }
                                    else if (fnName == "run_command")
                                        wroteFilesSinceBuild = false;

                                    if (!IsSuppressedImageGenFailure(fnName, result))
                                        AppendToolResult(agentChatMsg, fnName, displayName, result, isMono, argsJson, activeProjectPath);
                                    SetAgentImage(agentChatMsg, fnName, result, activeProjectPath);
                                    SetAgentAttachment(agentChatMsg, fnName, result, activeProjectPath);
                                    ScrollChatToEnd();

                                    historyForApi.Add(new ChatMessage
                                    {
                                        Role = "tool",
                                        ToolCallId = tc.Id,
                                        Content = result
                                    });
                                }
                            }
                            else
                            {
                                nudgedForCodeInChatCount++;
                                var lastText = (msg.Content ?? "").Truncate(120);
                                var nudgeContent = nudgedForCodeInChatCount switch
                                {
                                    1 => "STOP repeating yourself. Your last response was: \"" + lastText + "\". Output a DIFFERENT response: a JSON tool call. Example: {\"function\": \"list_directory\", \"arguments\": {\"path\": \".\"}}",
                                    2 => "AGAIN: Your last response was: \"" + lastText + "\". Do NOT repeat that. You MUST output one of: {\"function\": \"list_directory\", \"arguments\": {\"path\": \".\"}} or {\"function\": \"read_file\", \"arguments\": {\"path\": \"...\"}} or {\"function\": \"search_files\", \"arguments\": {\"pattern\": \"...\", \"path\": \".\"}}",
                                    3 => "FINAL WARNING — You repeated yourself " + nudgedForCodeInChatCount + " times. Output {\"function\": \"list_directory\", \"arguments\": {\"path\": \".\"}} NOW or reply NO_CHANGES_NEEDED.",
                                    _ => "Output {\"function\": \"list_directory\", \"arguments\": {\"path\": \".\"}} or NO_CHANGES_NEEDED."
                                };
                                historyForApi.Add(new ChatMessage { Role = AgentNudgeRole, Content = TagIfNudgeIsUserRole(nudgeContent) });
                                agentChatMsg.AppendContent($"\n\n[No tool calls — nudge {nudgedForCodeInChatCount}]");
                            }
                            continue;
                        }

                        // Before accepting "done", re-prompt the recorded notes + todo checklist so the agent
                        // can verify it completed everything before quitting. Escalates: with open items, the
                        // FIRST claim of completion re-prompts the notes; a SECOND claim is a HARD BLOCK — the
                        // agent cannot finish while its own checklist is incomplete. With zero open items the
                        // completion is accepted immediately.
                        if (SaysNoChangesNeeded(msg.Content) && _activeSession != null && (!string.IsNullOrWhiteSpace(_activeSession.UserIntent) || _activeSession.TodoList.Count > 0 || _activeSession.Notes.Count > 0))
                        {
                            var pendingCount = _activeSession.TodoList.Count(t => t.Status == AgentSession.TodoStatus.Pending);
                            if (pendingCount > 0)
                            {
                                if (completionReprompts >= 1)
                                {
                                    var openList = string.Join("\n", _activeSession.TodoList.Where(t => t.Status == AgentSession.TodoStatus.Pending).Select(t => $"  - #{t.Id} {t.Text}"));
                                    historyForApi.Add(new ChatMessage
                                    {
                                        Role = AgentNudgeRole,
                                        Content = TagIfNudgeIsUserRole($"HARD BLOCK: you cannot finish the task. {pendingCount} TODO item(s) are still OPEN and you claimed completion twice without closing them:\n{openList}\n\nDo the work, close each item with update_notes(todo_complete=\"<id>\") — closures are verified against real write_file/delete_file/move_file/copy_file calls — then run the final build and report the actual result.")
                                    });
                                    agentChatMsg.AppendContent("\n\n[Completion blocked — open TODOs remain]");
                                    continue;
                                }
                                completionReprompts++;
                                var noteBlock = $"## Your notes\n\n{AgenticWorkflow.RenderNotesBlock(_activeSession)}\n\nReview this before marking the task as done. If all items are completed, respond NO_CHANGES_NEEDED again. If not, continue working.";
                                historyForApi.Add(new ChatMessage
                                {
                                    Role = AgentNudgeRole,
                                    Content = TagIfNudgeIsUserRole(noteBlock)
                                });
                                agentChatMsg.AppendContent("\n\n[Re-prompting your notes before accepting completion]");
                                continue;
                            }
                        }

                        finalContent = msg.Content;
                        break;
                    }

                    if (filesChangedThisTurn.Count > 0 && finalContent != null)
                    {
                        var summary = "\n\n---\n**Summary of changes:**\n";
                        summary += "\n**Files changed:**\n";
                        foreach (var f in filesChangedThisTurn.Distinct())
                            summary += $"- `{f}`\n";
                        summary += $"\n**What was done:** {finalContent.Truncate(500)}\n";
                        agentChatMsg.AppendContent(summary);
                        ScrollChatToEnd();
                    }

                    if (finalContent == null)
                    {
                        var summary = filesChangedThisTurn.Count > 0
                            ? $"\n\n*(Agent reached iteration limit. Changes applied:\n{string.Join(", ", filesChangedThisTurn.Distinct().Select(f => $"`{f}`"))})*"
                            : "\n\n*(Agent reached iteration limit without producing a final response.)*";
                        finalContent = summary;
                        agentChatMsg.AppendContent(summary);
                        ScrollChatToEnd();
                    }

                    reply = finalContent ?? "";
                    _statusLabel.Content = "Ready";
                }
                else
                {
                    var loopRetries = 0;
                    const int maxLoopRetries = 3;

                    for (; ; )
                    {
                        _chatHistory.Add(new ChatMessage { Role = "assistant", Content = "" });
                        ScrollChatToEnd();

                        var historyForApi = _chatHistory.Take(_chatHistory.Count - 1).ToList();
                        if (!string.IsNullOrWhiteSpace(_settings.TextSystemPrompt))
                            historyForApi.Insert(0, new ChatMessage { Role = "system", Content = _settings.TextSystemPrompt });
                        historyForApi.Insert(0, new ChatMessage { Role = "system", Content = "You have access to a web search tool. When you need current or real-time information to answer the user's question, include WEBSEARCH: <query> in your response and the search will be performed automatically." });
                        historyForApi.Insert(0, new ChatMessage
                        {
                            Role = "system",
                            Content = "When the user asks for a downloadable file (e.g. a script, document, or data file) rather than an inline answer, "
                                + "write it as a fenced code block with an explicit filename, like: ```python filename=\"script.py\"\ncode here\n``` "
                                + "— this works for any file type, not just code. The app detects this pattern and offers the file for download; "
                                + "plain code blocks without a filename are only shown as chat text, not offered as a file."
                        });
                        _tpsTimestamp = Stopwatch.GetTimestamp();
                        var streamedChars = 0;

                        reply = await Task.Run(async () =>
                            await SendChatStreamViaBackendAsync(historyForApi, (content, reasoning, isDone) =>
                            {
                                if (isDone) return;
                                var append = content ?? reasoning ?? "";
                                if (string.IsNullOrEmpty(append)) return;

                                streamedChars += append.Length;
                                var secs = Stopwatch.GetElapsedTime(_tpsTimestamp).TotalSeconds;
                                var tps = secs > 0 ? streamedChars / 4.0 / secs : 0.0;

                                Dispatcher.BeginInvoke(() =>
                                {
                                    _chatHistory[^1].AppendContent(append);
                                    ScrollChatToEnd();
                                    if (secs > 0)
                                        _tpsLabel.Content = $"{tps:F1} t/s";
                                });
                            }, reasoningEffort, maxTokens,
                            _settings.TextTemperature, _settings.TextTopP, _settings.TextTopK, _settings.TextRepeatPenalty,
                            ct: _cts.Token));

                        var elapsed = Stopwatch.GetElapsedTime(_tpsTimestamp).TotalSeconds;
                        if (elapsed > 0 && !string.IsNullOrWhiteSpace(reply) && !reply.StartsWith("(empty"))
                        {
                            var approxTokens = reply.Length / 4;
                            _tpsLabel.Content = $"{approxTokens / elapsed:F1} t/s";
                        }

                        if (!string.IsNullOrWhiteSpace(reply))
                        {
                            var wsMatch = System.Text.RegularExpressions.Regex.Match(reply, @"WEBSEARCH:\s*(.+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                            if (wsMatch.Success)
                            {
                                var query = wsMatch.Groups[1].Value.Trim().TrimEnd('.', '!', '?');
                                _statusLabel.Content = "Searching web...";
                                try
                                {
                                    if (_koboldClient == null) { Log("Web search unavailable: koboldcpp not running in external mode"); continue; }

                                    var searchResults = await Task.Run(async () =>
                                        await _koboldClient.WebSearchAsync(query, _cts.Token));

                                    if (searchResults.Count > 0)
                                    {
                                        var resultsText = string.Join("\n\n", searchResults.Select(r =>
                                            $"[{r.Title}]({r.Url})\n{r.Description}\nContent: {r.Content.Truncate(2000)}"));
                                        historyForApi.Add(new ChatMessage { Role = AgentNudgeRole, Content = TagIfNudgeIsUserRole($"Web search results for \"{query}\":\n\n{resultsText}") });
                                        Log($"Web search done for: {query}");
                                        _chatHistory.RemoveAt(_chatHistory.Count - 1);
                                        continue;
                                    }
                                }
                                catch (Exception ex)
                                {
                                    Log($"Web search error: {ex.Message}");
                                }
                                _statusLabel.Content = "Thinking...";
                            }
                        }

                        if (!string.IsNullOrWhiteSpace(reply) && loopRetries < maxLoopRetries && IsDegenerateRepetition(reply))
                        {
                            loopRetries++;
                            Log($"Chat: degenerate repetition detected and discarded ({reply.Length} chars, sample: {reply.Truncate(60)})");
                            _chatHistory.RemoveAt(_chatHistory.Count - 1);
                            _chatHistory.Add(new ChatMessage { Role = AgentNudgeRole, Content = TagIfNudgeIsUserRole(DegenerateRepetitionMessage) });
                            continue;
                        }

                        if (!string.IsNullOrWhiteSpace(reply) && loopRetries < maxLoopRetries && IsAssistantLoop(_chatHistory, reply))
                        {
                            loopRetries++;
                            _chatHistory.RemoveAt(_chatHistory.Count - 1);
                            _chatHistory.Add(new ChatMessage { Role = AgentNudgeRole, Content = TagIfNudgeIsUserRole(LoopBreakMessage) });
                            continue;
                        }
                        break;
                    }
                }
            }

            while (_chatHistory.Count > _maxHistoryCount)
                _chatHistory.RemoveAt(0);

            var files = ParseFilesFromResponse(reply);
            if (files.Count > 0)
            {
                _detectedFiles.Clear();
                _detectedFiles.AddRange(files);
                Dispatcher.BeginInvoke(() => PopulateFilesPanel(_chatControl.FilesPanel, files));
            }
            else
            {
                Dispatcher.BeginInvoke(() => _chatControl.FilesPanel.Visibility = Visibility.Collapsed);
            }

            _statusLabel.Content = "Ready";
        }
        catch (Exception ex)
        {
            Log($"Chat error: {ex.Message}");
            _statusLabel.Content = "Error";
        }
        finally
        {
            if (_isGenerating)
            {
                _isGenerating = false;
                UpdateTabLockState();
                _textSendBtn.Content = "Send";
                _textSendBtn.Background = Accent;
            }
            _generateBtn.IsEnabled = true;
        }
    }
    private static string NormalizePathForTracking(string rawPath, string projectPath)
    {
        if (string.IsNullOrWhiteSpace(rawPath) || string.IsNullOrWhiteSpace(projectPath)) return "";
        // Delegate to the same normalizer AgenticWorkflow.ReadFile/WriteFile/ReadLedger use
        // internally, so a path recorded here as "discovered" or "read" is guaranteed to be
        // the exact same key the gates look it up under. This function used to be a separate,
        // string-only reimplementation that could disagree with the real one on edge cases
        // (redundant project-folder prefixes, "..", mixed separators) — see NormalizeRel's
        // doc comment for the history of why that was a real, previously-hit bug.
        return AgenticWorkflow.NormalizeRel(rawPath, projectPath);
    }
    private List<DetectedFile> ParseFilesFromResponse(string text)
    {
        var result = new List<DetectedFile>();
        if (string.IsNullOrWhiteSpace(text)) return result;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var codeBlockPattern = System.Text.RegularExpressions.Regex.Matches(text,
            @"```(\w+)\s+(?:filename|file)=[""']([^""']+)[""']\s*\n(.*?)```",
            System.Text.RegularExpressions.RegexOptions.Singleline);
        foreach (System.Text.RegularExpressions.Match? m in codeBlockPattern)
        {
            if (m == null || !m.Success) continue;
            var lang = m.Groups[1].Value;
            var filename = Path.GetFileName(m.Groups[2].Value);
            var content = m.Groups[3].Value.TrimEnd();
            if (!string.IsNullOrWhiteSpace(content) && seen.Add(filename))
                result.Add(new DetectedFile(filename, lang, content));
        }

        var markerPattern = System.Text.RegularExpressions.Regex.Matches(text,
            @"(?://|#|--)\s*file:\s*(\S+)\s*\n(```\w+\n)?(.*?)(?=\n(?://|#|--)\s*file:|```|$)",
            System.Text.RegularExpressions.RegexOptions.Singleline);
        foreach (System.Text.RegularExpressions.Match? m in markerPattern)
        {
            if (m == null || !m.Success) continue;
            var filename = Path.GetFileName(m.Groups[1].Value);
            var content = m.Groups[3].Value.TrimEnd();
            if (!string.IsNullOrWhiteSpace(content) && seen.Add(filename))
            {
                var ext = Path.GetExtension(filename).ToLowerInvariant();
                var lang = ext.TrimStart('.');
                result.Add(new DetectedFile(filename, lang, content));
            }
        }

        // Fallback: a plain fenced block with a language tag but no explicit filename/marker.
        // Only for languages/extensions that are almost always a standalone deliverable
        // (data, config, scripts) rather than a short illustrative snippet, and only when
        // it has enough lines to look like a real file rather than a one-liner example.
        var plainBlockPattern = System.Text.RegularExpressions.Regex.Matches(text,
            @"```(\w+)\s*\n(.*?)```",
            System.Text.RegularExpressions.RegexOptions.Singleline);
        var plainIndex = 0;
        foreach (System.Text.RegularExpressions.Match? m in plainBlockPattern)
        {
            if (m == null || !m.Success) continue;
            var lang = m.Groups[1].Value.ToLowerInvariant();
            if (!AttachableExtensions.TryGetValue(lang, out var ext)) continue;
            var content = m.Groups[2].Value.TrimEnd();
            if (content.Split('\n').Length < 3) continue; // too short to be a real deliverable

            plainIndex++;
            var filename = $"output_{plainIndex}{ext}";
            if (seen.Add(filename))
                result.Add(new DetectedFile(filename, lang, content));
        }

        return result;
    }

    private static readonly Dictionary<string, string> AttachableExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ["csv"] = ".csv",
        ["tsv"] = ".tsv",
        ["txt"] = ".txt",
        ["text"] = ".txt",
        ["json"] = ".json",
        ["yaml"] = ".yaml",
        ["yml"] = ".yaml",
        ["toml"] = ".toml",
        ["ini"] = ".ini",
        ["cfg"] = ".cfg",
        ["conf"] = ".conf",
        ["env"] = ".env",
        ["xml"] = ".xml",
        ["sql"] = ".sql",
        ["python"] = ".py",
        ["py"] = ".py",
        ["bash"] = ".sh",
        ["sh"] = ".sh",
        ["powershell"] = ".ps1",
        ["ps1"] = ".ps1",
        ["batch"] = ".bat",
        ["bat"] = ".bat",
        ["javascript"] = ".js",
        ["js"] = ".js",
        ["typescript"] = ".ts",
        ["ts"] = ".ts",
        ["csharp"] = ".cs",
        ["cs"] = ".cs",
        ["go"] = ".go",
        ["rust"] = ".rs",
        ["java"] = ".java",
        ["ruby"] = ".rb",
        ["php"] = ".php",
    };

    private void PopulateFilesPanel(StackPanel panel, List<DetectedFile> files)
    {
        panel.Children.Clear();
        if (files.Count == 0)
        {
            panel.Visibility = Visibility.Collapsed;
            return;
        }

        var header = new Label
        {
            Content = $"Detected files ({files.Count}):",
            Foreground = Fg,
            FontSize = 12,
            FontWeight = FontWeight.FromOpenTypeWeight(600),
            Padding = new Thickness(0, 2, 0, 2)
        };
        panel.Children.Add(header);

        foreach (var file in files)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 1, 0, 1) };
            var badge = new Border
            {
                Background = BrBlue,
                CornerRadius = new CornerRadius(0),
                Padding = new Thickness(4, 1, 4, 1),
                Margin = new Thickness(0, 0, 4, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock
                {
                    Text = file.Filename,
                    Foreground = Brushes.White,
                    FontSize = 11,
                    FontWeight = FontWeight.FromOpenTypeWeight(600)
                }
            };
            var saveBtn = new Button
            {
                Content = "Save",
                FontSize = 11,
                Cursor = Cursors.Hand,
                Padding = new Thickness(8, 1, 8, 1),
                Background = BrGreen,
                Foreground = Brushes.White,
                BorderThickness = new Thickness(0),
                Tag = file
            };
            saveBtn.Click += OnFileSaveClick;
            row.Children.Add(badge);
            row.Children.Add(saveBtn);
            panel.Children.Add(row);
        }

        panel.Visibility = Visibility.Visible;
    }

    private void OnFileSaveClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not DetectedFile file) return;
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            FileName = file.Filename,
            Filter = "All files (*.*)|*.*",
            Title = $"Save: {file.Filename}"
        };
        if (dlg.ShowDialog(this) == true)
        {
            try
            {
                File.WriteAllText(dlg.FileName, file.Content);
                Log($"Saved: {dlg.FileName}");
            }
            catch (Exception ex)
            {
                Log($"Save error: {ex.Message}");
            }
        }
    }

    private void OnAttachmentChipClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.DataContext is not AttachmentInfo att) return;
        if (string.IsNullOrEmpty(att.FullPath) || !File.Exists(att.FullPath))
        {
            Log($"Attachment missing on disk: {att.FileName}");
            return;
        }

        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            FileName = att.FileName,
            Filter = "All files (*.*)|*.*",
            Title = $"Save: {att.FileName}"
        };
        if (dlg.ShowDialog(this) == true)
        {
            try
            {
                File.Copy(att.FullPath, dlg.FileName, true);
                Log($"Saved: {dlg.FileName}");
            }
            catch (Exception ex)
            {
                Log($"Save error: {ex.Message}");
            }
        }
    }

    private void OnTextAttachClick(object sender, RoutedEventArgs e)
    {
        // Text mode: if no text MMProj is configured the model can't process images,
        // so exclude image types from the file dialog entirely.
        var hasMmproj = !string.IsNullOrWhiteSpace(_settings.TextMmprojPath);
        var dlg = new OpenFileDialog
        {
            Multiselect = true,
            Filter = hasMmproj
                ? "All supported|*.txt;*.md;*.cs;*.py;*.js;*.ts;*.json;*.xml;*.html;*.css;*.yaml;*.yml;*.ini;*.cfg;*.sh;*.bat;*.ps1;*.png;*.jpg;*.jpeg;*.webp;*.gif;*.bmp|Text files|*.txt;*.md;*.cs;*.py;*.js;*.ts;*.json;*.xml;*.html;*.css;*.yaml;*.yml;*.ini;*.cfg;*.sh;*.bat;*.ps1|Image files|*.png;*.jpg;*.jpeg;*.webp;*.gif;*.bmp"
                : "All supported|*.txt;*.md;*.cs;*.py;*.js;*.ts;*.json;*.xml;*.html;*.css;*.yaml;*.yml;*.ini;*.cfg;*.sh;*.bat;*.ps1|Text files|*.txt;*.md;*.cs;*.py;*.js;*.ts;*.json;*.xml;*.html;*.css;*.yaml;*.yml;*.ini;*.cfg;*.sh;*.bat;*.ps1"
        };
        if (dlg.ShowDialog() == true)
        {
            foreach (var f in dlg.FileNames)
                if (!_textAttachments.Contains(f))
                    _textAttachments.Add(f);
            RebuildTextAttachChips();
        }
    }

    private static string GetFileIcon(string ext) => ext.ToLowerInvariant() switch
    {
        ".png" or ".jpg" or ".jpeg" or ".webp" or ".gif" or ".bmp" or ".svg" => "\U0001F5BC",
        ".txt" or ".md" or ".log" => "\U0001F4DD",
        ".cs" or ".py" or ".js" or ".ts" or ".jsx" or ".tsx" or ".css" or ".html" or ".xml" or ".json" or ".yaml" or ".yml" or ".ini" or ".cfg" or ".sh" or ".bat" or ".ps1" or ".go" or ".rs" or ".java" or ".cpp" or ".c" or ".h" or ".hpp" or ".rb" or ".php" or ".swift" or ".kt" => "\U0001F4C4",
        ".zip" or ".rar" or ".7z" or ".tar" or ".gz" => "\U0001F4E6",
        ".mp3" or ".wav" or ".ogg" or ".flac" or ".aac" or ".m4a" or ".wma" => "\U0001F3B5",
        ".mp4" or ".avi" or ".mkv" or ".mov" or ".wmv" or ".flv" => "\U0001F3AC",
        ".pdf" => "\U0001F4D1",
        ".xls" or ".xlsx" or ".csv" => "\U0001F4CA",
        ".doc" or ".docx" => "\U0001F4CB",
        _ => "\U0001F4C4"
    };

    private void RebuildTextAttachChips()
    {
        _textAttachPanel.Children.Clear();
        foreach (var att in _textAttachments)
        {
            var name = Path.GetFileName(att);
            var chip = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(60, 60, 80)),
                CornerRadius = new CornerRadius(0),
                Margin = new Thickness(0, 0, 4, 2),
                Padding = new Thickness(6, 2, 6, 2)
            };
            var sp = new StackPanel { Orientation = Orientation.Horizontal };
            sp.Children.Add(new TextBlock
            {
                Text = name,
                Foreground = Fg,
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center
            });
            var delBtn = new Button
            {
                Content = "\u00d7",
                FontSize = 11,
                Cursor = Cursors.Hand,
                Background = Brushes.Transparent,
                Foreground = new SolidColorBrush(Color.FromRgb(255, 100, 100)),
                BorderThickness = new Thickness(0),
                Padding = new Thickness(4, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Tag = att
            };
            delBtn.Click += (_, _) =>
            {
                _textAttachments.Remove((string)delBtn.Tag);
                RebuildTextAttachChips();
            };
            sp.Children.Add(delBtn);
            chip.Child = sp;
            _textAttachPanel.Children.Add(chip);
        }
    }

    private Border BuildStatusBar()
    {
        _statusLabel = new Label
        {
            Content = "Configure KoboldCpp in Settings, then Start the server.",
            Foreground = FgDim,
            FontSize = 11,
            Padding = new Thickness(12, 0, 0, 0),
            VerticalContentAlignment = VerticalAlignment.Center
        };
        _tpsLabel = new Label
        {
            Content = "0.0 t/s",
            Foreground = Accent,
            FontSize = 11,
            FontWeight = FontWeight.FromOpenTypeWeight(600),
            Padding = new Thickness(8, 0, 12, 0),
            VerticalContentAlignment = VerticalAlignment.Center,
            HorizontalContentAlignment = HorizontalAlignment.Right,
            MinWidth = 70,
            Visibility = _settings.ShowTps ? Visibility.Visible : Visibility.Collapsed
        };
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(_statusLabel);
        Grid.SetColumn(_tpsLabel, 1);
        grid.Children.Add(_tpsLabel);
        return new Border
        {
            Background = Surface,
            BorderBrush = Border,
            BorderThickness = new Thickness(0, 1, 0, 0),
            Child = grid
        };
    }

    private static Border Card(UIElement child)
    {
        return new Border
        {
            Background = CardBg,
            BorderBrush = Border,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(10, 8, 10, 8),
            Margin = new Thickness(0, 0, 0, 8),
            Child = child
        };
    }

    private static TextBlock SectionLabel(string text)
    {
        return new TextBlock
        {
            Text = text,
            FontSize = 11,
            FontWeight = FontWeight.FromOpenTypeWeight(600),
            Foreground = Accent,
            Margin = new Thickness(0, 8, 0, 4)
        };
    }

    private static void ApplyComboStyle(ComboBox combo)
    {
        var itemStyle = new Style(typeof(ComboBoxItem));
        itemStyle.Setters.Add(new Setter(Control.BackgroundProperty, Surface));
        itemStyle.Setters.Add(new Setter(Control.ForegroundProperty, Fg));
        var hover = new Trigger { Property = ComboBoxItem.IsMouseOverProperty, Value = true };
        hover.Setters.Add(new Setter(Control.BackgroundProperty, Highlight));
        itemStyle.Triggers.Add(hover);
        combo.ItemContainerStyle = itemStyle;
    }

    private void UpdateBackendUIVisibility()
    {
        var isExt = _settings.BackendMode == "external";
        if (_openRouterModelRow != null)
            _openRouterModelRow.Visibility = isExt ? Visibility.Visible : Visibility.Collapsed;
        if (_openRouterKeyRow != null)
            _openRouterKeyRow.Visibility = isExt ? Visibility.Visible : Visibility.Collapsed;
        if (_customApiUrlRow != null)
            _customApiUrlRow.Visibility = isExt ? Visibility.Visible : Visibility.Collapsed;
        if (_externalProviderRow != null)
            _externalProviderRow.Visibility = isExt ? Visibility.Visible : Visibility.Collapsed;
    }

    private string _lastOrApiKey = "";
    private string _lastOrBaseUrl = "";

    public static string GetCompactionPrompt()
    {
        return @"You are a context compactor. Keep it UNDER 800 CHARACTERS total. No prose, no thinking out loud, no questions, no self-debate.

Output ONLY this format, nothing before or after:

Objective
[1 sentence]

Important Details
[1-2 bullet points if needed, else None]

Work State
Completed
[1-2 bullet points]
Active
[None if nothing in progress]
Blocked
[None if nothing blocked]
Next Move
[1 line]

Relevant Files
[1-2 bullet points or None]";
    }

    public static string GetCompactionPromptExtended()
    {
        return @"You are a context compactor for an autonomous coding agent. CRITICAL: Output UNDER 1200 CHARACTERS total. Do NOT write code, fix anything, or continue the task. Do NOT think out loud, debate yourself, or narrate your reasoning. Just compress.

===== ABSOLUTE OUTPUT RULES (do not break these) =====
1. HARD LIMIT: Entire output MUST be under 1200 characters. Count your characters before responding. If over, cut ruthlessly.
2. Your response must start with the exact characters: ## Objective
3. Your response must end after the last line of the '## Relevant Files' section. Nothing after it.
4. Do NOT write any preamble, sign-off, or extra text of any kind.
5. Do NOT wrap your output in ``` code fences.
6. Do NOT skip, rename, reorder, or merge any section headers, even if empty.
7. If a section has no content, write: None
8. Never invent information. If unsure, write: Unclear
9. Copy-paste exact text for file paths, error messages, commands — do not paraphrase them.
10. Only include the LATEST version if the conversation changed direction. No history of reversals.
11. One fact per bullet. Prefer 1-2 bullets per section. Never more than 3.
12. Output plain text using '-' for bullets only. No numbered lists, bold, italics.

===== EXACT STRUCTURE TO OUTPUT =====

## Objective
[1 sentence — under 100 chars]

## Important Details
- [1-2 bullets max, under 50 chars each, or None]

## User Preferences
- [1 bullet or None]

## Work State
### Completed
- [1-2 bullets max, or None]
### Active
- [1 bullet or None]
### Blocked
- [1 bullet or None]

## Next Move
[1 sentence — under 100 chars]

## Relevant Files
- [1-2 bullets max, or None]

===== SELF-CHECK =====
- Total length under 1200 chars?
- Starts with '## Objective', nothing before?
- Ends after Relevant Files, nothing after?
- Max 2 bullets per section?
- No prose, no thinking out loud?
If any NO, rewrite.";
    }

    /// <summary>Extracts relative file paths from free-text (user messages, build errors, etc.)
    /// by matching path-like patterns. Used to seed the discovery gate so the model can read
    /// files the user or compiler explicitly named without searching first.</summary>
    private static readonly Regex _pathExtractPattern = new(
        @"[\w./\\\-]+\.(cs|csproj|sln|xaml|py|ts|tsx|js|jsx|json|md|txt|yaml|yml|config|xml|html|css|svg|ps1|bat|sh|env|gitignore|editorconfig)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static List<string> ExtractPathsFromText(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return new List<string>();
        return _pathExtractPattern.Matches(text)
            .Select(m => m.Value.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static int PruneStaleToolResults(List<ChatMessage> history)
    {
        // tool_call_id -> (function name, arguments json), gathered from every assistant
        // message's ToolCalls. A "tool" role result only carries a ToolCallId, so this is
        // how we trace a result back to which call (and which path) produced it.
        var callInfo = new Dictionary<string, (string Name, string Args)>();
        foreach (var m in history)
            if (m.ToolCalls is { Count: > 0 })
                foreach (var tc in m.ToolCalls)
                    callInfo[tc.Id] = (tc.Function.Name, tc.Function.Arguments);

        // For each path, the index (position in `history`) of its LAST write_file result
        // that actually succeeded. Two things this fixes vs. the previous version:
        //
        // 1. Only counts a write if its RESULT indicates success. A failed ("ERROR:") or
        //    skipped ("SKIPPED:", e.g. empty content) write_file call never touched the
        //    file — ReadLedger.RecordWrite is likewise only called after a real
        //    File.WriteAllText success (see AgenticWorkflow.WriteFile). If this function
        //    didn't check that too, a failed write would make it erase a read_file result
        //    the ledger still (correctly) considers valid and un-re-readable — leaving the
        //    model with neither the old content nor permission to fetch it again.
        //
        // 2. Tracks WHERE the write sits in the list, not just whether one ever happened.
        //    tool-result messages are appended to historyForApi in strict execution order
        //    (see the tool-call loop above), so list index == chronological order. This
        //    lets the pruning below only stub reads that precede the write — a read_file
        //    called AFTER a successful write (re-verifying the new content) must be left
        //    alone. ReadLedger deliberately allows and records exactly that "write then
        //    re-read" pattern as valid; stubbing it here would erase content the ledger
        //    is telling the model it doesn't need to re-fetch.
        var lastSuccessfulWriteIndex = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < history.Count; i++)
        {
            var m = history[i];
            if (m.Role != "tool" || m.ToolCallId == null) continue;
            if (!callInfo.TryGetValue(m.ToolCallId, out var info) || info.Name != "write_file") continue;
            if (m.Content == null || m.Content.StartsWith("ERROR:") || m.Content.StartsWith("SKIPPED:") || m.Content.StartsWith("BLOCKED:")) continue;
            if (!TryGetJsonStringProp(info.Args, "path", out var writtenPath)) continue;
            lastSuccessfulWriteIndex[writtenPath] = i; // later entries overwrite earlier ones — we want the LAST write
        }

        if (lastSuccessfulWriteIndex.Count == 0) return 0; // nothing has actually been overwritten yet — nothing to prune

        var prunedChars = 0;
        for (int i = 0; i < history.Count; i++)
        {
            var m = history[i];
            if (m.Role != "tool" || m.ToolCallId == null) continue;
            if (m.Content is not { Length: > 200 }) continue; // already short — not worth touching
            if (!callInfo.TryGetValue(m.ToolCallId, out var info) || info.Name != "read_file") continue;
            if (!TryGetJsonStringProp(info.Args, "path", out var readPath)) continue;
            if (!lastSuccessfulWriteIndex.TryGetValue(readPath, out var writeIdx)) continue;
            if (i >= writeIdx) continue; // this read happened at/after the write — it's current, not stale

            prunedChars += m.Content.Length;
            m.Content = $"[stale — {readPath} was later overwritten via write_file; original read "
                      + "content omitted to save context. The file's current content is whatever "
                      + "you last wrote to it — do not rely on this old snapshot.]";
        }
        return prunedChars;
    }

    /// <summary>Read-only tools whose non-identical-but-successful repeated results may be
    /// collapsed to the most recent snapshot. Anything that mutates state or is
    /// non-deterministic (run_command, websearch, all write_*/move_*/delete_* etc.) is
    /// intentionally excluded — for those we only ever drop a call when its result is
    /// byte-for-byte identical to a later one.</summary>
    private static readonly HashSet<string> DuplicatePruneReadOnlyTools =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "read_file", "search_files", "list_directory", "analyze_method", "find_symbol",
            "search_methods", "symbols", "get_notes",
        };

    /// <summary>Drops assistant/tool-result pairs that re-executed an identical call —
    /// same function, same canonical JSON arguments — so repeated calls to the same thing
    /// (get_notes, re-reading the same file, update_notes(todo_add), ...) don't keep
    /// re-inflating the context every turn. Only ever removes COMPLETE pairs, so the
    /// assistant-msg/tool-result correspondence the backend requires is preserved; the
    /// newest occurrence of each call is always kept, and messages between the pairs
    /// (nudges, narration) are never touched.</summary>
    private static int PruneDuplicateToolCalls(List<ChatMessage> history, int startIndex)
    {
        // Defense-in-depth: the caller passes baseHistoryCount (captured at turn start), but a
        // mid-turn compaction rebuilds the list smaller — clamp so a stale start never scans
        // beyond the current list or lets RemoveAt hit an out-of-range index.
        startIndex = Math.Clamp(startIndex, 0, history.Count);

        // A "tool" result carries only a ToolCallId, so map every recorded call back to its
        // position, name, canonical arg-key and captured result content.
        var assistantByCallId = new Dictionary<string, (int Asst, string Name, string Args)>();
        var pairsByKey = new Dictionary<string, List<(int Asst, int Result, string Name, string Content)>>();

        for (int i = startIndex; i < history.Count; i++)
        {
            var m = history[i];
            if (m.Role == "assistant" && m.ToolCalls is { Count: > 0 })
            {
                foreach (var tc in m.ToolCalls)
                    if (!string.IsNullOrWhiteSpace(tc.Id))
                        // Null-tolerant like the execution loop (line ~6565): a malformed tool
                        // call (Function == null) from a quirky backend must not throw here,
                        // or the dedupe sweep would break the agent loop on every iteration.
                        assistantByCallId[tc.Id] = (i, tc.Function?.Name ?? "", tc.Function?.Arguments ?? "{}");
            }
            else if (m.Role == "tool" && m.ToolCallId != null
                     && assistantByCallId.TryGetValue(m.ToolCallId, out var info))
            {
                var key = info.Name + "|" + CanonicalizeJsonArgs(info.Args);
                if (!pairsByKey.TryGetValue(key, out var list))
                    pairsByKey[key] = list = [];
                list.Add((info.Asst, i, info.Name, m.Content ?? ""));
            }
        }

        var toRemove = new HashSet<int>();
        var prunedChars = 0;
        foreach (var group in pairsByKey.Values)
        {
            if (group.Count < 2) continue;
            var allowDifferentResult = DuplicatePruneReadOnlyTools.Contains(group[0].Name);

            // Chronological pass: only drop an EARLIER pair when the pair we keep is a
            // success. Guard: a later failure (BLOCKED:/SKIPPED:/REDUNDANT_READ_BLOCKED:)
            // must never delete a successful earlier result — e.g. "read_file succeeded,
            // then the same call got REDUNDANT_READ_BLOCKED" must retain content A.
            int prevKept = 0;
            for (int i = 1; i < group.Count; i++)
            {
                var prev = group[prevKept];
                var cur = group[i];

                bool removable = string.Equals(prev.Content, cur.Content, StringComparison.Ordinal)
                    || (allowDifferentResult
                        && !IsDedupeFailure(prev.Content)
                        && !IsDedupeFailure(cur.Content));

                if (removable)
                {
                    toRemove.Add(prev.Asst);
                    toRemove.Add(prev.Result);
                    prunedChars += prev.Content.Length + (history[prev.Asst].Content?.Length ?? 0);
                    prevKept = i; // collapse onto the surviving pair
                }
                else
                {
                    prevKept = i;
                }
            }
        }

        if (toRemove.Count == 0) return 0;
        foreach (var idx in toRemove.OrderByDescending(x => x))
            history.RemoveAt(idx);
        return prunedChars;
    }

    /// <summary>True when the line looks like a tool failure OR a BLOCKED rejection. Reuses
    /// AgenticWorkflow.IsToolFailure (single source of truth) and additionally treats
    /// "BLOCKED:" as failure — run_command keeps that text out of the shared prefix list,
    /// but a guard like this one must still treat it as unusable output.</summary>
    private static bool IsDedupeFailure(string result) =>
        AgenticWorkflow.IsToolFailure(result) || result.StartsWith("BLOCKED:", StringComparison.Ordinal);

    /// <summary>Canonicalizes the arguments JSON so equivalent calls collide on one key:
    /// object keys sorted lexically, "path" values normalized (case + separators) so
    /// ./src/Foo.cs, SRC\foo.cs and src/foo.cs count as the same file. Numeric/boolean/
    /// array values are serialized as-is; malformed JSON falls back to the raw string so
    /// byte-identical argument blobs can still be deduped.</summary>
    private static string CanonicalizeJsonArgs(string argsJson)
    {
        var sb = new StringBuilder((argsJson?.Length ?? 0) + 16);
        try
        {
            using var doc = JsonDocument.Parse(argsJson ?? "");
            if (doc.RootElement.ValueKind == JsonValueKind.Object)
            {
                AppendCanonicalJson(sb, doc.RootElement, "root");
                return sb.ToString();
            }
        }
        catch { /* fall through to raw */ }
        return argsJson ?? "";
    }

    private static void AppendCanonicalJson(StringBuilder sb, JsonElement el, string parentProp)
    {
        switch (el.ValueKind)
        {
            case JsonValueKind.Object:
                sb.Append('{');
                bool first = true;
                foreach (var prop in el.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal))
                {
                    if (!first) sb.Append(',');
                    first = false;
                    sb.Append('"').Append(prop.Name).Append("\":");
                    AppendCanonicalJson(sb, prop.Value, prop.Name);
                }
                sb.Append('}');
                break;
            case JsonValueKind.Array:
                sb.Append('[');
                bool firstItem = true;
                foreach (var item in el.EnumerateArray())
                {
                    if (!firstItem) sb.Append(',');
                    firstItem = false;
                    AppendCanonicalJson(sb, item, parentProp);
                }
                sb.Append(']');
                break;
            case JsonValueKind.String:
                var s = el.GetString() ?? "";
                // Paths: ./x, x\, and case differences all mean the same file (Windows).
                if (parentProp == "path" || parentProp.EndsWith("Path", StringComparison.Ordinal))
                    s = s.Replace('\\', '/').TrimStart('.', '/').ToLowerInvariant();
                // Escape so distinct strings can't collide through a stray quote.
                sb.Append('"').Append(s.Replace("\\", "\\\\").Replace("\"", "\\\"")).Append('"');
                break;
            default:
                sb.Append(el.GetRawText());
                break;
        }
    }
    private static bool TryGetJsonStringProp(string json, string propName, out string value)
    {
        value = "";
        if (string.IsNullOrWhiteSpace(json)) return false;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty(propName, out var el)
                && el.ValueKind == JsonValueKind.String)
            {
                var rawVal = el.GetString() ?? "";
                // Normalize paths so ./file.cs and cligameOne/file.cs match file.cs
                value = rawVal.Replace('\\', '/').TrimStart('.', '/');
                return value.Length > 0;
            }
        }
        catch { /* malformed tool-call arguments — nothing we can safely act on */ }
        return false;
    }

    /// <summary>
    /// Runs the secondary planner model to produce a project-context blueprint, gated to the
    /// template's own sections (see EnforceTemplateSections). Returns null if the planner is
    /// disabled/misconfigured, or if generation failed/produced nothing — callers should treat
    /// null as "no plan available" and proceed without one. Used both for the initial plan and
    /// to refresh the blueprint after context compaction.
    /// </summary>
    private async Task<string?> GeneratePlannerAnalysisAsync(string activeProjectPath, string? projectContext, string userRequestText, CancellationToken ct)
    {
        if (!_settings.PlannerEnabled) return null;
        if (_settings.BackendMode != "local") { Log("Agentic: Planner skipped — requires BackendMode = local"); return null; }
        if (string.IsNullOrWhiteSpace(_settings.PlannerModelPath)) { Log("Agentic: Planner skipped — PlannerModelPath is empty"); return null; }
        if (!File.Exists(_settings.PlannerModelPath)) { Log($"Agentic: Planner skipped — model not found at: {_settings.PlannerModelPath}"); return null; }
        if (string.IsNullOrWhiteSpace(projectContext)) { Log("Agentic: Planner skipped — project context is empty (set project folder in Sessions tab)"); return null; }
        if (string.IsNullOrWhiteSpace(_settings.PlannerTemplatePath) || !File.Exists(_settings.PlannerTemplatePath))
        {
            Log("Agentic: Planner skipped — PlannerTemplatePath is empty or file not found (disable Planner in advanced panel or set a template in Settings → Text)");
            return null;
        }

        try
        {
            // Clear context on the already-running main koboldcpp so planner starts fresh
            await _koboldClient.ClearStateAsync(ct);

            // Scan source files for real method signatures to prevent hallucination
            var methodScan = new StringBuilder();
            methodScan.AppendLine("## Source Code Methods");
            try
            {
                var csFiles = Directory.GetFiles(activeProjectPath, "*.cs", SearchOption.AllDirectories)
                    .Where(f => !f.Contains("\\obj\\") && !f.Contains("\\bin\\") && !f.Contains("\\Debug\\") && !f.Contains("\\Release\\"));
                foreach (var file in csFiles)
                {
                    var relPath = Path.GetRelativePath(activeProjectPath, file);
                    var content = File.ReadAllText(file);
                    var methodMatches = Regex.Matches(content,
                        @"(public|private|protected|internal)\s+(static\s+)?(partial\s+)?[\w\[\]<>,\s]+\s+(\w+)\s*\([^)]*\)\s*(\{|=>)");
                    var classMatch = Regex.Match(content,
                        @"(public|private|protected|internal)?\s*(static\s+)?(partial\s+)?(class|struct|interface)\s+(\w+)");
                    var className = classMatch.Success ? classMatch.Groups[5].Value : Path.GetFileNameWithoutExtension(file);
                    if (methodMatches.Count > 0)
                    {
                        methodScan.AppendLine($"\n{relPath} — class {className}:");
                        foreach (Match m in methodMatches)
                        {
                            var access = m.Groups[1].Value;
                            var modifiers = m.Groups[2].Value.Trim();
                            var returnType = m.Value.Split(new[] { '(' }, 2)[0].Trim().Split(' ').Last();
                            var methodName = m.Groups[4].Value;
                            var parenIdx = m.Value.IndexOf('(');
                            var args = parenIdx >= 0 ? m.Value.Substring(parenIdx, m.Value.IndexOf(')') - parenIdx + 1) : "()";
                            methodScan.AppendLine($"  {access} {modifiers} {returnType} {methodName}{args}");
                        }
                    }
                    else
                    {
                        methodScan.AppendLine($"\n{relPath} — class {className}: (no methods detected)");
                    }
                }
            }
            catch { methodScan.AppendLine("(could not scan source files)"); }

            var methodScanStr = methodScan.ToString();
            var safeProjectContext = projectContext ?? "";
            var plannerTemplate = "";
            if (!string.IsNullOrWhiteSpace(_settings.PlannerTemplatePath) && File.Exists(_settings.PlannerTemplatePath))
            {
                try { plannerTemplate = File.ReadAllText(_settings.PlannerTemplatePath).Trim(); }
                catch { Log("Agentic: Failed to read planner template file — using default"); }
            }
            if (string.IsNullOrWhiteSpace(plannerTemplate))
                plannerTemplate = "## Planner Analysis\n### Project Summary\n";
            // Strip everything after the ## Planner Analysis section
            // (template file also contains playbook sections meant for the main agent)
            var analysisEnd = Regex.Match(plannerTemplate, @"\n## (?!#)");
            if (analysisEnd.Success)
                plannerTemplate = plannerTemplate[..analysisEnd.Index];
            // Strip meta-annotations meant for humans maintaining the template
            plannerTemplate = Regex.Replace(plannerTemplate, @"\[CRITICAL\]\s*", "");
            plannerTemplate = Regex.Replace(plannerTemplate, @"\s*\(FIXED CATEGORY\)", "");

            // Extract headings first — these are the only structure the model needs.
            var expectedHeadings = Regex.Matches(plannerTemplate, @"(?m)^###\s+(.+?)\s*$")
                .Select(m => m.Groups[1].Value.Trim())
                .Where(h => h.Length > 0)
                .ToList();

            // Rebuild template from headings ONLY — strip all example output blocks,
            // prose descriptions, and instructions. Small models echo body text back
            // verbatim instead of writing real analysis, so feed them only the skeleton.
            if (expectedHeadings.Count > 0)
                plannerTemplate = "## Planner Analysis\n" + string.Join("\n",
                    expectedHeadings.Select(h => "### " + h));
            else
                plannerTemplate = "## Planner Analysis\n### Project Summary\n";

            static string EnforceTemplateSections(string text, List<string> headings)
            {
                if (headings.Count == 0) return text.Trim();

                // Match on the KNOWN heading names, not on "###" — small local models
                // regularly drift off the exact markdown level (using "##", bold text,
                // a trailing colon, or no markdown at all) once instructed to be terse.
                // Requiring literal "###" made every section fall back to "None generated."
                // even when the model's content was actually all there.
                var headingAlternation = string.Join("|", headings.OrderByDescending(h => h.Length).Select(Regex.Escape));
                var matches = Regex.Matches(text,
                    $@"(?m)^[ \t]*#{{1,6}}[ \t]*\**({headingAlternation})\**[ \t]*:?[ \t]*$",
                    RegexOptions.IgnoreCase);

                var sections = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < matches.Count; i++)
                {
                    var heading = matches[i].Groups[1].Value.Trim();
                    var start = matches[i].Index + matches[i].Length;
                    var end = i + 1 < matches.Count ? matches[i + 1].Index : text.Length;
                    var body = CleanBody(text[start..end]);
                    if (!sections.ContainsKey(heading) && body.Length > 0) sections[heading] = body;
                }

                // Nothing recognizable matched at all — the model ignored headings
                // entirely. Show its raw (still real) analysis instead of an
                // eight-section wall of "None generated."
                if (sections.Count == 0)
                    return CleanBody(text);

                var sb = new StringBuilder();
                sb.AppendLine("## Planner Analysis");
                sb.AppendLine();
                foreach (var h in headings)
                {
                    var matchKey = sections.Keys.FirstOrDefault(k => string.Equals(k, h, StringComparison.OrdinalIgnoreCase));
                    var body = matchKey != null ? sections[matchKey] : "None generated.";
                    sb.AppendLine($"### {h}");
                    sb.AppendLine(body);
                    sb.AppendLine();
                }
                return sb.ToString().TrimEnd();
            }

            // Strips reasoning-model <think> blocks, stray code-fence wrappers, and
            // post-amble thinking-junk (numbered steps, re-listed headings, business
            // jargon) so raw model "thinking" never ends up inside a section body.
            static string CleanBody(string body)
            {
                body = Regex.Replace(body, @"<think>[\s\S]*?</think>", "", RegexOptions.IgnoreCase);
                body = Regex.Replace(body, @"(?:^|\n)[ \t]*```[^\n]*", "", RegexOptions.IgnoreCase);
                // Strip at the first line that signals thinking/meta-commentary:
                //   numbered step + bold text  (e.g. "3. **Map Input...**")
                //   re-listed bold heading     (e.g. "*### Project Summary*")
                //   bullet re-analysis         (e.g. "- Need a brief summary")
                //   "Following baseline..."    (business jardon drift)
                var junkMatch = Regex.Match(body,
                    @"(?m)^(?:\d+\.\s+\*{1,2}|[ \t]*\*#{1,6}|[ \t]*-\s+(?:Need|I\s+need|I\s+must|I'll|Describe|List|Check|Fill|Map))",
                    RegexOptions.IgnoreCase);
                if (junkMatch.Success)
                    body = body[..junkMatch.Index].Trim();
                return body.Trim();
            }

            var plannerSystemPrompt =
                "You are a structured project analyst. Output ONLY the template below, filled in with real analysis. " +
                "Rules:\n" +
                "- No thinking process, no preamble, no meta-commentary, no numbered steps like \"1.\" or \"2.\".\n" +
                "- No text before ## Planner Analysis or after the last section.\n" +
                "- Do not add sections, headings, or bullet points that are not in the template.\n" +
                "- One line per method/file/folder entry. No prose paragraphs.\n" +
                "- Terse plain facts only — if a section doesn't apply, write \"None\" and move on.\n" +
                "- Start your reply with ## Planner Analysis.\n\n" +
                plannerTemplate;
            var planningMessages = new List<ChatMessage>
            {
                new() { Role = "system", Content = plannerSystemPrompt },
                new() { Role = "user", Content = $"## Project Context\n{safeProjectContext}\n\n{methodScanStr}\n\n## User Request\n{userRequestText}" }
            };

            var planResponse = await _koboldClient.SendChatCompletionAsync(
                planningMessages, maxTokens: 2048,
                temperature: _settings.PlannerTemperature,
                topP: _settings.PlannerTopP,
                topK: _settings.PlannerTopK,
                repeatPenalty: _settings.PlannerRepeatPenalty,
                ct: ct);

            // Clear planner context so it doesn't leak into the agent workflow
            await _koboldClient.ClearStateAsync(ct);

            var finishReason = planResponse?.Choices?.FirstOrDefault()?.FinishReason;
            var plan = planResponse?.Choices?.FirstOrDefault()?.Message?.Content;

            if (string.IsNullOrWhiteSpace(plan))
                return null;

            // Strip reasoning-model <think> blocks up front, before the heading cut below
            // uses string positions — a leaked <think> block can otherwise contain text
            // that looks like "## Planner Analysis" and throws off the cut point.
            plan = Regex.Replace(plan, @"<think>[\s\S]*?</think>", "", RegexOptions.IgnoreCase).Trim();
            if (string.IsNullOrWhiteSpace(plan))
                return null;

            // Defensive: some local models still emit a "thinking process" or
            // preamble before the actual template despite the system prompt
            // forbidding it. Cut everything before the first real heading.
            var headingMatch = Regex.Match(plan, @"##\s*Planner Analysis");
            if (headingMatch.Success && headingMatch.Index > 0)
                plan = plan[headingMatch.Index..];

            var accumulatedPlan = plan;
            var continueCount = 0;
            const int maxContinue = 2;

            while (!ct.IsCancellationRequested && continueCount < maxContinue && finishReason == "length")
            {
                continueCount++;
                Log($"Agentic: Planner output truncated (finish_reason=length) — continuing ({continueCount}/{maxContinue})");

                var continueResp = await _koboldClient.SendChatCompletionAsync(
                    new List<ChatMessage>
                    {
                        new() { Role = "system", Content = "You are a precise project analyst. Continue writing the analysis from where you stopped. Do NOT repeat any existing content." },
                        new() { Role = "user", Content = "Continue the analysis from where you left off. Output only the continuation — do not repeat anything already written." }
                    },
                    maxTokens: 2048,
                    temperature: _settings.PlannerTemperature,
                    topP: _settings.PlannerTopP,
                    topK: _settings.PlannerTopK,
                    repeatPenalty: _settings.PlannerRepeatPenalty,
                    ct: ct);

                var continuation = continueResp?.Choices?.FirstOrDefault()?.Message?.Content;
                if (string.IsNullOrWhiteSpace(continuation))
                    break;

                accumulatedPlan += "\n" + continuation;
                finishReason = continueResp?.Choices?.FirstOrDefault()?.FinishReason;
            }

            accumulatedPlan = EnforceTemplateSections(accumulatedPlan, expectedHeadings);
            Log($"Agentic: Planner summary generated ({accumulatedPlan.Length} chars, {continueCount} continuation(s))");
            return accumulatedPlan;
        }
        catch (Exception ex)
        {
            Log($"Agentic: Planner failed ({ex.Message}) — continuing without plan");
            return null;
        }
    }

    private async Task<string?> CompactHistoryAsync(List<ChatMessage> history, string? reasoningEffort, CancellationToken ct)
    {
        var compactMessages = new List<ChatMessage>();
        // Preserve system context for the summarization
        compactMessages.AddRange(history.Where(m => m.Role == "system"));
        // Include key conversation context — trim tool results to avoid blowing the budget
        var recentHistory = history
            .Where(m => m.Role != "system")
            .Select(m =>
            {
                if (m.Role == "tool" && m.Content != null && m.Content.Length > 500)
                    return new ChatMessage { Role = m.Role, ToolCallId = m.ToolCallId, Content = m.Content[..500] + $"\n... (truncated, was {m.Content.Length} chars)" };
                return m;
            })
            .ToList();
        compactMessages.AddRange(recentHistory);
        // Append compaction instruction — use the extended version when verbose prompts are enabled
        compactMessages.Add(new ChatMessage { Role = "user", Content = _settings.CompactPrompt ? GetCompactionPrompt() : GetCompactionPromptExtended() });

        var response = await SendChatCompletionViaBackendAsync(compactMessages,
            reasoningEffort, maxTokens: 2048,
            temperature: 0.2f, topP: null, topK: null, repeatPenalty: null,
            tools: null, toolChoice: "none", ct: ct);

        return response?.Choices?.FirstOrDefault()?.Message?.Content;
    }

    /// Role to use for mid-conversation nudges injected by the agentic/chat loops (context
    /// warnings, retry notices, loop-break instructions, etc.) — anything added AFTER the
    /// initial history has already been built, not via Insert(0, ...).
    ///
    /// External backends (OpenRouter and friends) use the standard OpenAI-style API, which
    /// accepts "system" messages anywhere in the array, so we keep "system" there for the
    /// stronger instruction-following it typically gets.
    ///
    /// Local KoboldCpp is running our custom Jinja chat template, which enforces "system
    /// message must be at the beginning" and throws (surfacing as a full request failure)
    /// if a system message shows up anywhere else. For local, we downgrade to "user" so the
    /// template doesn't choke, and prefix with a tag so the model still reads it as an
    /// out-of-band instruction rather than something the human typed.
    // FIX: zero-width marker so PruneStaleToolResults' per-iteration sweep (below) can
    // recognize an AgentNudgeRole message and exempt it from deletion. Previously that
    // sweep deleted every "system"/"user" role message added since the start of the turn
    // on nearly every iteration (see the guard at its call site) — which silently wiped
    // out every nudge (stall warning, read-file warning, redundant-read STOP, degenerate-
    // repetition break, loop-break, build reminder, confirmation nudge, etc.) before the
    // model's NEXT request ever went out. The model only ever saw hard "tool" role BLOCKED
    // results with none of the graduated warnings leading up to them.
    private const string NudgeMarker = "\u200B";
    private string AgentNudgeRole => _settings.BackendMode == "external" ? "system" : "user";

    /// Wraps nudge text with a "[SYSTEM NOTE]" tag when it's being sent as "user" (local
    /// backend) so the model doesn't mistake it for something the human actually said.
    /// No-op (beyond the marker) for external backends, since it's already sent as a real
    /// "system" message there. Always prepends NudgeMarker regardless of role so the sweep
    /// in PruneStaleToolResults's call site can identify and protect this message.
    private string TagIfNudgeIsUserRole(string content) =>
        NudgeMarker + (AgentNudgeRole == "user" ? $"[SYSTEM NOTE] {content}" : content);

    /// Routes chat completion to the active backend (local koboldcpp vs external OpenRouter).
    /// Structured tools are NEVER sent via the API to local KoboldCpp (avoids its Python
    /// tool-call parser).  The caller injects the tool definitions into the system prompt
    /// instead, and C# parses the model's raw text output.
    private async Task<ChatCompletionResponse> SendChatCompletionViaBackendAsync(List<ChatMessage> messages,
        string? reasoningEffort = null, int maxTokens = 65536,
        float? temperature = null, float? topP = null, int? topK = null, float? repeatPenalty = null,
        List<ToolDefinition>? tools = null, string? toolChoice = null,
        CancellationToken ct = default)
    {
        if (_settings.BackendMode == "external")
        {
            EnsureOpenRouterClient();
            return await _openRouterClient!.SendChatCompletionAsync(messages, reasoningEffort, maxTokens,
                temperature, topP, topK, repeatPenalty, tools, toolChoice,
                _settings.OpenRouterModel, ct);
        }
        // Local KoboldCpp: send tools via API when Jinja is active (native tool_calls),
        // bypass our text-injection approach entirely. Fall back to null tools for Universal mode.
        if (_koboldProcess?.UseJinjaTools == true)
            return await _koboldClient!.SendChatCompletionAsync(messages, reasoningEffort, maxTokens,
                temperature, topP, topK, repeatPenalty, tools, toolChoice, ct);
        return await _koboldClient!.SendChatCompletionAsync(messages, reasoningEffort, maxTokens,
            temperature, topP, topK, repeatPenalty, null, null, ct);
    }

    /// Routes streaming chat to the active backend (local koboldcpp vs external OpenRouter).
    private async Task<string> SendChatStreamViaBackendAsync(List<ChatMessage> messages,
        KoboldCppClient.StreamChunkHandler onChunk,
        string? reasoningEffort = null, int maxTokens = 65536,
        float? temperature = null, float? topP = null, int? topK = null, float? repeatPenalty = null,
        List<ToolDefinition>? tools = null, string? toolChoice = null,
        CancellationToken ct = default)
    {
        if (_settings.BackendMode == "external")
        {
            EnsureOpenRouterClient();
            return await _openRouterClient!.SendChatStreamAsync(messages, onChunk, reasoningEffort, maxTokens,
                temperature, topP, topK, repeatPenalty, tools, toolChoice, _settings.OpenRouterModel, ct);
        }

        // Local KoboldCpp: send tools via API when Jinja is active
        if (_koboldProcess?.UseJinjaTools == true)
            return await _koboldClient!.SendChatStreamAsync(messages, onChunk, reasoningEffort, maxTokens,
                temperature, topP, topK, repeatPenalty, tools, toolChoice, ct);
        return await _koboldClient!.SendChatStreamAsync(messages, onChunk, reasoningEffort, maxTokens,
            temperature, topP, topK, repeatPenalty, null, null, ct);
    }

    private void EnsureOpenRouterClient()
    {
        var apiKey = _settings.OpenRouterApiKey ?? "";
        var baseUrl = string.IsNullOrWhiteSpace(_settings.CustomApiUrl) ? "" : _settings.CustomApiUrl;
        if (_openRouterClient != null && _lastOrApiKey == apiKey && _lastOrBaseUrl == baseUrl)
            return;
        _openRouterClient?.Dispose();
        _openRouterClient = new OpenRouterClient(apiKey, string.IsNullOrWhiteSpace(baseUrl) ? null : baseUrl, _settings.TextTimeoutSeconds);
        _lastOrApiKey = apiKey;
        _lastOrBaseUrl = baseUrl;
    }

    private async Task PopulateModelsForProviderAsync(string provider)
    {
        if (_externalModelCombo == null) return;

        _refreshModelsBtn.IsEnabled = false;
        _externalModelCombo.Items.Clear();
        _externalModelCombo.Items.Add(new ComboBoxItem { Content = "Loading...", IsEnabled = false });

        try
        {
            if (provider == "OpenRouter")
            {
                EnsureOpenRouterClient();
                var models = await _openRouterClient!.GetModelsAsync();
                _allOpenRouterModels = models;
                _externalModelCombo.Items.Clear();
                var filterFree = _modelFilterCombo.SelectedItem is ComboBoxItem fi && fi.Tag is "free";
                foreach (var m in models)
                {
                    if (filterFree && !m.IsFree) continue;
                    var label = $"{m.Name} ({m.Id})";
                    if (m.ContextLength > 0)
                        label += $" — {m.ContextLength} ctx";
                    var item = new ComboBoxItem { Content = label, Tag = m.Id };
                    _externalModelCombo.Items.Add(item);
                    if (string.Equals(m.Id, _settings.OpenRouterModel, StringComparison.OrdinalIgnoreCase))
                        _externalModelCombo.SelectedItem = item;
                }
                if (_externalModelCombo.SelectedIndex < 0 && _externalModelCombo.Items.Count > 0)
                    _externalModelCombo.SelectedIndex = 0;
                Log($"Loaded {models.Count} OpenRouter models");
            }
            else
            {
                // Non-OpenRouter providers: call /v1/models on their API endpoint
                var baseUrl = _customApiUrlBox?.Text;
                if (string.IsNullOrWhiteSpace(baseUrl))
                {
                    _externalModelCombo.Items.Clear();
                    _externalModelCombo.Items.Add(new ComboBoxItem { Content = "Enter API URL first", IsEnabled = false });
                    _refreshModelsBtn.IsEnabled = true;
                    return;
                }
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
                if (!string.IsNullOrWhiteSpace(_settings.OpenRouterApiKey))
                    http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _settings.OpenRouterApiKey);
                var resp = await http.GetAsync(baseUrl.TrimEnd('/') + "/models");
                if (resp.IsSuccessStatusCode)
                {
                    var body = await resp.Content.ReadAsStringAsync();
                    var doc = JsonDocument.Parse(body);
                    _externalModelCombo.Items.Clear();
                    if (doc.RootElement.TryGetProperty("data", out var data))
                    {
                        foreach (var m in data.EnumerateArray())
                        {
                            var id = m.TryGetProperty("id", out var idProp) ? idProp.GetString() : "";
                            if (!string.IsNullOrWhiteSpace(id))
                                _externalModelCombo.Items.Add(new ComboBoxItem { Content = id, Tag = id });
                        }
                    }
                    if (_externalModelCombo.Items.Count > 0)
                    {
                        if (string.IsNullOrWhiteSpace(_settings.OpenRouterModel))
                            _externalModelCombo.SelectedIndex = 0;
                        Log($"Loaded {_externalModelCombo.Items.Count} models from {provider}");
                    }
                    else
                    {
                        _externalModelCombo.Items.Add(new ComboBoxItem { Content = "No models found", IsEnabled = false });
                    }
                }
                else
                {
                    var errBody = await resp.Content.ReadAsStringAsync();
                    _externalModelCombo.Items.Clear();
                    _externalModelCombo.Items.Add(new ComboBoxItem { Content = $"API error: {(int)resp.StatusCode}", IsEnabled = false });
                    Log($"Model list failed for {provider}: {(int)resp.StatusCode} {errBody.Truncate(100)}");
                }
            }
        }
        catch (Exception ex)
        {
            _externalModelCombo.Items.Clear();
            _externalModelCombo.Items.Add(new ComboBoxItem { Content = $"Failed: {ex.Message.Truncate(60)}", IsEnabled = false });
            Log($"Model fetch failed for {provider}: {ex.Message}");
        }

        _refreshModelsBtn.IsEnabled = true;
    }

    private void ApplyModelFilter()
    {
        if (_externalModelCombo == null || _allOpenRouterModels == null) return;
        var savedModel = _settings.OpenRouterModel;
        _externalModelCombo.Items.Clear();
        var filterFree = _modelFilterCombo.SelectedItem is ComboBoxItem fi && fi.Tag is "free";
        foreach (var m in _allOpenRouterModels)
        {
            if (filterFree && !m.Id.Contains(":free", StringComparison.OrdinalIgnoreCase)) continue;
            var label = $"{m.Name} ({m.Id})";
            if (m.ContextLength > 0)
                label += $" — {m.ContextLength} ctx";
            var item = new ComboBoxItem { Content = label, Tag = m.Id };
            _externalModelCombo.Items.Add(item);
            if (string.Equals(m.Id, savedModel, StringComparison.OrdinalIgnoreCase))
                _externalModelCombo.SelectedItem = item;
        }
        if (_externalModelCombo.SelectedIndex < 0 && _externalModelCombo.Items.Count > 0)
            _externalModelCombo.SelectedIndex = 0;
    }

    private static FrameworkElement PromptLabelWithButton(string label, RoutedEventHandler onLoad)
    {
        var lbl = new TextBlock
        {
            Text = label,
            FontSize = 11,
            FontWeight = FontWeight.FromOpenTypeWeight(600),
            Foreground = Accent,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 6, 0)
        };
        var btn = new Button
        {
            Content = "Load .md",
            Height = 22,
            FontSize = 10,
            Cursor = Cursors.Hand,
            Background = SurfaceAlt,
            Foreground = Fg,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(6, 0, 6, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        btn.Click += onLoad;
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 4) };
        panel.Children.Add(lbl);
        panel.Children.Add(btn);
        return panel;
    }

    private void LoadMdFile(bool isVideoTab)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "Markdown (*.md)|*.md|Text files (*.txt)|*.txt|All files (*.*)|*.*",
            DefaultExt = ".md",
            Title = "Load prompt from file"
        };
        if (dlg.ShowDialog(this) != true) return;

        try
        {
            var content = File.ReadAllText(dlg.FileName);
            if (string.IsNullOrWhiteSpace(content))
            {
                Log($"Error: '{Path.GetFileName(dlg.FileName)}' is empty.");
                return;
            }

            string? prompt = null, negative = null;
            var lines = content.Split('\n');
            string? currentSection = null;
            var sectionLines = new List<string>();
            bool foundPrompt = false;

            foreach (var rawLine in lines)
            {
                var line = rawLine.TrimEnd('\r').TrimEnd();

                if (line.StartsWith("# ", StringComparison.OrdinalIgnoreCase))
                {
                    if (currentSection != null && sectionLines.Count > 0)
                    {
                        var text = string.Join("\n", sectionLines).Trim();
                        if (string.Equals(currentSection, "prompt", StringComparison.OrdinalIgnoreCase))
                        {
                            if (text.Length > 0) { prompt = text; foundPrompt = true; }
                        }
                        else if (string.Equals(currentSection, "negative-prompt", StringComparison.OrdinalIgnoreCase))
                        {
                            if (text.Length > 0) negative = text;
                        }
                    }

                    var header = line[2..].Trim().ToLowerInvariant();
                    currentSection = header;
                    sectionLines.Clear();
                    continue;
                }

                if (currentSection == null) continue;

                if (line.Equals("[CONTENT]", StringComparison.OrdinalIgnoreCase) ||
                    line.Equals("[/CONTENT]", StringComparison.OrdinalIgnoreCase))
                    continue;

                sectionLines.Add(line);
            }

            if (currentSection != null && sectionLines.Count > 0)
            {
                var text = string.Join("\n", sectionLines).Trim();
                if (string.Equals(currentSection, "prompt", StringComparison.OrdinalIgnoreCase))
                {
                    if (text.Length > 0) { prompt = text; foundPrompt = true; }
                }
                else if (string.Equals(currentSection, "negative-prompt", StringComparison.OrdinalIgnoreCase))
                {
                    if (text.Length > 0) negative = text;
                }
            }

            if (!foundPrompt)
            {
                Log($"Error: '{Path.GetFileName(dlg.FileName)}' is missing a '# PROMPT' section.");
                return;
            }

            if (isVideoTab)
            {
                if (prompt != null) _videoPromptBox.Text = prompt;
                if (negative != null) _videoNegativeBox.Text = negative;
            }
            else
            {
                if (prompt != null) _promptBox.Text = prompt;
                if (negative != null) _negativeBox.Text = negative;
            }

            Log($"Loaded prompt from {Path.GetFileName(dlg.FileName)}");
            _statusLabel.Content = "Prompt loaded";
        }
        catch (Exception ex)
        {
            Log($"Error loading file: {ex.Message}");
        }
    }

    private static FrameworkElement SliderRow(string label, double min, double max, double val, double tick,
        out Slider slider, out Label valLabel)
    {
        var lbl = new Label
        {
            Content = val.ToString(tick >= 1 ? "F0" : "F1"),
            Foreground = FgDim,
            FontSize = 11,
            Padding = new Thickness(0),
            VerticalContentAlignment = VerticalAlignment.Center,
            MinWidth = 30,
            HorizontalContentAlignment = HorizontalAlignment.Right
        };
        valLabel = lbl;

        var s = new Slider
        {
            Minimum = min,
            Maximum = max,
            Value = val,
            TickFrequency = tick,
            IsSnapToTickEnabled = true,
            Height = 24,
            Foreground = Accent,
            Background = Border,
            BorderBrush = Brushes.Transparent,
            Style = SliderStyle
        };
        slider = s;

        var capturedLabel = lbl;
        var capturedTick = tick;
        s.ValueChanged += (_, e) =>
        {
            capturedLabel.Content = capturedTick >= 1 ? e.NewValue.ToString("F0") : e.NewValue.ToString("F1");
        };

        var grid = new Grid { Margin = new Thickness(0, 2, 0, 2) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(50) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        grid.Children.Add(new TextBlock { Text = label, Foreground = Fg, FontSize = 12, VerticalAlignment = VerticalAlignment.Center });
        Grid.SetColumn(s, 1);
        grid.Children.Add(s);
        Grid.SetColumn(lbl, 2);
        grid.Children.Add(lbl);

        return grid;
    }

    private static Button MakeBtn(string text, double w, double h, double fs, RoutedEventHandler click, Brush? bg = null, Brush? fg = null)
    {
        var btn = new Button
        {
            Content = text,
            Width = w,
            Height = h,
            FontSize = fs,
            Cursor = Cursors.Hand,
            Background = bg ?? Surface,
            Foreground = fg ?? Fg,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0)
        };
        btn.Click += click;
        return btn;
    }

    private void ApplyDarkScrollbarOverride()
    {
        DarkenScrollbarResources(this.Resources);
        if (Application.Current != null)
            DarkenScrollbarResources(Application.Current.Resources);

        try
        {
            var scrollbarStyle = BuildDarkScrollbarStyle();
            this.Resources[typeof(System.Windows.Controls.Primitives.ScrollBar)] = scrollbarStyle;
            if (Application.Current != null)
                Application.Current.Resources[typeof(System.Windows.Controls.Primitives.ScrollBar)] = scrollbarStyle;
        }
        catch { }
    }

    private static Style BuildDarkScrollbarStyle()
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
        return (Style)System.Windows.Markup.XamlReader.Parse(xaml);
    }

    private static void DarkenScrollbarResources(ResourceDictionary r)
    {
        var trackBg = CardBg;
        var thumbBg = ThumbBg;
        var thumbOver = ThumbHover;
        var thumbPressed = ThumbPressed;
        var btnBg = ButtonBg;
        var noBorder = Brushes.Transparent;

        r[SystemColors.ControlBrushKey] = trackBg;
        r[SystemColors.ControlLightBrushKey] = thumbBg;
        r[SystemColors.ControlDarkBrushKey] = noBorder;
        r[SystemColors.ControlDarkDarkBrushKey] = noBorder;
        r[SystemColors.ControlLightLightBrushKey] = noBorder;
        r["ScrollBar.Static.Background"] = trackBg;
        r["ScrollBar.Static.Border"] = noBorder;
        r["ScrollBar.Thumb.Static.Background"] = thumbBg;
        r["ScrollBar.Thumb.Static.Border"] = noBorder;
        r["ScrollBar.Thumb.MouseOver.Background"] = thumbOver;
        r["ScrollBar.Thumb.Pressed.Background"] = thumbPressed;
        r["ScrollBar.RepeatButton.Static.Background"] = btnBg;
        r["ScrollBar.RepeatButton.Static.Border"] = noBorder;
    }

    private async Task<bool> EnsureKoboldModeReadyAsync(KoboldMode required)
    {
        if (_isKoboldRunning && _currentMode == required)
            return true;

        ShowLoadingOverlay();
        _overlayLabel.Text = $"Starting KoboldCpp ({required} mode)...";

        if (_isKoboldRunning)
        {
            Log($"Switching to {required} mode, stopping KoboldCpp...");
            _statusLabel.Content = $"Switching to {required} mode...";
            await Task.Run(() =>
            {
                try { _koboldProcess?.Stop(); } catch { }
                _koboldProcess?.Dispose();
            });
            _koboldProcess = null;
            _koboldClient?.Dispose();
            _koboldClient = null;
            _isKoboldStarting = false;
            SetKoboldRunning(false);
        }

        if (_isKoboldStarting)
        {
            if (_koboldReadyTcs != null)
            {
                var timeout = Task.Delay(TimeSpan.FromMinutes(2));
                var done = await Task.WhenAny(_koboldReadyTcs.Task, timeout);
                if (done != _koboldReadyTcs.Task || !_isKoboldRunning)
                {
                    _isKoboldStarting = false;
                    HideLoadingOverlay();
                    Log("KoboldCpp not ready in time.");
                    _statusLabel.Content = "Start failed";
                    return false;
                }
            }
            _isKoboldStarting = false;
            if (_isKoboldRunning && _currentMode == required)
                HideLoadingOverlay();
            return _isKoboldRunning && _currentMode == required;
        }

        _isKoboldStarting = true;
        StartKoboldCpp(required);
        if (_koboldReadyTcs != null)
        {
            var timeout = Task.Delay(TimeSpan.FromMinutes(2));
            var done = await Task.WhenAny(_koboldReadyTcs.Task, timeout);
            if (done != _koboldReadyTcs.Task || !_isKoboldRunning)
            {
                _isKoboldStarting = false;
                HideLoadingOverlay();
                Log("KoboldCpp not ready in time.");
                _statusLabel.Content = "Start failed";
                return false;
            }
        }
        _isKoboldStarting = false;
        HideLoadingOverlay();
        return _isKoboldRunning;
    }

    private void StartKoboldCpp(KoboldMode mode)
    {
        if (_isKoboldRunning) return;
        _currentMode = mode;
        _koboldReadyTcs = new TaskCompletionSource();
        var exePath = _settings.KoboldExePath;

        if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath))
        { Log("KoboldCpp exe not found. Set it in Settings."); return; }

        try
        {
            _koboldProcess?.Dispose();
            _koboldClient?.Dispose();

            _koboldProcess = new KoboldCppProcess(
                exePath: exePath, port: _settings.KoboldPort, modelPath: _settings.ModelPath,
                textModelPath: _settings.TextModelPath, clipLPath: _settings.ClipLPath, t5Path: _settings.TextEncoderPath, vaePath: _settings.ImageVaePath,
                gpuLayers: _settings.GpuLayers, threads: _settings.Threads, contextSize: _settings.ContextSize, batchSize: _settings.BatchSize, blasBatchSize: _settings.BlasBatchSize,
                noKvOffload: _settings.NoKvOffload, useMlock: _settings.UseMlock, useMmap: _settings.UseMmap, keepClipOnCpu: _settings.KeepClipOnCpu,
                backend: _settings.Backend, extraArgs: _settings.KoboldExtraArgs,
                flashAttention: _settings.FlashAttention,
                contextShift: _settings.AgenticNoShift == "enable" && mode == KoboldMode.Text ? "disable" : _settings.ContextShift,
                launchBrowser: _settings.LaunchBrowser, useMmq: _settings.UseMmq,
                sdLoraPath: _settings.SdLoraPath, sdLoraMult: _settings.SdLoraMult, sdFlashAttention: _settings.SdFlashAttention,
                sdTiledVae: _settings.SdTiledVae, sdConvDirect: _settings.SdConvDirect, runtimeLora: _settings.RuntimeLora,
                fastForwarding: _settings.FastForwarding,
                allowSwa: _settings.AllowSwa,
                sdClipOnCpu: _settings.SdClipOnCpu, sdVaeOnCpu: _settings.SdVaeOnCpu,
                videoEnabled: _settings.VideoEnabled, videoModelPath: _settings.VideoModelPath,
                videoVaePath: _settings.VideoVaePath, videoT5Path: _settings.VideoT5Path,
                audioModelPath: _settings.AudioModelPath, voiceModelPath: _settings.VoiceModelPath, voiceTokenizerPath: _settings.VoiceTokenizerPath, voiceTtsDir: _settings.VoiceTtsDir,
                visionModelPath: _settings.VisionModelPath, visionMmprojPath: _settings.VisionMmprojPath, visionMmprojCpu: _settings.VisionMmprojCpu,
                textMmprojPath: _settings.TextMmprojPath, textMmprojCpu: _settings.TextMmprojCpu,
                textMoeExpertsOverride: _settings.TextMoeExpertsOverride, textMoeCpuMode: _settings.TextMoeCpuMode, textMoeCpuLayers: _settings.TextMoeCpuLayers,
                visionMoeExpertsOverride: _settings.VisionMoeExpertsOverride, visionMoeCpuMode: _settings.VisionMoeCpuMode, visionMoeCpuLayers: _settings.VisionMoeCpuLayers,
                textLoraPath: _settings.TextLoraPath, textLoraMult: _settings.TextLoraMult,
                videoLoraPath: _settings.VideoLoraPath, videoLoraMult: _settings.VideoLoraMult,
                audioLoraPath: _settings.AudioLoraPath, audioLoraMult: _settings.AudioLoraMult,
                enableWebSearch: _settings.EnableWebSearch,
                noCertify: _settings.NoCertify,
                mcpFilePath: _settings.MCPFilePath,
                chatTemplate: _settings.TextChatTemplate,
                musicLlmPath: _settings.MusicLlmPath, musicDiffusionPath: _settings.MusicDiffusionPath,
                musicEmbeddingsPath: _settings.MusicEmbeddingsPath, musicVaePath: _settings.MusicVaePath, musicVaeOnCpu: _settings.MusicVaeOnCpu,
                textQuantKv: _settings.TextQuantizedKvCache, textRopeScale: _settings.TextRopeScale, textRopeBase: _settings.TextRopeBase,
                visionQuantKv: _settings.VisionQuantizedKvCache, visionRopeScale: _settings.VisionRopeScale, visionRopeBase: _settings.VisionRopeBase,
                smartContext: _settings.SmartContext, overrideNativeContext: _settings.OverrideNativeContext, tensorSplit: _settings.TensorSplit,
                noAvx2: _settings.NoAvx2, failsafe: _settings.Failsafe, debugMode: _settings.DebugMode,
                overrideTensors: _settings.OverrideTensors, overrideKv: _settings.OverrideKv,
                cacheSlots: _settings.CacheSlots, defaultGenAmt: _settings.DefaultGenAmt,
                enableGuidance: _settings.EnableGuidance, thinkEffort: _settings.ThinkEffort,
                swaPadding: _settings.SwaPadding,
                draftModelPath: _settings.DraftModelPath, draftAmount: _settings.DraftAmount,
                useMtp: _settings.UseMtp, draftGpuLayers: _settings.DraftGpuLayers,
                embedsModelPath: _settings.EmbedsModelPath, embedsMaxCtx: _settings.EmbedsMaxCtx,
                embedsGpu: _settings.EmbedsGpu,
                autoFit: _settings.AutoFit,
                mode: mode);
            _koboldClient = new KoboldCppClient(_settings.KoboldPort);
            _koboldStdoutReadyTcs = new TaskCompletionSource();

            _koboldProcess.OutputReceived += msg => Dispatcher.BeginInvoke(() =>
            {
                var trimmed = msg.Trim();

                // Detect koboldcpp's own "server is up" announcement from stdout instead of
                // guessing readiness by repeatedly hammering the HTTP port. koboldcpp prints
                // a line like "Please connect to your KoboldAI instance at http://localhost:PORT"
                // (wording/case has varied a bit across versions), so match a few known
                // patterns rather than one exact string.
                if (!string.IsNullOrEmpty(trimmed) &&
                    (trimmed.Contains("please connect", StringComparison.OrdinalIgnoreCase)
                     || trimmed.Contains("starting kobold api", StringComparison.OrdinalIgnoreCase)
                     || (trimmed.Contains("http://", StringComparison.OrdinalIgnoreCase)
                         && trimmed.Contains(_settings.KoboldPort.ToString(), StringComparison.Ordinal))))
                {
                    _koboldStdoutReadyTcs?.TrySetResult();
                }
                bool hasPipe = trimmed.IndexOf('|') >= 0;
                bool hasSlash = trimmed.IndexOf('/') >= 0;
                bool hasTimeUnit = trimmed.Contains("s/it") || trimmed.Contains("it/s");
                bool hasEsc = trimmed.Contains('\x1b');
                bool isEmpty = trimmed.Length == 0;
                bool isGenProgress = trimmed.StartsWith("Generating (", StringComparison.Ordinal);
                bool isProcessingProgress = trimmed.StartsWith("Processing ", StringComparison.Ordinal) && hasSlash;
                bool isProgress = isEmpty || hasEsc || isGenProgress || isProcessingProgress ||
                    (hasPipe && hasSlash && hasTimeUnit);

                if (isProgress)
                {
                    if (isEmpty)
                    {
                        _lastKcppWasProgress = true;
                        return;
                    }
                    var display = trimmed;
                    if (hasEsc)
                    {
                        var idx = display.IndexOf('\x1b');
                        if (idx >= 0)
                            display = display[..idx];
                    }
                    LogReplaceLast($"KCPP: {display}");
                    _lastKcppWasProgress = true;
                }
                else
                {
                    if (_lastKcppWasProgress)
                        _lastKcppWasProgress = false;
                    Log($"KCPP: {trimmed}");
                }

                if (_settings.ShowTps)
                    TryParseTps(trimmed);
            });
            _koboldProcess.ErrorReceived += msg => Dispatcher.BeginInvoke(() =>
            {
                Log($"KCPP ERR: {msg}");
                if (_settings.ShowTps)
                    TryParseTps(msg);
            });
            _koboldProcess.ProcessExited += () => Dispatcher.BeginInvoke(() => SetKoboldRunning(false));

            _koboldProcess.Start();
            Log("KoboldCpp starting...");
            _statusLabel.Content = "Starting KoboldCpp...";
            _ = WaitForKoboldReadyAsync();
        }
        catch (Exception ex)
        {
            Log($"Error: {ex.Message}");
        }
    }

    private async Task WaitForKoboldReadyAsync()
    {
        if (_koboldClient == null) return;
        try
        {
            // Phase 1: wait for koboldcpp's stdout to announce the server is up, instead of
            // firing HTTP requests at a port nothing is listening on yet. Model loading can
            // legitimately take anywhere from a few seconds to a couple of minutes depending
            // on size/hardware, and every failed poll during that window used to throw and
            // get caught inside IsReadyAsync — harmless, but it spammed the debugger's
            // first-chance exception log once per second for the whole load duration.
            var overallTimeout = Task.Delay(TimeSpan.FromMinutes(2));
            if (_koboldStdoutReadyTcs != null)
            {
                var signaled = await Task.WhenAny(_koboldStdoutReadyTcs.Task, overallTimeout);
                if (signaled != _koboldStdoutReadyTcs.Task)
                {
                    Dispatcher.BeginInvoke(() => { Log("KoboldCpp not ready after 2 min."); _statusLabel.Content = "Timed out"; });
                    return;
                }
            }

            // Phase 2: the server just started listening (or we don't have a stdout signal
            // for some reason) — a small number of confirmation checks is enough here, no
            // need for the old 1-per-second loop.
            for (int i = 0; i < 10; i++)
            {
                if (await _koboldClient.IsReadyAsync(2000))
                {
                    Dispatcher.BeginInvoke(() =>
                    {
                        SetKoboldRunning(true);
                        Log("KoboldCpp ready.");
                        _statusLabel.Content = "Ready";
                        if (_koboldProcess?.UseJinjaTools == true)
                        {
                            var source = !string.IsNullOrWhiteSpace(_settings.TextChatTemplate) ? "user-provided" : "built-in";
                            Log($"Jinja tool calling enabled ({source} chat template).");
                        }
                        else
                            Log("No chat template available — using Universal tool calling mode.");
                    });
                    return;
                }
                await Task.Delay(500);
            }
            Dispatcher.BeginInvoke(() => { Log("KoboldCpp not ready after 2 min."); _statusLabel.Content = "Timed out"; });
        }
        catch { }
    }

    private async void StopKoboldCpp()
    {
        await Task.Run(() => { try { _koboldProcess?.Stop(); } catch { } });
        SetKoboldRunning(false);
        Log("KoboldCpp stopped.");
        _statusLabel.Content = "Stopped";
    }

    private async void RestartKoboldIfRunning()
    {
        if (_isKoboldRunning)
        {
            Log("Settings changed – restarting KoboldCpp...");
            // Show the overlay up front: StartKoboldCpp() below shows its own overlay
            // for the "starting" half, but the stop/dispose sequence here can block
            // for several seconds (Stop() does an HTTP abort plus up to two 5s
            // WaitForExit calls) with no overlay up during that time - leaving the
            // whole UI clickable against a process that's mid-teardown.
            ShowLoadingOverlay();
            _overlayLabel.Text = "Restarting KoboldCpp...";
            await Task.Run(() =>
            {
                try { _koboldProcess?.Stop(); } catch { }
                _koboldProcess?.Dispose();
            });
            _koboldProcess = null;
            _koboldClient?.Dispose();
            _koboldClient = null;
            // SetKoboldRunning(false) unconditionally hides the overlay - re-show it
            // right after so it stays up through StartKoboldCpp's startup wait too,
            // instead of dropping for the several-second-to-2-minute gap between
            // "old process stopped" and "new process reports ready".
            SetKoboldRunning(false);
            ShowLoadingOverlay();
            _overlayLabel.Text = "Restarting KoboldCpp...";
            _statusLabel.Content = "Restarting...";
            StartKoboldCpp(_currentMode);
        }
    }

    private void OnSettingsApplied()
    {
        AutoSaveConfig();
        if (!string.IsNullOrWhiteSpace(_settings.OutputPath))
            Directory.CreateDirectory(_settings.OutputPath);
        RebuildThumbnails();
        Log("Config saved.");
        RestartKoboldIfRunning();
    }

    private async void OnDownloadKoboldClick(object sender, RoutedEventArgs e)
    {
        var btn = (Button)sender;
        btn.IsEnabled = false;
        btn.Content = "...";
        try
        {
            var (version, url, fileName, shaUrl) = await GetLatestKoboldCppReleaseAsync();
            if (url == null) { Log("Could not find a suitable KoboldCpp release."); return; }

            if (version == _settings.KoboldCppVersion)
            {
                Log($"KoboldCpp is up to date ({version}).");
                return;
            }

            _overlayLabel.Text = "Downloading KoboldCpp...";
            ShowLoadingOverlay();
            await DownloadAndInstallAsync(url, shaUrl, fileName, version);
        }
        catch (Exception ex)
        {
            Log($"Update failed: {ex.Message}");
        }
        finally
        {
            HideLoadingOverlay();
            btn.Content = "Update";
            btn.IsEnabled = true;
        }
    }

    private async Task<bool> TryDownloadKoboldCppAsync()
    {
        try
        {
            var (version, url, fileName, shaUrl) = await GetLatestKoboldCppReleaseAsync();
            if (url == null) { Log("Could not find a suitable KoboldCpp release."); return false; }
            await DownloadAndInstallAsync(url, shaUrl, fileName, version);
            return true;
        }
        catch (Exception ex)
        {
            Log($"Download failed: {ex.Message}");
            _statusLabel.Content = "Download failed";
            return false;
        }
    }

    private async Task DownloadAndInstallAsync(string url, string? shaUrl, string fileName, string version)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "MyAiGen");
        Directory.CreateDirectory(tempDir);
        var tempPath = Path.Combine(tempDir, fileName);
        Log($"Downloading {fileName}...");
        _statusLabel.Content = "Downloading KoboldCpp...";
        await DownloadFileAsync(url, tempPath);

        if (shaUrl != null)
        {
            try
            {
                Log("Verifying checksum...");
                _statusLabel.Content = "Verifying checksum...";
                using var shaClient = new HttpClient();
                var expectedHash = (await shaClient.GetStringAsync(shaUrl)).Trim();
                var hashOnly = expectedHash.Split(' ', '\t')[0].Trim().ToUpperInvariant();

                using var stream = File.OpenRead(tempPath);
                var computedHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream)).ToUpperInvariant();

                if (hashOnly != computedHash)
                {
                    File.Delete(tempPath);
                    Log("Checksum mismatch – downloaded file is corrupt.");
                    _statusLabel.Content = "Download corrupt";
                    return;
                }
                Log("Checksum verified.");
            }
            catch (Exception ex)
            {
                Log($"Checksum verification failed: {ex.Message}");
            }
        }

        Directory.CreateDirectory(KoboldCppDirectory);
        var expectedPath = Path.Combine(KoboldCppDirectory, "koboldcpp.exe");

        if (_koboldProcess != null)
        {
            // Stop() does a blocking HTTP abort call plus up to two 5s WaitForExit
            // calls - up to ~13s of blocking work. Must not run on the UI thread.
            var processToStop = _koboldProcess;
            await Task.Run(() =>
            {
                try { processToStop.Stop(); } catch (Exception ex) { Log($"Stop error: {ex.Message}"); }
            });
            try { _koboldProcess.Dispose(); } catch (Exception ex) { Log($"Dispose error: {ex.Message}"); }
            _koboldProcess = null;
        }
        if (_koboldClient != null)
        {
            try { _koboldClient.Dispose(); } catch (Exception ex) { Log($"Client dispose error: {ex.Message}"); }
            _koboldClient = null;
        }
        _isKoboldStarting = false;
        SetKoboldRunning(false);
        try
        {
            foreach (var proc in System.Diagnostics.Process.GetProcessesByName("koboldcpp"))
            {
                proc.Kill();
                proc.WaitForExit(5000);
            }
        }
        catch { }

        if (File.Exists(expectedPath))
        {
            for (int retry = 0; retry < 10; retry++)
            {
                try { File.Delete(expectedPath); break; }
                catch { await Task.Delay(500); }
            }
        }
        File.Move(tempPath, expectedPath);
        try { Directory.Delete(tempDir, true); } catch { }

        _settings.KoboldExePath = expectedPath;
        _settings.KoboldCppVersion = version;
        AutoSaveConfig();
        Log($"KoboldCpp updated to {version}");
        _statusLabel.Content = $"Updated to {version}";
    }

    private async Task<(string version, string? url, string? fileName, string? shaUrl)> GetLatestKoboldCppReleaseAsync()
    {
        using var client = new HttpClient();
        client.DefaultRequestHeaders.UserAgent.ParseAdd("MyAiGen/1.0");
        client.Timeout = TimeSpan.FromSeconds(15);

        var json = await client.GetStringAsync("https://api.github.com/repos/LostRuins/koboldcpp/releases/latest");
        var doc = JsonDocument.Parse(json);
        var tag = doc.RootElement.GetProperty("tag_name").GetString() ?? "unknown";
        var assets = doc.RootElement.GetProperty("assets");

        var assetList = assets.EnumerateArray().Select(a => (
            name: a.GetProperty("name").GetString() ?? "",
            url: a.GetProperty("browser_download_url").GetString() ?? ""
        )).ToList();

        var exeAssets = assetList.Where(a => a.name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)).ToList();
        Log("Available: " + string.Join(", ", exeAssets.Select(e => e.name)));

        bool hasCuda = HasNvidiaGpu() || HasNvidiaSmi() || File.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "nvcuda.dll"));
        Log($"CUDA detected: {hasCuda}");

        string? match = null;

        if (hasCuda)
        {
            match = exeAssets.FirstOrDefault(n =>
                n.name.Equals("koboldcpp.exe", StringComparison.OrdinalIgnoreCase)).name;
            match ??= exeAssets.FirstOrDefault(n =>
                !n.name.Contains("nocuda", StringComparison.OrdinalIgnoreCase) &&
                !n.name.Contains("oldpc", StringComparison.OrdinalIgnoreCase)).name;
            match ??= exeAssets.FirstOrDefault(n =>
                !n.name.Contains("nocuda", StringComparison.OrdinalIgnoreCase)).name;
            match ??= exeAssets.FirstOrDefault().name;
        }
        else
        {
            match = exeAssets.FirstOrDefault(n =>
                n.name.Contains("nocuda", StringComparison.OrdinalIgnoreCase)).name;
            match ??= exeAssets.FirstOrDefault(n =>
                n.name.Contains("oldpc", StringComparison.OrdinalIgnoreCase)).name;
            match ??= exeAssets.FirstOrDefault().name;
        }

        if (match == null) return (tag, null, null, null);

        var exeAsset = assetList.FirstOrDefault(a => a.name == match);
        if (exeAsset.name == null) return (tag, null, null, null);

        var shaName = match + ".sha256";
        var shaAsset = assetList.FirstOrDefault(a => a.name.Equals(shaName, StringComparison.OrdinalIgnoreCase));

        return (tag, exeAsset.url, match, shaAsset.url);
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

    private async Task DownloadFileAsync(string url, string destPath)
    {
        using var client = new HttpClient();
        client.Timeout = TimeSpan.FromMinutes(10);

        using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();

        var totalBytes = response.Content.Headers.ContentLength ?? -1;
        using var contentStream = await response.Content.ReadAsStreamAsync();
        using var fileStream = new FileStream(destPath, FileMode.Create, FileAccess.Write, FileShare.None, 8192, true);

        var buffer = new byte[8192];
        long bytesRead = 0;
        int read;
        while ((read = await contentStream.ReadAsync(buffer, 0, buffer.Length)) > 0)
        {
            await fileStream.WriteAsync(buffer, 0, read);
            bytesRead += read;
            if (totalBytes > 0)
            {
                var pct = bytesRead * 100 / totalBytes;
                Dispatcher.BeginInvoke(() => _statusLabel.Content = $"Downloading KoboldCpp... {pct}%");
            }
        }
    }

    private int CalcMaxTokens()
    {
        var margin = _settings.TokenSafetyMargin switch
        {
            "none" => 0,
            "strict" => 32,
            "safe" => 256,
            _ => 128
        };
        return Math.Max(1, _settings.ContextSize - margin);
    }

    private static int EstimateTokenCount(IEnumerable<ChatMessage> messages)
    {
        long chars = 0;
        foreach (var m in messages)
            chars += (m.Content?.Length ?? 0) + (m.ToolCallId?.Length ?? 0)
                + (m.ToolCalls?.Sum(tc => (tc.Function?.Name?.Length ?? 0) + (tc.Function?.Arguments?.Length ?? 0)) ?? 0);
        return (int)(chars / 4);
    }

    /// <summary>
    /// max_tokens bounds ONLY the completion; the server still needs prompt_tokens + max_tokens
    /// to fit inside ContextSize. CalcMaxTokens() ignores prompt size entirely, so once the agent
    /// loop accumulates a few file reads the request silently overflows ContextSize — the backend
    /// then truncates/shifts the oldest messages out of context (which is why the model can
    /// contradict itself about a file it just read a few turns earlier), or eventually errors out.
    /// This computes what's actually left for the completion given the current prompt.
    /// </summary>
    private int CalcMaxTokensForPrompt(IEnumerable<ChatMessage> promptMessages)
    {
        var margin = _settings.TokenSafetyMargin switch
        {
            "none" => 0,
            "strict" => 32,
            "safe" => 256,
            _ => 128
        };
        var promptTokens = EstimateTokenCount(promptMessages);
        const int minCompletionTokens = 256;
        return Math.Max(minCompletionTokens, _settings.ContextSize - margin - promptTokens);
    }

    private void SetKoboldRunning(bool running)
    {
        _isKoboldRunning = running;
        _isKoboldStarting = false;
        if (!_isGenerating) _generateBtn.IsEnabled = true;
        HideLoadingOverlay();
        if (running)
        {
            _koboldReadyTcs?.TrySetResult();
        }
        else
        {
            _statusLabel.Content = "Stopped";
        }
    }

    private Task? _generationTask;

    private void UpdateVisionLockState()
    {
        bool locked = _screenOcrOverlays.Count > 0;
        if (_tabControl.SelectedIndex == 2)
        {
            _generateBtn.IsEnabled = !locked;
            if (locked)
            {
                _generateBtn.Content = "\U0001f512 Ask";
                _generateBtn.Background = BrVisionLocked;
            }
            else
            {
                _generateBtn.Content = "Ask";
                _generateBtn.Background = BrVisionUnlocked;
            }
        }
        UpdateTabLockState();
    }

    private void UpdateTabLockState()
    {
        if (_tabControl == null) return;
        bool generating = _isGenerating;
        int cur = _tabControl.SelectedIndex;
        bool visionOcrActive = _screenOcrOverlays.Count > 0;
        bool audioActive = _transcriber?.IsRunning == true;
        for (int i = 0; i < _tabControl.Items.Count; i++)
        {
            if (_tabControl.Items[i] is TabItem tab)
            {
                bool locked = (generating && i != cur) || (visionOcrActive && i != 2) || (audioActive && i != 4);
                tab.IsEnabled = !locked;
            }
        }
    }

    private async void OnGenerateClick(object sender, RoutedEventArgs e)
    {
        if (_tabControl.SelectedIndex == 2 && _screenOcrOverlays.Count > 0)
        {
            Log("Ask is locked while live translation is active.");
            return;
        }

        if (_isGenerating)
        {
            Log("Cancelling...");
            _cts?.Cancel();
            if (_koboldClient != null)
                _ = _koboldClient.AbortGenerationAsync();
            if (_generationTask != null)
            {
                try { await _generationTask; } catch { }
                _generationTask = null;
            }
            return;
        }

        var requiredMode = _tabControl.SelectedIndex switch
        {
            3 => KoboldMode.Text,
            1 => KoboldMode.Video,
            4 => KoboldMode.Audio,
            2 => KoboldMode.Vision,
            _ => KoboldMode.Image
        };

        if (requiredMode == KoboldMode.Text)
        {
            OnChatSendClick(sender, e);
            return;
        }

        if (requiredMode == KoboldMode.Audio)
            return;

        _isGenerating = true;
        UpdateTabLockState();

        try
        {
            if (!await EnsureKoboldModeReadyAsync(requiredMode))
                return;

            bool isVideo = requiredMode == KoboldMode.Video;
            bool isVision = requiredMode == KoboldMode.Vision;

            if (isVision)
            {
                if (string.IsNullOrWhiteSpace(_visionImagePath) || !File.Exists(_visionImagePath))
                { Log("Select an image first."); return; }
            }
            else
            {
                var prompt = isVideo ? _videoPromptBox.Text : _promptBox.Text;
                if (string.IsNullOrWhiteSpace(prompt)) { Log("Enter a prompt."); return; }
            }

            _cts?.Dispose();
            _cts = new CancellationTokenSource();
            var token = _cts.Token;
            _generationTask = requiredMode switch
            {
                KoboldMode.Video => RunVideoGenerationAsync(token),
                KoboldMode.Vision => RunVisionGenerationAsync(token),
                _ => RunGenerationAsync(token)
            };
            await _generationTask;
            _generationTask = null;
        }
        finally
        {
            if (_isGenerating)
            {
                _isGenerating = false;
                UpdateTabLockState();
            }
        }
    }

    private async Task RunGenerationAsync(CancellationToken token)
    {
        _progressBar.IsIndeterminate = true;
        _progressBar.Value = 0;
        _generateBtn.Content = "Stop";
        _generateBtn.Background = Error;
        _progressLabel.Content = "Generating...";
        _resultImage.Source = null;
        _placeholder.Visibility = Visibility.Visible;
        _statusLabel.Content = "Generating...";

        try
        {
            int.TryParse(_batchCountBox.Text, out int bc);
            if (bc < 1) bc = 1;
            if (bc > 100) bc = 100;

            long seed = -1;
            if (_randomSeedCheck.IsChecked != true)
                long.TryParse(_seedBox.Text, out seed);

            List<string>? initImages = null;
            if (_refImagePaths.Count > 0)
            {
                initImages = new List<string>(_refImagePaths.Count);
                foreach (var path in _refImagePaths)
                {
                    if (File.Exists(path))
                        initImages.Add(Convert.ToBase64String(File.ReadAllBytes(path)));
                }
                if (initImages.Count == 0) initImages = null;
            }

            var req = new ImageGenerationRequest
            {
                Prompt = _promptBox.Text,
                NegativePrompt = _negativeBox.Text,
                Width = (int)_widthSlider.Value,
                Height = (int)_heightSlider.Value,
                Steps = (int)_stepsSlider.Value,
                CfgScale = (float)_cfgSlider.Value,
                Seed = seed,
                InitImagesBase64 = initImages,
                DenoisingStrength = (float)_denoisingSlider.Value
            };

            for (int batch = 0; batch < bc; batch++)
            {
                token.ThrowIfCancellationRequested();
                _progressLabel.Content = $"Batch {batch + 1}/{bc}...";
                Log($"Batch {batch + 1}/{bc}...");

                var result = await _koboldClient!.GenerateImageAsync(req, token);

                var bmp = Base64ToBitmap(result.ImageBase64);
                if (bmp != null)
                {
                    Dispatcher.BeginInvoke(() =>
                    {
                        _resultImage.Source = bmp;
                        _placeholder.Visibility = Visibility.Collapsed;
                        _progressBar.IsIndeterminate = false;
                        _progressBar.Value = 100;
                        _progressLabel.Content = $"Batch {batch + 1}/{bc} | Seed: {result.Seed}";
                    });

                    if (!string.IsNullOrWhiteSpace(_settings.OutputPath))
                    {
                        Directory.CreateDirectory(_settings.OutputPath);
                        var fn = $"image_{result.Seed}_{DateTime.Now:yyyyMMdd_HHmmss}.png";
                        SaveImage(bmp, Path.Combine(_settings.OutputPath, fn));
                        Log($"Saved: {fn}");
                        RebuildThumbnails();
                    }
                }
                Log($"Batch {batch + 1} done (seed: {result.Seed}).");
            }
            _statusLabel.Content = bc > 1 ? $"Done ({bc} images)" : "Done";
            Log("Generation complete.");
        }
        catch (OperationCanceledException)
        {
            Log("Cancelled.");
            _statusLabel.Content = "Cancelled";
        }
        catch (Exception ex)
        {
            SaveCrashLog(ex);
            Log($"Error: {ex.Message}");
            _statusLabel.Content = "Failed";
        }
        finally
        {
            _isGenerating = false;
            UpdateTabLockState();
            _progressBar.IsIndeterminate = false;
            _generateBtn.Content = "Generate";
            _generateBtn.Background = BrGreen;
        }
    }

    private async Task RunVideoGenerationAsync(CancellationToken token)
    {
        _progressBar.IsIndeterminate = true;
        _generateBtn.Content = "Stop";
        _generateBtn.Background = Error;
        _progressBar.Value = 0;
        _progressLabel.Content = "Starting...";
        _videoPlayer.Stop();
        _videoPlayer.Source = null;
        _placeholder.Visibility = Visibility.Visible;
        _placeholder.Text = "Generating video...";
        _statusLabel.Content = "Generating video...";

        try
        {
            long seed = -1;
            if (_videoRandomSeedCheck.IsChecked != true)
                long.TryParse(_videoSeedBox.Text, out seed);

            var req = new VideoGenerationRequest
            {
                Prompt = _videoPromptBox.Text,
                NegativePrompt = _videoNegativeBox.Text,
                Width = (int)_videoWidthSlider.Value,
                Height = (int)_videoHeightSlider.Value,
                Steps = (int)_videoStepsSlider.Value,
                CfgScale = (float)_videoCfgSlider.Value,
                Seed = seed,
                Frames = (int)_videoFramesSlider.Value,
                Fps = (int)_videoFpsSlider.Value,
                OutputFormat = "webm"
            };

            _progressLabel.Content = "Submitting job...";

            var result = await _koboldClient!.GenerateVideoAsync(req, token);

            var tempDir = Path.Combine(Path.GetTempPath(), "MyAiGenVideos");
            Directory.CreateDirectory(tempDir);
            var ext = result.OutputFormat == "webm" ? ".webm" : ".gif";
            var tempFile = Path.Combine(tempDir, $"vid_{DateTime.Now:yyyyMMdd_HHmmss}{ext}");
            var bytes = Convert.FromBase64String(result.VideoBase64);
            await File.WriteAllBytesAsync(tempFile, bytes, token);

            result.SavedFilePath = tempFile;
            Dispatcher.BeginInvoke(() =>
            {
                _placeholder.Visibility = Visibility.Collapsed;
                _videoSeekSlider.Value = 0;
                _videoSeekSlider.IsEnabled = true;
                _videoPlayBtn.Content = "\u25B6";
                _videoPlayer.Source = new Uri(tempFile);
                _videoTimer.Start();
                _progressBar.Value = 100;
                _progressLabel.Content = $"{result.FrameCount} frames | {result.Fps} FPS | Seed: {result.Seed}";
                UpdateTransportBarVisibility();
            });

            if (!string.IsNullOrWhiteSpace(_settings.OutputPath) && Directory.Exists(_settings.OutputPath))
            {
                var fn = $"video_{result.Seed}_{DateTime.Now:yyyyMMdd_HHmmss}{ext}";
                var dest = Path.Combine(_settings.OutputPath, fn);
                await File.WriteAllBytesAsync(dest, bytes, token);
                Log($"Saved: {fn}");
            }

            _statusLabel.Content = "Video ready";
            Log($"Video done: {result.FrameCount} frames, {result.Fps} FPS, seed {result.Seed}");
        }
        catch (OperationCanceledException)
        {
            Log("Video generation cancelled.");
            _statusLabel.Content = "Cancelled";
        }
        catch (Exception ex)
        {
            SaveCrashLog(ex);
            Log($"Error: {ex.Message}");
            _statusLabel.Content = "Failed";
            _progressLabel.Content = "Failed";
            _progressBar.IsIndeterminate = false;
            _progressBar.Value = 0;
        }
        finally
        {
            _isGenerating = false;
            UpdateTabLockState();
            if (_tabControl.SelectedIndex == 1)
            {
                _generateBtn.Content = "Generate Video";
                _generateBtn.Background = BrBlue;
            }
            else
            {
                _generateBtn.Content = "Generate";
                _generateBtn.Background = BrGreen;
            }
        }
    }

    private async Task RunVisionGenerationAsync(CancellationToken token)
    {
        _progressBar.IsIndeterminate = true;
        _generateBtn.Content = "Stop";
        _generateBtn.Background = Error;
        bool isTranslate = _visionTargetLang.SelectedIndex != 0;
        _progressLabel.Content = isTranslate ? "Translating..." : "Asking...";
        _statusLabel.Content = isTranslate ? "Detecting text..." : "Asking vision model...";

        try
        {
            if (string.IsNullOrWhiteSpace(_visionImagePath) || !File.Exists(_visionImagePath))
            { Log("Select an image first."); return; }

            if (!isTranslate)
            {
                var prompt = _visionChatInput.Text;
                if (string.IsNullOrWhiteSpace(prompt)) { Log("Enter a prompt."); return; }
                _visionChatInput.Clear();
                var imgPath = _visionImagePath;
                _visionChatHistory.Add(new ChatMessage { Role = "user", Content = prompt, ImagePath = imgPath });
                ScrollVisionToEnd();
                var response = await _koboldClient!.SendVisionChatAsync(imgPath, prompt, CalcMaxTokens(), token);
                _visionChatHistory.Add(new ChatMessage { Role = "assistant", Content = response });
                var visionFiles = ParseFilesFromResponse(response);
                Dispatcher.BeginInvoke(() =>
                {
                    if (visionFiles.Count > 0)
                    {
                        _detectedFiles.Clear();
                        _detectedFiles.AddRange(visionFiles);
                        PopulateFilesPanel(_visionChatControl.FilesPanel, visionFiles);
                    }
                    else
                    {
                        _visionChatControl.FilesPanel.Visibility = Visibility.Collapsed;
                    }
                    ScrollVisionToEnd();
                    _progressBar.Value = 100;
                    _progressLabel.Content = "Done";
                });
                _statusLabel.Content = "Response received";
                Log($"Vision: {response.Truncate(200)}");
            }
            else
            {
                var imgPath = _visionImagePath;
                var targetLang = (string)_visionTargetLang.SelectedItem!;
                var prompt = "Return ONLY a JSON array of text regions in this image. For each region, include bounding box pixel coordinates and the original text, and translate it to " + targetLang + ". Format: [{\"x1\":num,\"y1\":num,\"x2\":num,\"y2\":num,\"original\":\"source text\",\"translated\":\"translation to " + targetLang + "\"}]. Empty array [] if none. No other text.";
                var userText = _visionChatInput.Text.Trim();
                _visionChatInput.Clear();
                if (!string.IsNullOrWhiteSpace(userText))
                    prompt = $"{userText}\n\n{prompt}";
                _visionChatHistory.Add(new ChatMessage { Role = "user", Content = userText, ImagePath = imgPath });
                ScrollVisionToEnd();

                var raw = await _koboldClient!.SendVisionChatAsync(imgPath, prompt, CalcMaxTokens(), token);
                var start = raw.IndexOf('[');
                var end = raw.LastIndexOf(']');
                var boxes = new List<BoundingBox>();
                if (start >= 0 && end > start)
                {
                    try
                    {
                        var items = System.Text.Json.JsonSerializer.Deserialize<List<BoundingBoxRaw>>(raw[start..(end + 1)], new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
                        if (items != null)
                            boxes = items.Select(i => new BoundingBox { X1 = i.X1, Y1 = i.Y1, X2 = i.X2, Y2 = i.Y2, Original = i.Original, Translated = i.Translated }).ToList();
                    }
                    catch { }
                }

                string resultText;
                if (boxes.Count == 0)
                {
                    Log("No text regions detected.");
                    resultText = "(no text regions found)";
                }
                else
                {
                    Log($"Found {boxes.Count} text regions.");
                    resultText = string.Join("\n\n", boxes.Select((b, i) =>
                        $"[{i + 1}] ({b.X1:F0},{b.Y1:F0})-({b.X2:F0},{b.Y2:F0})\n" +
                        $"  Orig: {b.Original ?? "(empty)"}\n" +
                        $"  Trans: {b.Translated ?? "(empty)"}"));
                    ShowBoundingBoxOverlay(boxes, imgPath);
                    _statusLabel.Content = $"{boxes.Count} regions";
                }
                _visionChatHistory.Add(new ChatMessage { Role = "assistant", Content = resultText });
                Dispatcher.BeginInvoke(() =>
                {
                    _visionChatControl.MessageList.ItemsSource = null;
                    _visionChatControl.MessageList.ItemsSource = _visionChatHistory;
                    ScrollVisionToEnd();
                    _progressBar.Value = 100;
                    _progressLabel.Content = "Done";
                });
            }
        }
        catch (OperationCanceledException)
        {
            Log("Vision cancelled.");
            _statusLabel.Content = "Cancelled";
        }
        catch (Exception ex)
        {
            SaveCrashLog(ex);
            Log($"Error: {ex.Message}");
            _statusLabel.Content = "Failed";
            _progressLabel.Content = "Failed";
            _progressBar.IsIndeterminate = false;
            _progressBar.Value = 0;
        }
        finally
        {
            _isGenerating = false;
            UpdateTabLockState();
            _progressBar.IsIndeterminate = false;
            var idx = _tabControl.SelectedIndex;
            if (idx == 2)
            {
                _generateBtn.Content = "Ask";
                _generateBtn.Background = BrVisionUnlocked;
            }
            else if (idx == 0)
            {
                _generateBtn.Content = "Generate";
                _generateBtn.Background = BrGreen;
            }
            else if (idx == 1)
            {
                _generateBtn.Content = "Generate Video";
                _generateBtn.Background = BrBlue;
            }
            else if (idx == 3)
            {
                _generateBtn.Content = "Send";
                _generateBtn.Background = BrTextSend;
            }
        }
    }

    private static void ShowBoundingBoxOverlay(List<BoundingBox> boxes, string imagePath)
    {
        var img = new BitmapImage(new Uri(imagePath));
        var imgWidth = (double)img.PixelWidth;
        var imgHeight = (double)img.PixelHeight;
        if (imgWidth <= 0 || imgHeight <= 0) return;

        var canvas = new Canvas { Width = imgWidth, Height = imgHeight };
        var rng = new Random(42);
        foreach (var b in boxes)
        {
            var color = Color.FromRgb((byte)rng.Next(100, 255), (byte)rng.Next(60, 180), (byte)rng.Next(60, 180));
            var rect = new Rectangle
            {
                Width = Math.Max(1, b.X2 - b.X1),
                Height = Math.Max(1, b.Y2 - b.Y1),
                Stroke = new SolidColorBrush(color),
                StrokeThickness = 2,
                Fill = new SolidColorBrush(Color.FromArgb(30, color.R, color.G, color.B))
            };
            Canvas.SetLeft(rect, b.X1);
            Canvas.SetTop(rect, b.Y1);
            canvas.Children.Add(rect);

            if (!string.IsNullOrWhiteSpace(b.Translated))
            {
                var tb = new TextBlock
                {
                    Text = b.Translated,
                    Foreground = Brushes.White,
                    Background = new SolidColorBrush(Color.FromArgb(180, 0, 0, 0)),
                    FontSize = Math.Max(10, Math.Min(18, (b.Y2 - b.Y1) * 0.4)),
                    Padding = new Thickness(3, 1, 3, 1),
                    TextWrapping = TextWrapping.Wrap,
                    MaxWidth = Math.Max(50, b.X2 - b.X1) * 2
                };
                Canvas.SetLeft(tb, b.X1);
                Canvas.SetTop(tb, b.Y2 + 2);
                canvas.Children.Add(tb);
            }
        }

        var window = new Window
        {
            Title = $"Text Detection — {Path.GetFileName(imagePath)}",
            Width = Math.Min(1200, imgWidth + 80),
            Height = Math.Min(900, imgHeight + 120),
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            Background = Bg,
            Content = new Viewbox
            {
                Stretch = Stretch.Uniform,
                Child = new Grid
                {
                    Width = imgWidth,
                    Height = imgHeight,
                    Children =
                    {
                        new Image { Source = img, Stretch = Stretch.None, Opacity = 0.85 },
                        canvas
                    }
                }
            }
        };
        window.ShowDialog();
    }

    private ScreenOcrOverlay CreateScreenOcrOverlay()
    {
        var overlay = new ScreenOcrOverlay();
        overlay.OcrRequested += OnScreenOcrCapture;
        overlay.FontSizeChanged += fs =>
        {
            _visionSyncingOverlay = true;
            _visionFontSlider.Value = fs;
            _visionFontLabel.Content = ((int)fs).ToString();
            _visionSyncingOverlay = false;
        };
        overlay.TextColorChanged += c =>
        {
            _visionSyncingOverlay = true;
            for (int i = 0; i < _visionTextColorCombo.Items.Count; i++)
            {
                if (_visionTextColorCombo.Items[i] is StackPanel p &&
                    p.Children[0] is Rectangle r &&
                    r.Fill is SolidColorBrush b &&
                    b.Color == c)
                { _visionTextColorCombo.SelectedIndex = i; break; }
            }
            _visionSyncingOverlay = false;
        };
        overlay.FontFamilyChanged += name =>
        {
            _visionSyncingOverlay = true;
            for (int i = 0; i < _visionFontCombo.Items.Count; i++)
            {
                if ((string)_visionFontCombo.Items[i] == name)
                { _visionFontCombo.SelectedIndex = i; break; }
            }
            _visionSyncingOverlay = false;
        };
        overlay.BgOpacityChanged += alpha =>
        {
            _visionSyncingOverlay = true;
            _visionOpacitySlider.Value = alpha;
            _visionOpacityLabel.Content = $"{(int)(alpha / 240.0 * 100)}%";
            _visionSyncingOverlay = false;
        };
        overlay.BgColorChanged += c =>
        {
            _visionSyncingOverlay = true;
            for (int i = 0; i < _visionBgColorCombo.Items.Count; i++)
            {
                if (_visionBgColorCombo.Items[i] is StackPanel p &&
                    p.Children[0] is Rectangle r &&
                    r.Fill is SolidColorBrush b &&
                    b.Color == c)
                { _visionBgColorCombo.SelectedIndex = i; break; }
            }
            _visionSyncingOverlay = false;
        };
        _liveOverlayCounter++;
        var overlayId = _liveOverlayCounter;
        _overlayById[overlayId] = overlay;
        var item = new ListBoxItem
        {
            Tag = overlay,
            Foreground = FgDim,
            Background = Brushes.Transparent,
            Cursor = Cursors.Hand
        };

        void UpdateItemText()
        {
            var combo = _hotkeyManager.GetCombo(overlayId);
            var hotkeyStr = combo.HasValue && !combo.Value.IsEmpty ? $" [{combo.Value}]" : "";
            item.Content = $"LIVE #{overlayId}{hotkeyStr}";
        }

        item.MouseDoubleClick += (_, _) =>
        {
            var current = _hotkeyManager.GetCombo(overlayId) ?? default;
            var win = new HotkeyConfigWindow(overlayId, current);
            if (win.ShowDialog() == true)
            {
                _hotkeyManager.Register(overlayId, win.Result);
                UpdateItemText();
            }
        };

        overlay.ClosedByUser += () => Dispatcher.BeginInvoke(() =>
        {
            _screenOcrOverlays.Remove(overlay);
            _liveOverlayList.Items.Remove(item);
            _hotkeyManager.Unregister(overlayId);
            _overlayById.Remove(overlayId);
            UpdateVisionLockState();
        });
        _screenOcrOverlays.Add(overlay);
        _liveOverlayList.Items.Add(item);
        UpdateItemText();

        if (_visionFontCombo.SelectedItem is string fontName)
            overlay.SetFontFamily(fontName);
        overlay.SetFontSize((int)_visionFontSlider.Value);
        var tcIdx = _visionTextColorCombo.SelectedIndex;
        if (tcIdx >= 0 && tcIdx < _visionTextColorCombo.Items.Count)
        {
            if (_visionTextColorCombo.Items[tcIdx] is StackPanel p && p.Children[0] is Rectangle r && r.Fill is SolidColorBrush tb)
                overlay.SetTextColor(tb.Color);
        }
        var bgIdx = _visionBgColorCombo.SelectedIndex;
        if (bgIdx >= 0 && bgIdx < _visionBgColorCombo.Items.Count)
        {
            if (_visionBgColorCombo.Items[bgIdx] is StackPanel bp && bp.Children[0] is Rectangle br && br.Fill is SolidColorBrush bb)
                overlay.SetBgColor(bb.Color);
        }
        overlay.SetBgOpacity((int)_visionOpacitySlider.Value);

        return overlay;
    }

    private void OnHotkeyTriggered(int overlayId)
    {
        if (_overlayById.TryGetValue(overlayId, out var overlay))
            overlay.ToggleVisibility();
    }

    private async void OnVisionChatSend(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_visionImagePath) || !File.Exists(_visionImagePath))
        {
            Log("Select an image first.");
            return;
        }
        if (string.IsNullOrWhiteSpace(_visionChatInput.Text))
        {
            Log("Enter a prompt.");
            return;
        }
        await EnsureKoboldModeReadyAsync(KoboldMode.Vision);
        OnGenerateClick(null, null);
    }

    private async void OnLiveTranslationAdd(object sender, RoutedEventArgs e)
    {
        if (!await EnsureKoboldModeReadyAsync(KoboldMode.Vision))
        {
            Log("Failed to start vision model for screen OCR.");
            return;
        }

        var overlay = CreateScreenOcrOverlay();
        overlay.Start();
        UpdateVisionLockState();
        Log("Additional live translation overlay started.");
    }

    private void OnLiveTranslationRemove(object sender, RoutedEventArgs e)
    {
        if (_liveOverlayList.SelectedItem is ListBoxItem item && item.Tag is ScreenOcrOverlay overlay)
        {
            overlay.Stop();
            overlay.Close();
            _screenOcrOverlays.Remove(overlay);
            _liveOverlayList.Items.Remove(item);
            UpdateVisionLockState();
            Log($"LIVE translation overlay stopped.");
        }
    }

    private async Task<string> OnScreenOcrCapture(byte[] imageBytes, CancellationToken ct)
    {
        if (_koboldClient == null) return "(client not ready)";

        var tempPath = Path.Combine(Path.GetTempPath(), "screen_ocr_capture.png");
        try
        {
            await File.WriteAllBytesAsync(tempPath, imageBytes, ct);
            var lang = _visionTargetLang.SelectedIndex;
            string prompt;
            if (lang > 0)
            {
                var targetLang = (string)_visionTargetLang.SelectedItem!;
                prompt = $"Return ONLY a JSON array of text regions in this image. For each region include bounding box coordinates and the original text translated to {targetLang}. Format: [{{\"x1\":num,\"y1\":num,\"x2\":num,\"y2\":num,\"original\":\"source\",\"translated\":\"translation\"}}]. Empty array [] if none. No other text.";
            }
            else
            {
                prompt = "Read all text visible in this image and return it exactly as written, preserving line breaks. If no text, say '(no text found)'.";
            }
            var result = await _koboldClient.SendVisionChatAsync(tempPath, prompt, CalcMaxTokens(), ct);
            if (string.IsNullOrWhiteSpace(result) || result.Contains("(empty"))
                return "(no text detected)";
            if (lang > 0)
            {
                var start = result.IndexOf('[');
                var end = result.LastIndexOf(']');
                if (start >= 0 && end > start)
                {
                    try
                    {
                        var items = System.Text.Json.JsonSerializer.Deserialize<List<BoundingBoxRaw>>(result[start..(end + 1)], new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
                        if (items != null && items.Count > 0)
                            result = string.Join("\n", items.Select(i =>
                                $"{i.Original ?? ""} → {i.Translated ?? ""}"));
                    }
                    catch { }
                }
            }
            return result;
        }
        catch (OperationCanceledException)
        {
            return "(cancelled)";
        }
        catch (Exception ex)
        {
            Log($"Screen OCR error: {ex.Message}");
            return $"(error: {ex.Message})";
        }
        finally
        {
            try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
        }
    }

    private void OnSaveImageClick(object _, RoutedEventArgs _2)
    {
        if (_resultImage.Source == null) return;
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "PNG (*.png)|*.png|JPEG (*.jpg)|*.jpg|BMP (*.bmp)|*.bmp",
            DefaultExt = ".png",
            FileName = $"image_{DateTime.Now:yyyyMMdd_HHmmss}.png"
        };
        if (dlg.ShowDialog(this) == true)
        {
            SaveImage(_resultImage.Source, dlg.FileName);
            Log($"Saved: {dlg.FileName}");
        }
    }

    private void OnSaveVideoClick(object _, RoutedEventArgs _2)
    {
        if (_videoPlayer.Source == null) return;
        var ext = _videoPlayer.Source.AbsolutePath.EndsWith(".webm", StringComparison.OrdinalIgnoreCase) ? ".webm" : ".gif";
        var filter = ext == ".webm" ? "WebM (*.webm)|*.webm|All files (*.*)|*.*" : "GIF (*.gif)|*.gif|All files (*.*)|*.*";
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Filter = filter,
            DefaultExt = ext,
            FileName = $"video_{DateTime.Now:yyyyMMdd_HHmmss}{ext}"
        };
        if (dlg.ShowDialog(this) == true)
        {
            try
            {
                if (_videoPlayer.Source.IsFile)
                {
                    File.Copy(_videoPlayer.Source.LocalPath, dlg.FileName, overwrite: true);
                    Log($"Saved: {dlg.FileName}");
                }
            }
            catch (Exception ex)
            {
                Log($"Save failed: {ex.Message}");
            }
        }
    }

    private static void SaveImage(ImageSource source, string path)
    {
        if (source is BitmapSource bs)
        {
            BitmapEncoder enc = path.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase)
                ? new JpegBitmapEncoder { QualityLevel = 95 }
                : path.EndsWith(".bmp", StringComparison.OrdinalIgnoreCase)
                    ? new BmpBitmapEncoder()
                    : new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(bs));
            using var fs = new FileStream(path, FileMode.Create);
            enc.Save(fs);
        }
    }

    private static BitmapImage? Base64ToBitmap(string base64)
    {
        try
        {
            var bytes = Convert.FromBase64String(base64);
            using var ms = new MemoryStream(bytes);
            var img = new BitmapImage();
            img.BeginInit();
            img.CacheOption = BitmapCacheOption.OnLoad;
            img.StreamSource = ms;
            img.EndInit();
            img.Freeze();
            return img;
        }
        catch { return null; }
    }

    private void RebuildThumbnails()
    {
        if (_thumbPreviewCombo != null && _thumbPreviewCombo.SelectedIndex != 1)
            return;
        _allThumbFiles.Clear();
        _thumbLoadedCount = 0;
        _thumbnailBox.Items.Clear();
        var path = _settings.OutputPath;
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
            return;
        try
        {
            _allThumbFiles.AddRange(Directory.GetFiles(path, "*.png")
                .Concat(Directory.GetFiles(path, "*.jpg"))
                .Concat(Directory.GetFiles(path, "*.jpeg"))
                .Concat(Directory.GetFiles(path, "*.bmp"))
                .OrderByDescending(f => File.GetLastWriteTime(f))
                .Take(50));
        }
        catch { }
        LoadNextThumbnailBatch();
    }

    private void LoadNextThumbnailBatch()
    {
        var end = Math.Min(_thumbLoadedCount + ThumbBatchSize, _allThumbFiles.Count);
        for (; _thumbLoadedCount < end; _thumbLoadedCount++)
        {
            var f = _allThumbFiles[_thumbLoadedCount];
            var img = new Image
            {
                Width = 100,
                Height = 100,
                Stretch = Stretch.Uniform,
                StretchDirection = StretchDirection.DownOnly,
                Cursor = Cursors.Hand,
                ToolTip = Path.GetFileName(f)
            };
            try
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.DecodePixelWidth = 100;
                bmp.UriSource = new Uri(f);
                bmp.EndInit();
                bmp.Freeze();
                img.Source = bmp;
            }
            catch { img.Source = null; }

            var border = new Border
            {
                Width = 100,
                Height = 100,
                Background = InputBgAlt,
                BorderBrush = BorderDim,
                BorderThickness = new Thickness(1),
                Margin = new Thickness(2),
                Child = img,
                Tag = f
            };

            var item = new ListBoxItem
            {
                Content = border,
                Tag = f,
                ToolTip = Path.GetFileName(f),
                Padding = new Thickness(0),
                MinWidth = 100,
                MinHeight = 100,
                HorizontalContentAlignment = HorizontalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center,
            };
            item.MouseDoubleClick += (_, _) =>
            {
                try { Process.Start(new ProcessStartInfo(f) { UseShellExecute = true }); }
                catch { }
            };

            var menu = new ContextMenu();
            var saveItem = new MenuItem { Header = "Save As..." };
            saveItem.Click += (_, _) => SaveThumbnailAs(f);
            menu.Items.Add(saveItem);
            var copyItem = new MenuItem { Header = "Copy to Clipboard" };
            copyItem.Click += (_, _) => CopyThumbnail(f);
            menu.Items.Add(copyItem);
            menu.Items.Add(new Separator());
            var delItem = new MenuItem { Header = "Delete" };
            delItem.Click += (_, _) => DeleteThumbnail(item);
            menu.Items.Add(delItem);
            item.ContextMenu = menu;

            _thumbnailBox.Items.Add(item);
        }

    }

    private void OnThumbScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (_thumbLoadedCount >= _allThumbFiles.Count) return;
        if (e.VerticalOffset + e.ViewportHeight >= e.ExtentHeight - 10)
            LoadNextThumbnailBatch();
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject o)
    {
        var count = VisualTreeHelper.GetChildrenCount(o);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(o, i);
            if (child is ScrollViewer sv) return sv;
            var result = FindScrollViewer(child);
            if (result != null) return result;
        }
        return null;
    }

    private static readonly string[] ConfirmationPhrases =
    {
        "please confirm", "confirm if you", "would you like me to", "should i proceed",
        "let me know if you", "let me know if this", "ready for me to", "shall i proceed",
        "do you want me to", "if you'd like me to", "if you want me to", "waiting for your",
        "awaiting your confirmation", "please let me know", "can you confirm"
    };

    private static bool SaysNoChangesNeeded(string content)
    {
        if (string.IsNullOrWhiteSpace(content)) return false;
        var normalized = content.ToUpperInvariant().Replace("_", "").Replace(" ", "").Trim();
        return normalized.StartsWith("NOCHANGESNEEDED");
    }

    private static bool LooksLikeAskingForConfirmation(string content)
    {
        foreach (var phrase in ConfirmationPhrases)
            if (content.Contains(phrase, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    private static readonly string[] ExplicitPlanPhrases =
    {
        "i will modify", "i'll modify", "i will start by", "i'll start by",
        "i will now proceed", "i'll now proceed",
        "i will make the following changes", "i'll make the following changes",
        "to implement this, i will", "to fix this, i will",
        "modify `", "modify \"", "i will update", "i'll update",
        "i will add", "i'll add", "i will change", "i'll change"
    };

    private static List<string> ExtractMentionedFileNames(string content)
    {
        var matches = Regex.Matches(content, @"[A-Za-z0-9_\-./\\]*[A-Za-z0-9_\-]+\.(cs|py|js|ts|jsx|tsx|java|go|rb|cpp|h|hpp|xaml)\b");
        return matches.Select(m => m.Value.Trim('`', '"', '\'')).Distinct(StringComparer.OrdinalIgnoreCase).Take(3).ToList();
    }

    private static bool LooksLikeExplicitEditPlan(string content)
    {
        var hasCommitPhrase = ExplicitPlanPhrases.Any(p => content.Contains(p, StringComparison.OrdinalIgnoreCase));
        if (!hasCommitPhrase) return false;

        var mentionsConcreteTarget = Regex.IsMatch(content, @"[A-Za-z0-9_]+\.(cs|py|js|ts|jsx|tsx|java|go|rb|cpp|h|hpp|xaml)\b")
            || Regex.IsMatch(content, @"`[A-Za-z_][A-Za-z0-9_]*`");

        return mentionsConcreteTarget;
    }

    private static bool IsAssistantLoop(IList<ChatMessage> history, string currentContent)
    {
        if (string.IsNullOrWhiteSpace(currentContent)) return false;
        for (int i = history.Count - 2; i >= 0; i--)
        {
            if (history[i].Role == "assistant" && !string.IsNullOrWhiteSpace(history[i].Content))
            {
                if (history[i].Content == currentContent) return true;
                break;
            }
            if (history[i].Role == "user") break;
        }
        return false;
    }

    private const string LoopBreakMessage =
        "[CRITICAL SYSTEM OVERRIDE - LOOP DETECTED]\n" +
        "You have issued the exact same output consecutively. Your internal loop prevention failed.\n\n" +
        "MANDATORY INSTRUCTIONS:\n" +
        "1. Try to reason in 1 sentence why your previous action repeated.\n" +
        "2. You are FORBIDDEN from repeating your previous action or message.\n" +
        "3. Execute EXACTLY one of these resolution steps:\n" +
        "   - ACTION A: Reason yourself to call 'write_file' if you have actionable code modifications.\n" +
        "   - ACTION B: Reason yourself to call 'read_file' on a file path you have NOT queried in this conversation.\n" +
        "   - ACTION C: Ask yourself 'Have I completed all the tasks?', if YES output 'NO_CHANGES_NEEDED' as your entire text if task execution is complete, if NO then do tool call.\n\n";

    /// <summary>
    /// Catches a different failure mode than IsAssistantLoop: instead of the same whole
    /// message repeating across turns, a single completion internally degenerates into
    /// repeating one short token/phrase over and over (e.g. "RESPONSE\n\nRESPONSE\n\n...").
    /// This happens most often on small/quantized local models when sampling collapses
    /// into a low-entropy attractor — commonly right after the model has been boxed in by
    /// several stacked hard-block/nudge messages with no obviously "safe" next move.
    /// IsAssistantLoop can't see this because there's nothing to compare against within a
    /// single turn, so it's checked separately here.
    /// </summary>
    private static bool IsDegenerateRepetition(string content, int minWords = 12, double minDistinctRatio = 0.15)
    {
        if (string.IsNullOrWhiteSpace(content)) return false;
        var words = content.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (words.Length < minWords) return false;
        var distinctRatio = (double)words.Distinct(StringComparer.OrdinalIgnoreCase).Count() / words.Length;
        return distinctRatio < minDistinctRatio;
    }

    private const string DegenerateRepetitionMessage =
        "[SYSTEM: DEGENERATE OUTPUT DETECTED]\n" +
        "Your last response collapsed into repeating the same word or phrase instead of real content. " +
        "That response has been discarded — it was not shown to the user and nothing was recorded from it.\n" +
        "Stop and produce a genuinely different response now: either a valid JSON tool call, or (if you are " +
        "truly finished) a short plain-text summary of the actual changes made. Do not repeat any single " +
        "word or line more than a couple of times.";

    private System.Windows.Threading.DispatcherTimer? _scrollThrottle;

    private void ScrollChatToEnd()
    {
        if (_scrollThrottle != null) return;

        _scrollThrottle = new System.Windows.Threading.DispatcherTimer(System.Windows.Threading.DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(100)
        };

        _scrollThrottle.Tick += (_, _) =>
        {
            _scrollThrottle.Stop();
            _scrollThrottle = null;
            _chatControl.ScrollToEnd();
        };

        _scrollThrottle.Start();
    }

    /// <summary>Flushes the agent chat's pending render so tool results appear one-by-one
    /// instead of batching. Walks the visual tree from MessageList to find the last
    /// item's MarkdownView and forces it to render any pending text immediately.</summary>
    private void FlushAgentChatRender()
    {
        var listBox = _chatControl.MessageList;
        if (listBox.ItemContainerGenerator.Status != System.Windows.Controls.Primitives.GeneratorStatus.ContainersGenerated)
            return;
        if (listBox.Items.Count == 0) return;
        var lastItem = listBox.Items[^1];
        var container = listBox.ItemContainerGenerator.ContainerFromItem(lastItem) as System.Windows.Controls.ListBoxItem;
        if (container == null) return;
        var view = FindVisualChild<MarkdownView>(container);
        if (view != null)
            view.Flush();
    }

    private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, i);
            if (child is T t) return t;
            var found = FindVisualChild<T>(child);
            if (found != null) return found;
        }
        return null;
    }

    /// <summary>
    /// Appends the visible status for one executed tool call to the chat bubble.
    /// read_file renders as a plain, non-collapsible label — its result is the full
    /// file text, which isn't worth an expander that just re-prints file content
    /// the user already has on disk.
    /// Everything else (write_file, run_command, etc.) keeps the collapsible block so
    /// the user can inspect the actual output.
    /// </summary>
    private static void SetAgentImage(ChatMessage msg, string fnName, string result, string projectPath)
    {
        if (fnName is "render_html" && result.StartsWith("Successfully rendered"))
        {
            var lastTo = result.LastIndexOf(" to ");
            if (lastTo >= 0)
            {
                var afterTo = result[(lastTo + 4)..];
                // The result string might contain notes after the filename, so split by space or newline
                var filename = afterTo.Split([' ', '\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
                if (!string.IsNullOrEmpty(filename))
                {
                    var imgCacheDir = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "PromptWhizz", "imgcache");
                    msg.ImagePath = System.IO.Path.Combine(imgCacheDir, filename);
                }
            }
        }
    }

    private static (bool ok, string? path) TryParseAttachFileResult(string result)
    {
        var lastTo = result.LastIndexOf(" to ");
        if (lastTo < 0) return (false, null);
        return (true, result[(lastTo + 4)..].Trim());
    }

    private static (bool ok, string? path) TryParseWriteFileResult(string result)
    {
        // "Successfully wrote 1234 bytes to some/path/file.txt — summary"
        var toIdx = result.IndexOf(" to ", StringComparison.Ordinal);
        if (toIdx < 0) return (false, null);
        var rest = result[(toIdx + 4)..];
        var dashIdx = rest.LastIndexOf(" — ");
        if (dashIdx < 0) return (false, null);
        return (true, rest[..dashIdx].Trim());
    }

    private static void SetAgentAttachment(ChatMessage msg, string fnName, string result, string projectPath)
    {
        var (ok, filename) = fnName switch
        {
            "attach_file" when result.StartsWith("Successfully attached") =>
                TryParseAttachFileResult(result),
            "write_file" when result.StartsWith("Successfully wrote") =>
                TryParseWriteFileResult(result),
            _ => (false, null)
        };
        if (!ok || string.IsNullOrEmpty(filename)) return;

        var cacheDir = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "PromptWhizz", "attcache");
        try { System.IO.Directory.CreateDirectory(cacheDir); }
        catch { return; }

        var safeName = SanitizeFileName(System.IO.Path.GetFileName(filename));
        var cachePath = System.IO.Path.Combine(cacheDir, safeName);
        var srcPath = System.IO.Path.Combine(projectPath, filename);

        // Deduplicate by cache path so the same attachment isn't added twice
        if (msg.Attachments != null)
        {
            bool dup = false;
            foreach (var a in msg.Attachments) { if (a.FullPath == cachePath) { dup = true; break; } }
            if (dup) return;
        }

        // Copy to cache (overwrite if cache already has this filename from a prior turn)
        if (System.IO.File.Exists(srcPath))
        {
            try { System.IO.File.Copy(srcPath, cachePath, overwrite: true); }
            catch { return; }
        }

        var ext = System.IO.Path.GetExtension(filename).ToLowerInvariant();
        if (msg.Attachments == null) return;
        msg.Attachments.Add(new AttachmentInfo
        {
            FileName = System.IO.Path.GetFileName(filename),
            FullPath = cachePath,
            IsImage = false,
            Icon = GetFileIcon(ext)
        });
    }

    private static string SanitizeFileName(string name)
    {
        var invalid = System.IO.Path.GetInvalidFileNameChars();
        var sb = new System.Text.StringBuilder(name.Length);
        foreach (var ch in name)
            sb.Append(System.Array.IndexOf(invalid, ch) >= 0 ? '_' : ch);
        return sb.ToString();
    }

    /// <summary>
    /// Image-generation failures (render_html erroring out, e.g. browser not found or a
    /// bad tool-call argument) are noise the user doesn't need to see as a chat bubble —
    /// the model still gets the error via historyForApi so it can retry, and it's still
    /// written to the debug Log, but it's not rendered in the transcript.
    /// </summary>
    /// <summary>
    /// Appends tool-result markup to the chat message, with dedup for consecutive
    /// same-file read_file calls: shows (+N) for repeated full reads, updates line
    /// numbers for repeated ranged reads. Resets tracking on any other tool call.
    /// </summary>
    private void AppendToolResult(ChatMessage msg, string fnName, string displayName, string result, bool isMono, string argsJson, string projectPath)
    {
        if (fnName == "read_file" && !result.StartsWith("REJECTED:")
            && !result.StartsWith("REDUNDANT_READ_BLOCKED:")
            && !result.StartsWith("PATH_NOT_DISCOVERED:")
            && !result.StartsWith("BLOCKED:")
            && !result.StartsWith("ERROR:"))
        {
            var rawPath = AgenticWorkflow.GetPathFromCall(fnName, argsJson) ?? "";
            var readPath = NormalizePathForTracking(rawPath, projectPath);
            if (!string.IsNullOrWhiteSpace(readPath))
            {
                if (string.Equals(readPath, _lastReadFilePath, StringComparison.OrdinalIgnoreCase) && _consecutiveReadCount > 0)
                {
                    _consecutiveReadCount++;
                    var dimPart = displayName.Length > 8 ? displayName[8..] : "";
                    string newLabel = $"Reading~{dimPart}~";
                    if (!displayName.Contains("(lines"))
                        newLabel = $"Reading~{dimPart}~ (+{_consecutiveReadCount})";

                    if (_lastReadInsertPos >= 0 && _lastReadLabel != null && msg.Content != null)
                    {
                        var oldLen = _lastReadLabel.Length;
                        if (_lastReadInsertPos + oldLen <= msg.Content.Length)
                        {
                            msg.Content = msg.Content[.._lastReadInsertPos] + newLabel + msg.Content[(_lastReadInsertPos + oldLen)..];
                            _lastReadLabel = newLabel;
                            // _lastReadInsertPos stays the same — label was replaced in-place
                            return;
                        }
                    }
                    // fallback: search if position tracking failed
                    var content = msg.Content ?? "";
                    int idx = content.LastIndexOf(_lastReadLabel ?? "");
                    if (idx >= 0)
                    {
                        msg.Content = content[..idx] + newLabel + content[(idx + _lastReadLabel!.Length)..];
                        _lastReadLabel = newLabel;
                        _lastReadInsertPos = idx;
                        return;
                    }
                }
                else
                {
                    _lastReadFilePath = readPath;
                    _consecutiveReadCount = 1;
                    _lastReadLabel = $"Reading~{(displayName.Length > 8 ? displayName[8..] : "")}~";
                    _lastReadInsertPos = -1; // will be set after append
                }
            }
        }
        else
        {
            _lastReadFilePath = null;
            _consecutiveReadCount = 0;
            _lastReadLabel = null;
            _lastReadInsertPos = -1;
        }

        // Track insertion position BEFORE appending, so we know exactly where it goes
        _lastReadInsertPos = msg.Content?.Length ?? 0;
        // Show block warnings ("Blocked / Read loop guard triggered / FileNotFound Guard Triggered",
        // "BLOCKED: ...") only when Debug is ON; with the toggle off they're hidden from the
        // transcript while the raw result is still recorded in historyForApi for the model.
        if (_settings.DebugShowBlockWarnings || !IsBlockedToolResult(fnName, result))
            msg.AppendContent(BuildToolStatusMarkup(fnName, displayName, result, isMono));
    }

    private static bool IsSuppressedImageGenFailure(string fnName, string result) =>
        fnName is "render_html" && result.StartsWith("ERROR:", StringComparison.Ordinal);

    private static string BuildToolStatusMarkup(string fnName, string displayName, string result, bool isMono)
    {
        // Trailing newline: whatever gets appended right after (model text, another
        // tool result) would otherwise be glued onto the same line with no separator,
        // e.g. "*Reading - Foo.cs*```csharp" — which the line-based markdown parser
        // can't recognize as a fence, corrupting all parsing for the rest of the message.
        // Leading newlines removed: they created empty Paragraph blocks between every
        // tool result (the parser adds 4px-margin paragraphs for blank lines), causing
        // gaps between collapsibles. The shared preprocessor already ensures
        // <<COLLAPSE starts on its own line, so no leading whitespace needed.
        if (fnName is "read_file")
        {
            var dimPart = displayName.Length > 8 ? displayName[8..] : "";
            if (result.StartsWith("REJECTED:") || result.StartsWith("REDUNDANT_READ_BLOCKED:")
                || result.StartsWith("HARD STOP:") || result.StartsWith("STOP.")
                || result.StartsWith("ERROR: \nFile already read") || result.StartsWith("ERROR: File already read"))
                return $"Blocked~{dimPart}~\n\nRead loop guard triggered — reading file blocked\n";
            if (result.StartsWith("BLOCKED:"))
                return $"Blocked~{dimPart}~\n\n{result}\n";
            if (result.StartsWith("PATH_NOT_DISCOVERED:"))
                return $"Blocked~{dimPart}~\n\nFileNotFound Guard Triggered\n";
            if (result.StartsWith("ERROR:"))
                return $"Blocked~{dimPart}~\n\n{result}\n";
            return $"Reading~{dimPart}~\n";
        }

        return $"<<COLLAPSE{(isMono ? "-MONO" : "")}:{displayName}>>{result}<</COLLAPSE>>\n";
    }

    /// <summary>Mirrors BuildToolStatusMarkup's "blocked" classifications, used to gate whether
    /// the warning is rendered at all when the Debug toggle is off.</summary>
    private static bool IsBlockedToolResult(string fnName, string result)
    {
        if (fnName == "read_file")
            return result.StartsWith("REJECTED:")
                || result.StartsWith("REDUNDANT_READ_BLOCKED:")
                || result.StartsWith("HARD STOP:")
                || result.StartsWith("STOP.")
                || result.StartsWith("ERROR: \nFile already read")
                || result.StartsWith("ERROR: File already read")
                || result.StartsWith("BLOCKED:")
                || result.StartsWith("PATH_NOT_DISCOVERED:")
                || result.StartsWith("ERROR:");
        return result.StartsWith("BLOCKED:")
            || result.StartsWith("REJECTED:")
            || result.StartsWith("REDUNDANT_READ_BLOCKED:")
            || result.StartsWith("PATH_NOT_DISCOVERED:");
    }

    private void ScrollAudioToEnd()
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (_audioScrollViewer != null) _audioScrollViewer.ScrollToEnd();
            else if (_audioHistory.Count > 0) _audioHistoryList.ScrollIntoView(_audioHistory[^1]);
        }), DispatcherPriority.Background);
    }

    private void ScrollVisionToEnd()
    {
        _visionChatControl.ScrollToEnd();
    }

    private void SaveThumbnailAs(string path)
    {
        try
        {
            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                FileName = Path.GetFileName(path),
                Filter = "PNG (*.png)|*.png|JPEG (*.jpg)|*.jpg|All files (*.*)|*.*"
            };
            if (dlg.ShowDialog(this) == true)
                File.Copy(path, dlg.FileName, overwrite: true);
        }
        catch (Exception ex) { Log($"Save failed: {ex.Message}"); }
    }

    private static void CopyThumbnail(string path)
    {
        try
        {
            var img = new BitmapImage(new Uri(path));
            Clipboard.SetImage(img);
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"Copy failed: {ex.Message}"); }
    }

    private void DeleteThumbnail(ListBoxItem item)
    {
        try
        {
            var path = item.Tag as string;
            if (path != null && File.Exists(path))
            {
                File.Delete(path);
                _thumbnailBox.Items.Remove(item);
                _allThumbFiles.Remove(path);
                if (_thumbLoadedCount > 0) _thumbLoadedCount--;
            }
        }
        catch (Exception ex) { Log($"Delete failed: {ex.Message}"); }
    }

    private void LoadConfig(string path)
    {
        _settings = AppSettings.Load(path);
        _configPath = path;
    }

    private void _textConfirmMode_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_textConfirmMode == null || !_uiReady) return;
        _settings.ConfirmMode = _textConfirmMode.SelectedIndex == 1 ? "manual" : "auto";
        AutoSaveConfig();
    }

    private void AutoSaveConfig()
    {
        if (!_uiReady) return;
        try
        {
            if (_promptBox != null) _settings.Prompt = _promptBox.Text;
            if (_negativeBox != null) _settings.NegativePrompt = _negativeBox.Text;
            if (_videoPromptBox != null) _settings.VideoPrompt = _videoPromptBox.Text;
            if (_videoNegativeBox != null) _settings.VideoNegativePrompt = _videoNegativeBox.Text;
            if (_widthSlider != null) _settings.ImageWidth = (int)_widthSlider.Value;
            if (_heightSlider != null) _settings.ImageHeight = (int)_heightSlider.Value;
            if (_stepsSlider != null) _settings.ImageSteps = (int)_stepsSlider.Value;
            if (_cfgSlider != null) _settings.ImageCfgScale = (float)_cfgSlider.Value;
            if (_videoWidthSlider != null) _settings.VideoWidth = (int)_videoWidthSlider.Value;
            if (_videoHeightSlider != null) _settings.VideoHeight = (int)_videoHeightSlider.Value;
            if (_videoStepsSlider != null) _settings.VideoSteps = (int)_videoStepsSlider.Value;
            if (_videoCfgSlider != null) _settings.VideoCfgScale = (float)_videoCfgSlider.Value;
            if (_videoFramesSlider != null) _settings.VideoFrames = (int)_videoFramesSlider.Value;
            if (_videoFpsSlider != null) _settings.VideoFps = (int)_videoFpsSlider.Value;
            if (_randomSeedCheck != null) _settings.ImageRandomSeed = _randomSeedCheck.IsChecked == true;
            if (_seedBox != null) _settings.ImageSeed = int.TryParse(_seedBox.Text, out var seed) ? seed : -1;
            if (_batchCountBox != null) _settings.ImageBatchCount = int.TryParse(_batchCountBox.Text, out var bc) ? bc : 1;
            if (_videoRandomSeedCheck != null) _settings.VideoRandomSeed = _videoRandomSeedCheck.IsChecked == true;
            if (_videoSeedBox != null) _settings.VideoSeed = long.TryParse(_videoSeedBox.Text, out var vs) ? vs : -1;
            if (_audioModeCombo != null) _settings.AudioMode = _audioModeCombo.SelectedIndex;
            if (_audioSourceCombo != null) _settings.AudioSource = _audioSourceCombo.SelectedIndex;
            if (_audioOverlayCheck != null) _settings.AudioOverlay = _audioOverlayCheck.IsChecked == true;
            if (_audioToPromptCheck != null) _settings.AudioToPrompt = _audioToPromptCheck.IsChecked == true;
            if (_audioTranslateCheck != null) _settings.AudioTranslate = _audioTranslateCheck.IsChecked == true;
            if (_audioFontSlider != null) _settings.AudioFontSize = _audioFontSlider.Value;
            if (_audioOpacitySlider != null) _settings.AudioOpacity = _audioOpacitySlider.Value;
            if (_audioFontCombo != null) _settings.AudioFontName = _audioFontCombo.SelectedIndex >= 0 && _audioFontCombo.SelectedIndex < _audioFontCombo.Items.Count ? _audioFontCombo.Items[_audioFontCombo.SelectedIndex]?.ToString() ?? "" : "";
            if (_audioColorCombo != null) _settings.AudioTextColor = _audioColorCombo.SelectedIndex >= 0 && _audioColorCombo.SelectedIndex < _overlayColorPresets.Length ? _overlayColorPresets[_audioColorCombo.SelectedIndex].Name : "";
            if (_visionTargetLang != null) _settings.VisionTargetLang = _visionTargetLang.SelectedIndex.ToString();
            if (_visionFontSlider != null) _settings.VisionFontSize = _visionFontSlider.Value;
            if (_visionOpacitySlider != null) _settings.VisionOpacity = _visionOpacitySlider.Value;
            if (_visionFontCombo != null) _settings.VisionFontName = _visionFontCombo.SelectedIndex >= 0 && _visionFontCombo.SelectedIndex < _visionFontCombo.Items.Count ? _visionFontCombo.Items[_visionFontCombo.SelectedIndex]?.ToString() ?? "" : "";
            if (_visionTextColorCombo != null) _settings.VisionTextColor = _visionTextColorCombo.SelectedIndex >= 0 ? _visionTextColorCombo.SelectedIndex.ToString() : "";
            if (_visionBgColorCombo != null) _settings.VisionBgColor = _visionBgColorCombo.SelectedIndex >= 0 ? _visionBgColorCombo.SelectedIndex.ToString() : "";
            if (_maxHistoryBox != null) _settings.MaxHistoryCount = int.TryParse(_maxHistoryBox.Text, out var mh) ? mh : 50;
            if (_textEnableThinking != null) _settings.EnableThinking = _textEnableThinking.SelectedIndex == 1;
            if (_textThinkingEffort != null) _settings.ThinkingEffort = _textThinkingEffort.SelectedItem as string ?? "medium";
            if (_textCompactPrompt != null) _settings.CompactPrompt = _textCompactPrompt.SelectedIndex == 0;
            if (_textNoCertify != null) _settings.NoCertify = _textNoCertify.SelectedIndex == 1;
            if (_textAgenticWorkflow != null) _settings.AgenticWorkflowMode = _textAgenticWorkflow.SelectedIndex == 1 ? "enable" : "disable";
            if (_textSystemPromptBox != null) _settings.TextSystemPrompt = _textSystemPromptBox.Text;
            if (_textContextSlider != null) _settings.ContextSize = (int)_textContextSlider.Value;
            if (_textBatchSizeBox != null && int.TryParse(_textBatchSizeBox.Text, out var bs) && bs > 0) _settings.BatchSize = bs;
            if (_textBlasBatchBox != null && int.TryParse(_textBlasBatchBox.Text, out var bbs) && bbs > 0) _settings.BlasBatchSize = bbs;
            if (_textGpuLayersBox != null && int.TryParse(_textGpuLayersBox.Text, out var gl)) _settings.GpuLayers = gl;
            if (_textTempSlider != null) _settings.TextTemperature = (float)_textTempSlider.Value;
            if (_textTopPSlider != null) _settings.TextTopP = (float)_textTopPSlider.Value;
            if (_textTopKSlider != null) _settings.TextTopK = (int)_textTopKSlider.Value;
            if (_textRepPenSlider != null) _settings.TextRepeatPenalty = (float)_textRepPenSlider.Value;
            if (_plannerModelBox != null) _settings.PlannerModelPath = _plannerModelBox.Text;
            if (_plannerTemplateBox != null) _settings.PlannerTemplatePath = _plannerTemplateBox.Text;
            if (_plannerEnabledCombo != null) _settings.PlannerEnabled = _plannerEnabledCombo.SelectedIndex == 1;
            if (_debugCheck != null) _settings.DebugShowBlockWarnings = _debugCheck.IsChecked == true;

            var dir = Path.GetDirectoryName(_configPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);
            _settings.Save(_configPath);
        }
        catch (Exception ex)
        {
            Log($"Warning: {ex.Message}");
        }
    }

    private Border BuildLoadingOverlay()
    {
        var progress = new ProgressBar
        {
            Width = 200,
            IsIndeterminate = true,
            Height = 4,
            Foreground = Accent,
            Background = BorderAlt
        };

        _overlayLabel = new TextBlock
        {
            Text = "Starting KoboldCpp...",
            Foreground = Fg,
            FontSize = 16,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 16, 0, 0)
        };

        var stack = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center
        };
        stack.Children.Add(progress);
        stack.Children.Add(_overlayLabel);

        var overlay = new Border
        {
            Child = stack,
            Background = new SolidColorBrush(Color.FromArgb(200, 0, 0, 0)),
            Visibility = Visibility.Collapsed
        };
        Grid.SetRowSpan(overlay, 3);
        Panel.SetZIndex(overlay, 1000);
        return overlay;
    }

    private void ShowLoadingOverlay()
    {
        _loadingOverlay.Visibility = Visibility.Visible;
    }

    private void HideLoadingOverlay()
    {
        _loadingOverlay.Visibility = Visibility.Collapsed;
    }

    private void ApplySettingsToUI()
    {
        _widthSlider.Value = _settings.ImageWidth;
        _heightSlider.Value = _settings.ImageHeight;
        _stepsSlider.Value = _settings.ImageSteps;
        _cfgSlider.Value = _settings.ImageCfgScale;
        _promptBox.Text = _settings.Prompt;
        _negativeBox.Text = _settings.NegativePrompt;
        if (_modeCombo != null) _modeCombo.SelectedIndex = _settings.ViewMode;
        _randomSeedCheck.IsChecked = _settings.ImageRandomSeed;
        _seedBox.Text = _settings.ImageSeed >= 0 ? _settings.ImageSeed.ToString() : "42";
        _batchCountBox.Text = _settings.ImageBatchCount.ToString();
        if (_videoWidthSlider != null) _videoWidthSlider.Value = _settings.VideoWidth;
        if (_videoHeightSlider != null) _videoHeightSlider.Value = _settings.VideoHeight;
        if (_videoStepsSlider != null) _videoStepsSlider.Value = _settings.VideoSteps;
        if (_videoCfgSlider != null) _videoCfgSlider.Value = _settings.VideoCfgScale;
        if (_videoFramesSlider != null) _videoFramesSlider.Value = _settings.VideoFrames;
        if (_videoFpsSlider != null) _videoFpsSlider.Value = _settings.VideoFps;
        if (_videoPromptBox != null) _videoPromptBox.Text = _settings.VideoPrompt;
        if (_videoNegativeBox != null) _videoNegativeBox.Text = _settings.VideoNegativePrompt;
        if (_videoRandomSeedCheck != null) _videoRandomSeedCheck.IsChecked = _settings.VideoRandomSeed;
        if (_videoSeedBox != null) _videoSeedBox.Text = _settings.VideoSeed >= 0 ? _settings.VideoSeed.ToString() : "42";
        if (_audioModeCombo != null) _audioModeCombo.SelectedIndex = _settings.AudioMode;
        if (_audioSourceCombo != null) _audioSourceCombo.SelectedIndex = _settings.AudioSource;
        if (_audioOverlayCheck != null) _audioOverlayCheck.IsChecked = _settings.AudioOverlay;
        if (_audioToPromptCheck != null) _audioToPromptCheck.IsChecked = _settings.AudioToPrompt;
        if (_audioTranslateCheck != null) _audioTranslateCheck.IsChecked = _settings.AudioTranslate;
        if (_audioFontSlider != null) _audioFontSlider.Value = _settings.AudioFontSize;
        if (_audioOpacitySlider != null) _audioOpacitySlider.Value = _settings.AudioOpacity;
        if (_audioFontCombo != null) _audioFontCombo.SelectedIndex = FindFontIndex(_audioFontCombo, _settings.AudioFontName);
        if (_audioColorCombo != null) _audioColorCombo.SelectedIndex = FindColorIndex(_settings.AudioTextColor);
        if (_visionFontSlider != null) _visionFontSlider.Value = _settings.VisionFontSize;
        if (_visionOpacitySlider != null) _visionOpacitySlider.Value = _settings.VisionOpacity;
        if (_visionFontCombo != null) _visionFontCombo.SelectedIndex = FindFontIndex(_visionFontCombo, _settings.VisionFontName);
        if (_visionTextColorCombo != null) _visionTextColorCombo.SelectedIndex = int.TryParse(_settings.VisionTextColor, out var tc) ? tc : 0;
        if (_visionBgColorCombo != null) _visionBgColorCombo.SelectedIndex = int.TryParse(_settings.VisionBgColor, out var bc) ? bc : 0;
        if (_visionTargetLang != null) _visionTargetLang.SelectedIndex = int.TryParse(_settings.VisionTargetLang, out var tl) ? tl : 0;
        if (_textEnableThinking != null) _textEnableThinking.SelectedIndex = _settings.EnableThinking ? 1 : 0;
        if (_textThinkingEffort != null) _textThinkingEffort.SelectedItem = _settings.ThinkingEffort ?? "medium";
        if (_textCompactPrompt != null) _textCompactPrompt.SelectedIndex = _settings.CompactPrompt ? 0 : 1;
        if (_textNoCertify != null) _textNoCertify.SelectedIndex = _settings.NoCertify ? 1 : 0;
        if (_toolsCheck != null) _toolsCheck.IsChecked = _settings.SendToolsToLocalBackend;
        if (_textAgenticWorkflow != null) _textAgenticWorkflow.SelectedIndex = string.Equals(_settings.AgenticWorkflowMode, "enable", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
        if (_maxHistoryBox != null) _maxHistoryBox.Text = _settings.MaxHistoryCount.ToString();
        if (_textHistoryBox != null) _textHistoryBox.Text = _settings.MaxHistoryCount.ToString();
        if (_textSystemPromptBox != null) _textSystemPromptBox.Text = _settings.TextSystemPrompt;
        if (_textContextSlider != null) _textContextSlider.Value = _settings.ContextSize;
        if (_textContextValue != null) _textContextValue.Text = _settings.ContextSize.ToString();
        if (_textBatchSizeBox != null) _textBatchSizeBox.Text = _settings.BatchSize.ToString();
        if (_textBlasBatchBox != null) _textBlasBatchBox.Text = _settings.BlasBatchSize.ToString();
        if (_textGpuLayersBox != null) _textGpuLayersBox.Text = _settings.GpuLayers.ToString();
        if (_textTempSlider != null) _textTempSlider.Value = _settings.TextTemperature;
        if (_textTempValue != null) _textTempValue.Text = _settings.TextTemperature.ToString("F2");
        if (_textTopPSlider != null) _textTopPSlider.Value = _settings.TextTopP;
        if (_textTopPValue != null) _textTopPValue.Text = _settings.TextTopP.ToString("F2");
        if (_textTopKSlider != null) _textTopKSlider.Value = _settings.TextTopK;
        if (_textTopKValue != null) _textTopKValue.Text = _settings.TextTopK.ToString();
        if (_textRepPenSlider != null) _textRepPenSlider.Value = _settings.TextRepeatPenalty;
        if (_textRepPenValue != null) _textRepPenValue.Text = _settings.TextRepeatPenalty.ToString("F2");
        if (_plannerModelBox != null) _plannerModelBox.Text = _settings.PlannerModelPath;
        if (_plannerTemplateBox != null) _plannerTemplateBox.Text = _settings.PlannerTemplatePath;
        if (_plannerEnabledCombo != null) _plannerEnabledCombo.SelectedIndex = _settings.PlannerEnabled ? 1 : 0;
        if (_tpsLabel != null) _tpsLabel.Visibility = _settings.ShowTps ? Visibility.Visible : Visibility.Collapsed;
    }

    private static int FindFontIndex(ComboBox combo, string name)
    {
        if (string.IsNullOrEmpty(name)) return 0;
        for (int i = 0; i < combo.Items.Count; i++)
            if (combo.Items[i]?.ToString() == name) return i;
        return 0;
    }

    private int FindColorIndex(string name)
    {
        if (string.IsNullOrEmpty(name)) return 0;
        for (int i = 0; i < _overlayColorPresets.Length; i++)
            if (_overlayColorPresets[i].Name == name) return i;
        return 0;
    }

    private static readonly string _logFilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "PromptWhizz", "app.log");
    private static readonly object _logFileLock = new();

    private void Log(string message)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => Log(message));
            return;
        }
        var line = StringExtensions.FormatLogLine(message);
        _logBox.AppendText(line);
        while (_logBox.LineCount > 100)
        {
            var idx = _logBox.Text.IndexOf('\n');
            if (idx < 0) break;
            _logBox.Text = _logBox.Text[(idx + 1)..];
        }
        _logBox.ScrollToEnd();

        // Mirror to a file that never truncates, so nothing is lost during long agent runs.
        // Only writes when explicitly enabled in settings to avoid unnecessary disk I/O.
        if (_settings.LogToFile)
        {
            try
            {
                lock (_logFileLock)
                {
                    var dir = Path.GetDirectoryName(_logFilePath);
                    if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
                    File.AppendAllText(_logFilePath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {message}\n");
                }
            }
            catch { }
        }
    }

    private void LogReplaceLast(string message)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => LogReplaceLast(message));
            return;
        }
        var text = _logBox.Text;
        var lastNewline = text.LastIndexOf('\n');
        if (lastNewline < 0)
        {
            _logBox.Text = StringExtensions.FormatLogLine(message);
        }
        else
        {
            var beforeLast = text.LastIndexOf('\n', lastNewline - 1);
            if (beforeLast >= 0)
                _logBox.Text = StringExtensions.ReplaceLastLogLine(text, beforeLast + 1, message);
            else
                _logBox.Text = StringExtensions.FormatLogLine(message);
        }
        _logBox.ScrollToEnd();
    }

    private void TryParseTps(string text)
    {
        var m = Regex.Match(text, @"(\d+\.?\d*)\s*(?:tokens?\s*per\s*second|token/s|t/sec|tok/s)", RegexOptions.IgnoreCase);
        if (m.Success)
            _tpsLabel.Content = $"{m.Groups[1].Value} t/s";
    }

    private static void SaveCrashLog(Exception ex)
    {
        try
        {
            var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "MyAiGen_crash.log");
            File.WriteAllText(path, $"MyAiGen Crash\nTime: {DateTime.Now:yyyy-MM-dd HH:mm:ss}\nOS: {Environment.OSVersion}\n\n{ex}");
        }
        catch { }
    }

    private Grid MakeSubRow(string label)
    {
        var row = new Grid { Margin = new Thickness(0, 4, 0, 4) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(80) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.Children.Add(new TextBlock { Text = label, Foreground = Fg, FontSize = 11, VerticalAlignment = VerticalAlignment.Center });
        return row;
    }
    private TextBox MakeInputRow(Panel parent, string label, string initial, Action<string> onChange)
    {
        var row = new Grid { Margin = new Thickness(0, 4, 0, 4) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(80) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.Children.Add(new TextBlock { Text = label, Foreground = Fg, FontSize = 11, VerticalAlignment = VerticalAlignment.Center });
        var box = new TextBox
        {
            Text = initial,
            Width = 80,
            Height = 22,
            HorizontalAlignment = HorizontalAlignment.Left,
            FontSize = 11,
            Background = InputBg,
            Foreground = Fg,
            BorderBrush = Border,
            CaretBrush = Fg,
            VerticalContentAlignment = VerticalAlignment.Center,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(6, 0, 0, 0)
        };
        box.TextChanged += (_, _) => onChange(box.Text);
        Grid.SetColumn(box, 1);
        row.Children.Add(box);
        parent.Children.Add(row);
        return box;
    }
}

internal sealed record DetectedFile(string Filename, string Language, string Content);