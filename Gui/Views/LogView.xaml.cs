using System.Windows.Controls;

namespace PdfWatcher.Gui.Views;

public partial class LogView : System.Windows.Controls.UserControl
{
    private bool _autoScroll = true;

    public LogView() => InitializeComponent();

    private void LogScroller_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        // If user scrolled up, pause auto-scroll; if back at bottom, resume
        if (e.ExtentHeightChange == 0)
            _autoScroll = LogScroller.VerticalOffset >= LogScroller.ScrollableHeight - 4;

        if (_autoScroll && e.ExtentHeightChange != 0)
            LogScroller.ScrollToTop();
    }
}
