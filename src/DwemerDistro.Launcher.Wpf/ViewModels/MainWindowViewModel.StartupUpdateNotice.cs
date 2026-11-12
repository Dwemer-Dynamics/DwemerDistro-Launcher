using DwemerDistro.Launcher.Wpf.Services;

namespace DwemerDistro.Launcher.Wpf.ViewModels;

/// <summary>Why one system update run ended. Only the automatic startup sync reads more than success.</summary>
internal enum SystemUpdateRunResult
{
    /// <summary>Nothing ran: another operation held the distro, or the confirmation was declined.</summary>
    NotRun,
    Succeeded,
    Failed,
    /// <summary>Refused because another launcher's update holds the lock; nothing was changed.</summary>
    OtherUpdateRunning,
    Canceled,
    TimedOut,
    Error
}

/// <summary>What the startup update notice says. <see cref="IsWarning"/> marks an update that did finish.</summary>
internal sealed record StartupDistroUpdateNotice(string Title, string Heading, string Summary, string? Details, string NextStep, bool IsWarning)
{
    /// <summary>The whole notice as one string, for screen readers and the regression tests.</summary>
    public string ToPlainText() =>
        string.Join("\n\n", new[] { Heading, Summary, Details, NextStep }.Where(part => !string.IsNullOrWhiteSpace(part)));
}

/// <summary>
/// The one notice the automatic launcher-version sync may raise. It only explains; it never retries. Update Distro
/// in Settings stays the retry, behind the same confirmation and operation gate as always.
/// </summary>
public sealed partial class MainWindowViewModel
{
    private const int StartupNoticeErrorLimit = 200;
    private int _startupDistroUpdateNoticeRaised;

    /// <summary>Raised on the UI thread, at most once per launcher run, and never once the launcher is closing.</summary>
    internal event EventHandler<StartupDistroUpdateNotice>? StartupDistroUpdateNoticeRaised;

    /// <summary>
    /// Null when there is nothing to report: success, the launcher closing, another update already running (the
    /// assistant waits for that one), a run that never started because something else held the distro, or a Debian
    /// Trixie step that only deferred.
    /// </summary>
    internal static StartupDistroUpdateNotice? BuildStartupDistroUpdateNotice(
        SystemUpdateRunResult result,
        string? failedStage,
        string? failure,
        bool shuttingDown,
        bool markerRecorded,
        SystemUpgradeNoticeFacts? systemUpgrade = null)
    {
        if (shuttingDown)
        {
            return null;
        }

        const string title = "Distro update";
        const string retry = "Open Settings to retry, or show the console for details. The launcher will try again next time it opens.";
        switch (result)
        {
            case SystemUpdateRunResult.Succeeded when markerRecorded:
            case SystemUpdateRunResult.NotRun:
            case SystemUpdateRunResult.OtherUpdateRunning:
                return null;
            case SystemUpdateRunResult.Succeeded:
                return new StartupDistroUpdateNotice(
                    title,
                    "DwemerDistro updated, but the launcher couldn't record it",
                    "Core and shared components updated. The launcher could not save that this version finished syncing, so the same update runs again on its next start. Installed mods were not changed.",
                    null,
                    "If this keeps happening, use Generate Distro Debugging Logs on the Logs tab.",
                    IsWarning: true);
            case SystemUpdateRunResult.Failed when failedStage == UpdateStageRelease:
                return new StartupDistroUpdateNotice(
                    title,
                    "DwemerDistro updated, but its release couldn't be recorded",
                    "Core and shared components finished, but the launcher could not record the new distro release, so the update shows as failed. Installed mods were not changed.",
                    BuildStartupNoticeDetails(failure),
                    retry,
                    IsWarning: true);
            case SystemUpdateRunResult.Failed when failedStage == UpdateStageDatabase:
                return new StartupDistroUpdateNotice(
                    title,
                    "DwemerDistro's database upgrade didn't finish",
                    "Core and shared components updated, but the PostgreSQL 17 database upgrade did not finish, so the update is not recorded yet. A game that was still open postpones it; nothing in the database was changed then. Installed mods were not changed.",
                    BuildStartupNoticeDetails(failure),
                    "Close any open game, then use Update Distro in Settings. The launcher will also try again next time it opens.",
                    IsWarning: true);
            case SystemUpdateRunResult.Failed when failedStage == UpdateStageOperatingSystem:
                return BuildSystemUpgradeStartupNotice(title, failure, systemUpgrade);
            case SystemUpdateRunResult.Canceled:
            case SystemUpdateRunResult.TimedOut:
                return new StartupDistroUpdateNotice(
                    title,
                    "Automatic distro update timed out",
                    "The startup update of DwemerDistro core and shared components did not finish in time and was stopped. Installed mods were not changed.",
                    null,
                    retry,
                    IsWarning: false);
            case SystemUpdateRunResult.Error:
                return new StartupDistroUpdateNotice(
                    title,
                    "Automatic distro update failed",
                    "The launcher hit an unexpected error while updating DwemerDistro core and shared components. Installed mods were not changed.",
                    string.IsNullOrWhiteSpace(failure) ? null : "Error: " + TrimStartupNoticeText(DiagnosticEvidenceService.Sanitize(failure.Trim())),
                    retry,
                    IsWarning: false);
            default:
                return new StartupDistroUpdateNotice(
                    title,
                    "Automatic distro update failed",
                    "The startup update of DwemerDistro core and shared components did not finish. Installed mods were not changed.",
                    BuildStartupNoticeDetails(failure),
                    retry,
                    IsWarning: false);
        }
    }

    /// <summary>
    /// <see cref="DescribeSystemUpdateFailure"/> already names the stage, exit code and a redacted evidence line, and
    /// reads " during the core update (exit 1): ..."; the notice only gives it a subject.
    /// </summary>
    private static string? BuildStartupNoticeDetails(string? failure)
    {
        var text = failure?.Trim();
        return string.IsNullOrEmpty(text) ? null : "Stopped " + text;
    }

    private static string TrimStartupNoticeText(string text) =>
        text.Length > StartupNoticeErrorLimit ? text[..StartupNoticeErrorLimit] + "..." : text;

    /// <summary>Queued, never awaited, so the sync's own cleanup and the assistant's startup connection go on at once.</summary>
    private void RaiseStartupDistroUpdateNotice(StartupDistroUpdateNotice? notice)
    {
        if (notice is null
            || _shutdownCts.IsCancellationRequested
            || Interlocked.Exchange(ref _startupDistroUpdateNoticeRaised, 1) != 0)
        {
            return;
        }

        LauncherLogService.Startup($"Startup distro update notice: {notice.Heading}.");
        _ = _dispatcher.BeginInvoke(() =>
        {
            if (!_shutdownCts.IsCancellationRequested) StartupDistroUpdateNoticeRaised?.Invoke(this, notice);
        });
    }
}
