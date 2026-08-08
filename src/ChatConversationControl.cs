using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace MyAiGen;

/// <summary>
/// Reusable chat conversation panel containing a message ListBox and a files panel.
/// Used by both text and vision modes for consistent chat bubble presentation.
/// Each mode has its own input area wired externally.
/// </summary>
public sealed class ChatConversationControl
{
    public Grid Panel { get; }
    public ListBox MessageList { get; }
    public StackPanel FilesPanel { get; }
    public ScrollViewer? ScrollViewer { get; private set; }

    public ChatConversationControl(Brush bg, Brush fg)
    {
        Panel = new Grid();
        Panel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        Panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        MessageList = new ListBox
        {
            Background = bg,
            Foreground = fg,
            BorderThickness = new Thickness(0),
            Margin = new Thickness(8, 8, 8, 4),
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
        };
        // Pixel-based virtualization: only realized bubbles keep their visual tree and
        // selection-fragment captures, so long conversations stop accumulating full
        // per-message trees. CanContentScroll stays false (physical-pixel scrolling) —
        // VirtualizingPanel.ScrollUnit=Pixel is what makes virtualization work with it
        // (requires .NET 4.5+/Win10+). Containers are recycled; MarkdownView drops the
        // previous message's collapsible/parser state when it detects the recycled
        // container now holds different content (see MarkdownView.OnTextChanged).
        VirtualizingStackPanel.SetIsVirtualizing(MessageList, true);
        VirtualizingStackPanel.SetVirtualizationMode(MessageList, VirtualizationMode.Recycling);
        VirtualizingPanel.SetScrollUnit(MessageList, ScrollUnit.Pixel);
        ScrollViewer.SetHorizontalScrollBarVisibility(MessageList, ScrollBarVisibility.Disabled);
        ScrollViewer.SetCanContentScroll(MessageList, false);
        MessageList.Loaded += (_, _) => ScrollViewer ??= FindScrollViewer(MessageList);
        Panel.Children.Add(MessageList);

        FilesPanel = new StackPanel
        {
            Orientation = Orientation.Vertical,
            Margin = new Thickness(8, 0, 8, 4),
            Visibility = Visibility.Collapsed,
        };
        Grid.SetRow(FilesPanel, 1);
        Panel.Children.Add(FilesPanel);
    }

    public void ScrollToEnd()
    {
        System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvoke(new Action(() =>
        {
            if (ScrollViewer != null)
                ScrollViewer.ScrollToEnd();
            else if (MessageList.Items.Count > 0)
                MessageList.ScrollIntoView(MessageList.Items[^1]);
        }), System.Windows.Threading.DispatcherPriority.Background);
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject dep)
    {
        if (dep is ScrollViewer sv) return sv;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(dep); i++)
        {
            var child = VisualTreeHelper.GetChild(dep, i);
            var result = FindScrollViewer(child);
            if (result != null) return result;
        }
        return null;
    }
}
