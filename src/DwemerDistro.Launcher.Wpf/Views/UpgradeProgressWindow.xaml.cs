using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Threading;
using DwemerDistro.Launcher.Wpf.ViewModels;

namespace DwemerDistro.Launcher.Wpf.Views;

/// <summary>
/// The modeless progress window for a user-started or resumed upgrade: the active phase over an indeterminate bar, the
/// time on this step and in total, and the step's latest real output. It only shows <see cref="UpgradeProgressState"/>,
/// which outlives it, so a reopened window picks up where the hidden one was. It never cancels anything; hiding it only
/// closes the window. Touch it on the UI thread only.
/// </summary>
public partial class UpgradeProgressWindow : Window
{
    private const int MaxDetailLength = 240;
    private readonly UpgradeProgressState _state;
    private readonly DispatcherTimer _clock;
    private bool _closed;

    internal UpgradeProgressWindow(UpgradeProgressState state)
    {
        InitializeComponent();
        _state = state;
        Title = state.Title;
        HeadingTextBlock.Text = state.Heading;
        AutomationProperties.SetName(this, state.Title + " progress");
        ShowPhase();
        ShowDetail();
        MaxHeight = SystemParameters.WorkArea.Height - 24;
        // The clock runs only while this window is open; the run's start times stay in the state.
        _clock = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => ShowElapsed(), Dispatcher);
        ShowElapsed();
        Closed += (_, _) =>
        {
            _closed = true;
            _clock.Stop();
        };
        Loaded += (_, _) =>
        {
            if (ShowActivated) HideButton.Focus();
        };
    }

    public bool IsClosed => _closed;

    /// <summary>Shows it over the main window when that is visible, centred on screen otherwise.</summary>
    internal static UpgradeProgressWindow Open(UpgradeProgressState state, bool activate)
    {
        var window = new UpgradeProgressWindow(state) { ShowActivated = activate };
        if (System.Windows.Application.Current?.MainWindow is { IsVisible: true } owner) window.Owner = owner;
        else window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        window.Show();
        window._clock.Start();
        return window;
    }

    /// <summary>The state's phase; announced when it changes, and its last phase's detail is already gone.</summary>
    internal void ShowPhase()
    {
        if (_closed || PhaseTextBlock.Text == _state.Phase) return;
        var announce = PhaseTextBlock.Text.Length > 0;
        PhaseTextBlock.Text = _state.Phase;
        AutomationProperties.SetHelpText(ActivityProgressBar, _state.Phase);
        ShowDetail();
        ShowElapsed();
        if (announce && AutomationPeer.ListenerExists(AutomationEvents.LiveRegionChanged))
        {
            var peer = UIElementAutomationPeer.FromElement(PhaseTextBlock) ?? UIElementAutomationPeer.CreatePeerForElement(PhaseTextBlock);
            peer?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
        }
    }

    /// <summary>One line of the step's own output; not a live region, since a package upgrade prints thousands.</summary>
    internal void ShowDetail()
    {
        if (_closed) return;
        var line = _state.Detail ?? string.Empty;
        DetailTextBlock.Text = line;
        DetailTextBlock.ToolTip = line.Length > 0 ? line : null;
        AutomationProperties.SetHelpText(DetailTextBlock, line);
        DetailTextBlock.Visibility = line.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ShowElapsed()
    {
        if (!_closed) ElapsedTextBlock.Text = _state.ElapsedText(DateTime.UtcNow);
    }

    /// <summary>Safe to call more than once, and after the user hid it.</summary>
    internal void CloseQuietly()
    {
        if (_closed) return;
        _closed = true;
        _clock.Stop();
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
