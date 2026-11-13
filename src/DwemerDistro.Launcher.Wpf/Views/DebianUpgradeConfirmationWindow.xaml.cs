using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Media;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using DwemerDistro.Launcher.Wpf.Services;

namespace DwemerDistro.Launcher.Wpf.Views;

/// <summary>
/// The explicit Upgrade DwemerDistro / Later choice, with the space it needs per Windows volume and how long it
/// takes. It only confirms. Start is refused in place while a supported game or the server runs: the user closes it and
/// chooses Upgrade DwemerDistro again, so an upgrade is never queued behind a game. Without room on a volume Start stays disabled.
/// </summary>
public partial class DebianUpgradeConfirmationWindow : Window
{
    private readonly Func<IReadOnlyList<string>> _findRunningGames;
    private readonly Func<bool> _isServerRunning;
    private readonly bool _hasRoom;

    internal DebianUpgradeConfirmationWindow(
        TrixieUpgradeOffer offer, Func<IReadOnlyList<string>> findRunningGames, Func<bool> isServerRunning)
    {
        InitializeComponent();
        _findRunningGames = findRunningGames;
        _isServerRunning = isServerRunning;
        _hasRoom = offer.HasRoom;

        var volumes = (offer.Volumes ?? []).Select(VolumeRow.From).ToArray();
        VolumesItemsControl.ItemsSource = volumes;
        AutomationProperties.SetHelpText(VolumesItemsControl, string.Join(" ", volumes.Select(row => row.AccessibleText)));

        if (!_hasRoom)
        {
            StartButton.IsEnabled = false;
            var shortVolumes = string.Join(", ", volumes.Where(row => !row.HasRoom).Select(row => row.Volume));
            StartButton.ToolTip = $"Free up space on {shortVolumes} first.";
            ShowAlert($"Not enough free space on {shortVolumes}. Free up space there, then choose Upgrade DwemerDistro again. Nothing was changed.");
        }

        AutomationProperties.SetHelpText(StartButton, _hasRoom
            ? "Runs the distro update, then upgrades DwemerDistro to Debian 13. You can't play until it finishes."
            : "Unavailable: not enough free disk space.");
        AutomationProperties.SetHelpText(this, string.Join(" ", PlayTextBlock.Text, TimeTextBlock.Text));

        MaxHeight = SystemParameters.WorkArea.Height - 24;
        Loaded += (_, _) => LaterButton.Focus();
    }

    private void StartButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_hasRoom) return;
        // Checked at the moment of the click: a game opened since the dialog appeared is asked about, never waited for.
        var games = _findRunningGames();
        if (games.Count > 0)
        {
            ShowAlert($"{string.Join(", ", games)} is running. Close it, then choose Upgrade DwemerDistro again. Nothing was changed.");
            return;
        }

        if (_isServerRunning())
        {
            ShowAlert("The DwemerDistro server is running. Stop it, then choose Upgrade DwemerDistro again. Nothing was changed.");
            return;
        }

        DialogResult = true;
    }

    private void LaterButton_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void ShowAlert(string text)
    {
        AlertTextBlock.Text = text;
        AlertBorder.Visibility = Visibility.Visible;
        AlertBorder.BringIntoView();
        if (AutomationPeer.ListenerExists(AutomationEvents.LiveRegionChanged))
        {
            var peer = UIElementAutomationPeer.FromElement(AlertTextBlock) ?? UIElementAutomationPeer.CreatePeerForElement(AlertTextBlock);
            peer?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
        }
    }

    /// <summary>Public so the row template can bind to it.</summary>
    public sealed record VolumeRow(string Volume, string Description, string Verdict, Brush VerdictBrush, bool HasRoom)
    {
        public string AccessibleText => $"{Description}. {Verdict}.";
        public override string ToString() => AccessibleText;

        internal static VolumeRow From(TrixieVolumeNeed need)
        {
            var brush = System.Windows.Application.Current?.TryFindResource(need.HasRoom ? "GameCenter.TextMuted" : "GameCenter.DangerText") as Brush
                        ?? (need.HasRoom ? Brushes.Gray : Brushes.IndianRed);
            return new(need.Volume,
                $"{need.Volume} needs at least {TrixieSpacePlan.FormatGiB(need.RequiredBytes)} free; {TrixieSpacePlan.FormatGiB(need.FreeBytes)} available",
                need.HasRoom ? "Minimum met" : "Not enough space",
                brush,
                need.HasRoom);
        }
    }
}
