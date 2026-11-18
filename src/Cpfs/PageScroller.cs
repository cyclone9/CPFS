using System.Windows.Controls;

namespace Cpfs;

// The scroller of a page that has a fixed bar under its content (Setup, Uninstall, Connections). Such a page sets
// ScrollViewer.CanContentScroll="False", so WPF-UI hosts it without its own scroller, and scrolls in this one; the
// window's wheel routing treats it as the page's scroller.
public sealed class PageScroller : ScrollViewer
{
    public PageScroller()
    {
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        Focusable = false;
    }
}
