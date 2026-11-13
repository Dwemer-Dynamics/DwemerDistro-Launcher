using System.Windows.Threading;
using DwemerDistro.Launcher.Wpf.Services;
using DwemerDistro.Launcher.Wpf.Views;

namespace DwemerDistro.Launcher.Wpf.ViewModels;

/// <summary>
/// The progress window for the user-started upgrades: Upgrade DwemerDistro and the Database Maintenance tool, and an
/// unfinished Debian upgrade the launcher resumes at startup. It opens after the confirmation and closes before the
/// result dialog. The run's state lives here, so hiding the window and reopening it from the top bar shows the same
/// step, detail and timers. Reports come from any thread and are only posted, never sent, so a busy or waiting UI
/// thread can't hold up the update; bursts of output collapse into one post.
/// </summary>
public sealed partial class MainWindowViewModel
{
    internal const string ShowUpgradeProgressText = "Show upgrade progress";

    // UI thread only, except the null check in ReportUpgradeProgress.
    private volatile UpgradeProgressState? _upgradeProgress;
    private UpgradeProgressWindow? _upgradeProgressWindow;
    private string? _pendingUpgradePhase;
    private string? _pendingUpgradeDetail;
    private int _upgradeProgressPostQueued;

    /// <summary>True while a run shows progress, whether or not its window is hidden; the top bar then reopens it.</summary>
    public bool IsUpgradeProgressShown => _upgradeProgress is not null;

    /// <summary>The top-bar button during a run: it only reopens the progress window and never touches the run.</summary>
    public RelayCommand ShowUpgradeProgressCommand => _showUpgradeProgressCommand ??= new RelayCommand(
        () => ShowUpgradeProgressWindow(activate: true), () => IsUpgradeProgressShown);

    private RelayCommand? _showUpgradeProgressCommand;

    /// <summary>
    /// UI thread only. Replaces any run left showing, so a second run never reports into the first. Opened without
    /// activation when the user didn't just ask for it, so a resumed upgrade never pulls focus from a game.
    /// </summary>
    private void BeginUpgradeProgress(string title, string heading, string phase, bool activate = true)
    {
        EndUpgradeProgress();
        _upgradeProgress = new UpgradeProgressState(title, heading, phase, DateTime.UtcNow);
        RaiseUpgradeProgressShownChanged();
        ShowUpgradeProgressWindow(activate);
    }

    /// <summary>UI thread only; safe when the user already hid the window or none was opened.</summary>
    private void EndUpgradeProgress()
    {
        var window = _upgradeProgressWindow;
        var wasShown = _upgradeProgress is not null;
        _upgradeProgressWindow = null;
        _upgradeProgress = null;
        Interlocked.Exchange(ref _pendingUpgradePhase, null);
        Interlocked.Exchange(ref _pendingUpgradeDetail, null);
        window?.CloseQuietly();
        if (wasShown) RaiseUpgradeProgressShownChanged();
    }

    /// <summary>UI thread only. Brings the open window forward, or opens a new one on the run's current state; never two.</summary>
    private void ShowUpgradeProgressWindow(bool activate)
    {
        if (_upgradeProgress is not { } state || _shutdownCts.IsCancellationRequested) return;
        if (_upgradeProgressWindow is { IsClosed: false } open)
        {
            if (activate) open.Activate();
            return;
        }

        try
        {
            _upgradeProgressWindow = UpgradeProgressWindow.Open(state, activate);
        }
        catch (InvalidOperationException ex)
        {
            // The launcher is closing; the run itself goes on without the window.
            _upgradeProgressWindow = null;
            LauncherLogService.Operation("The upgrade progress window could not open: " + ex.Message);
        }
    }

    private void RaiseUpgradeProgressShownChanged()
    {
        OnPropertyChanged(nameof(IsUpgradeProgressShown));
        ShowUpgradeProgressCommand.RaiseCanExecuteChanged();
        // The Upgrade DwemerDistro button reads Show upgrade progress for the run.
        OnPropertyChanged(nameof(DebianUpgradeTopBarText));
        OnPropertyChanged(nameof(DebianUpgradeAvailableText));
        OnPropertyChanged(nameof(IsDebianUpgradeBarVisible));
    }

    /// <summary>Any thread. Nothing happens unless a run shows progress; a hidden window's state still follows.</summary>
    private void ReportUpgradeProgress(string? phase = null, string? detail = null)
    {
        if (_upgradeProgress is null) return;
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
        if (_upgradeProgress is not { } state) return;
        var phaseChanged = phase is not null && state.SetPhase(phase, DateTime.UtcNow);
        if (detail is not null) state.SetDetail(detail);
        if (_upgradeProgressWindow is not { IsClosed: false } window) return;
        if (phaseChanged) window.ShowPhase();
        if (detail is not null) window.ShowDetail();
    }

    /// <summary>
    /// The plain detail for a real backup's bytes on disk, e.g. "Data backup: 6.42 GB written so far". Only a backup's
    /// own size monitor uses it; disk requirements, restore, import and verification keep their own wording.
    /// </summary>
    internal static string DescribeBackupWritten(long bytes) => "Data backup: "
        + (bytes >= 1L << 30 ? $"{bytes / (double)(1L << 30):0.00} GB" : FormatByteSize(bytes)) + " written so far";

    /// <summary>Before the backup file exists: still the backup's own state, with no size claimed.</summary>
    internal const string BackupStartingText = "Data backup: starting...";
}

/// <summary>
/// One run's progress, kept while its window is hidden: the active phase, the latest output line, and when the run
/// and the phase began, so the window shows time on this step and in total even when the step prints nothing.
/// UI thread only.
/// </summary>
internal sealed class UpgradeProgressState(string title, string heading, string phase, DateTime startedUtc)
{
    public string Title { get; } = title;
    public string Heading { get; } = heading;
    public string Phase { get; private set; } = phase;
    public string? Detail { get; private set; }
    public DateTime StartedUtc { get; } = startedUtc;
    public DateTime PhaseStartedUtc { get; private set; } = startedUtc;

    /// <summary>A new phase restarts the step timer and drops the last phase's detail; a repeat changes nothing.</summary>
    public bool SetPhase(string text, DateTime nowUtc)
    {
        text = text.Trim();
        if (text.Length == 0 || text == Phase) return false;
        Phase = text;
        PhaseStartedUtc = nowUtc;
        Detail = null;
        return true;
    }

    public void SetDetail(string text)
    {
        var line = UpgradeProgressWindow.OneLine(text);
        if (line.Length > 0) Detail = line;
    }

    /// <summary>e.g. "This step 3:12 · Total 14:05".</summary>
    public string ElapsedText(DateTime nowUtc) =>
        $"This step {FormatElapsed(nowUtc - PhaseStartedUtc)} · Total {FormatElapsed(nowUtc - StartedUtc)}";

    internal static string FormatElapsed(TimeSpan elapsed)
    {
        if (elapsed < TimeSpan.Zero) elapsed = TimeSpan.Zero;
        return elapsed.TotalHours >= 1
            ? $"{(int)elapsed.TotalHours}:{elapsed.Minutes:00}:{elapsed.Seconds:00}"
            : $"{elapsed.Minutes}:{elapsed.Seconds:00}";
    }
}
