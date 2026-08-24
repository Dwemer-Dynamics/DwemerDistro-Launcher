using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Data;

namespace DwemerDistro.Launcher.Wpf.Views;

public partial class DebuggingWindow : Window
{
    public DebuggingWindow()
    {
        InitializeComponent();
    }

    private void OnDashboardAutoOpenStatusUpdated(object sender, DataTransferEventArgs e)
    {
        if (sender is not UIElement element || !AutomationPeer.ListenerExists(AutomationEvents.LiveRegionChanged))
        {
            return;
        }

        UIElementAutomationPeer.CreatePeerForElement(element)
            ?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
    }
}
