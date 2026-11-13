using System.Windows.Threading;
using DwemerDistro.Launcher.Wpf.Services;
using DwemerDistro.Launcher.Wpf.Views;

namespace DwemerDistro.Launcher.Wpf.ViewModels;

/// <summary>
/// The progress window for the user-started upgrades: Upgrade DwemerDistro and the Database Maintenance tool. It opens
/// after the confirmation and closes before the result dialog. Reports come from any thread and are only posted, never
/// sent, so a busy or waiting UI thread can't hold up the update; bursts of output collapse into one post.
/// </summary>
public sealed partial class MainWindowViewModel
{
    private volatile UpgradeProgressWindow? _upgradeProgressWindow;
    private string? _pendingUpgradePhase;
    private string? _pendingUpgradeDetail;
    private int _upgradeProgressPostQueued;

    /// <summary>UI thread only. Replaces any window left open, so a second run never reports into the first.</summary>
    private void BeginUpgradeProgress(string title, string heading, string phase)
    {
        EndUpgradeProgress();
        try
        {
            _upgradeProgressWindow = UpgradeProgressWindow.Open(title, heading, phase);
        }
        catch (InvalidOperationException ex)
        {
            // The launcher is closing; the run itself goes on without the window.
            LauncherLogService.Operation("The upgrade progress window could not open: " + ex.Message);
        }
    }

    /// <summary>UI thread only; safe when the user already hid the window or none was opened.</summary>
    private void EndUpgradeProgress()
    {
        var window = _upgradeProgressWindow;
        _upgradeProgressWindow = null;
        Interlocked.Exchange(ref _pendingUpgradePhase, null);
        Interlocked.Exchange(ref _pendingUpgradeDetail, null);
        window?.CloseQuietly();
    }

    /// <summary>Any thread. Nothing happens unless a progress window is open.</summary>
    private void ReportUpgradeProgress(string? phase = null, string? detail = null)
    {
        if (_upgradeProgressWindow is null) return;
        if (phase is not null)
        {
            // A new phase drops output still pending from the one before it.
            Interlocked.Exchange(ref _pendingUpgradeDetail, null);
            Interlocked.Exchange(ref _pendingUpgradePhase, phase);
        }

        if (!string.IsNullOrWhiteSpace(detail)) Interlocked.Exchange(ref _pendingUpgradeDetail, detail);
        if (Interlocked.Exchange(ref _upgradeProgressPostQueued, 1) != 0) return;
        _dispatcher.BeginInvoke(DispatcherPriority.Background, FlushUpgradeProgress);
    }

    private void FlushUpgradeProgress()
    {
        // Cleared first, so a report made while this runs queues its own post.
        Volatile.Write(ref _upgradeProgressPostQueued, 0);
        var phase = Interlocked.Exchange(ref _pendingUpgradePhase, null);
        var detail = Interlocked.Exchange(ref _pendingUpgradeDetail, null);
        if (_upgradeProgressWindow is not { IsClosed: false } window) return;
        if (phase is not null) window.SetPhase(phase);
        if (detail is not null) window.SetDetail(detail);
    }
}
