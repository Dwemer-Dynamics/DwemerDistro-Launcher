using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;

namespace DwemerDistro.Launcher.Wpf.Views;

/// <summary>
/// The modeless progress window for a user-started upgrade: the active phase over an indeterminate bar, and the step's
/// latest real output. It never cancels anything; hiding it only closes the window. Touch it on the UI thread only.
/// </summary>
public partial class UpgradeProgressWindow : Window
{
    private const int MaxDetailLength = 240;
    private bool _closed;

    internal UpgradeProgressWindow(string title, string heading, string phase)
    {
        InitializeComponent();
        Title = title;
        HeadingTextBlock.Text = heading;
        AutomationProperties.SetName(this, title + " progress");
        SetPhase(phase);
        MaxHeight = SystemParameters.WorkArea.Height - 24;
        Closed += (_, _) => _closed = true;
        Loaded += (_, _) => HideButton.Focus();
    }

    public bool IsClosed => _closed;

    /// <summary>Shows it over the main window when that is visible, centred on screen otherwise.</summary>
    internal static UpgradeProgressWindow Open(string title, string heading, string phase)
    {
        var window = new UpgradeProgressWindow(title, heading, phase);
        if (System.Windows.Application.Current?.MainWindow is { IsVisible: true } owner) window.Owner = owner;
        else window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        window.Show();
        return window;
    }

    internal void SetPhase(string phase)
    {
        if (_closed || string.IsNullOrWhiteSpace(phase) || PhaseTextBlock.Text == phase) return;
        PhaseTextBlock.Text = phase;
        AutomationProperties.SetHelpText(ActivityProgressBar, phase);
        // The last phase's output would read as this one's.
        DetailTextBlock.Visibility = Visibility.Collapsed;
        DetailTextBlock.Text = string.Empty;
        if (AutomationPeer.ListenerExists(AutomationEvents.LiveRegionChanged))
        {
            var peer = UIElementAutomationPeer.FromElement(PhaseTextBlock) ?? UIElementAutomationPeer.CreatePeerForElement(PhaseTextBlock);
            peer?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
        }
    }

    /// <summary>One line of the step's own output; not a live region, since a package upgrade prints thousands.</summary>
    internal void SetDetail(string? detail)
    {
        if (_closed) return;
        var line = OneLine(detail);
        if (line.Length == 0) return;
        DetailTextBlock.Text = line;
        DetailTextBlock.ToolTip = line;
        AutomationProperties.SetHelpText(DetailTextBlock, line);
        DetailTextBlock.Visibility = Visibility.Visible;
    }

    /// <summary>Safe to call more than once, and after the user hid it.</summary>
    internal void CloseQuietly()
    {
        if (_closed) return;
        _closed = true;
        Close();
    }

    internal static string OneLine(string? text)
    {
        var line = (text ?? string.Empty)
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .LastOrDefault() ?? string.Empty;
        return line.Length > MaxDetailLength ? line[..MaxDetailLength] + "..." : line;
    }

    private void HideButton_Click(object sender, RoutedEventArgs e) => Close();
}
