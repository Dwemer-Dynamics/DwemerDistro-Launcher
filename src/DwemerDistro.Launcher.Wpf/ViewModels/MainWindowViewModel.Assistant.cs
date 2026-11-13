using System.ComponentModel;
using System.Windows;
using DwemerDistro.Launcher.Wpf.Models;
using DwemerDistro.Launcher.Wpf.Services;
using MessageBox = System.Windows.MessageBox;

namespace DwemerDistro.Launcher.Wpf.ViewModels;

public sealed partial class MainWindowViewModel : IAssistantHost
{
    private AssistantViewModel? _assistant;
    private bool _isAssistantLiveChangeInProgress;
    // The connected session's own passive lease, apart from the short leases each assistant CLI call holds.
    private bool _isAssistantSessionLeaseHeld;
    // The last shared update found another one holding its lock (see QueueAssistantStartupConnectAfterOtherUpdate).
    private bool _otherDistroUpdateRunning;
    // UI thread only. Held from construction, so the page never offers Connect while startup may still change the distro, until
    // ReleaseAssistantStartupHold. Startup's sync takes it over once queued (see RunFirstRunSetupStartupCheckAsync).
    private bool _isAssistantStartupHeld = true;
    private string _assistantStartupPhase = AssistantStartupPhaseChecks;
    private bool _assistantStartupSyncQueued;
    // Populated during InitializeAsync, before the startup sync is queued; later refreshes are not included.
    private readonly List<Task> _startupDataTasks = [];

    internal const string AssistantStartupPhaseChecks = "Waiting for startup checks.";
    internal const string AssistantStartupPhaseSyncCheck = "Checking whether DwemerDistro needs an update.";
    internal const string AssistantStartupPhaseOtherUpdate = "Waiting for another DwemerDistro update to finish.";
    internal const string AssistantStartupIncompleteDetail =
        "Startup checks did not finish. You can connect now; if this is a fresh install, run First-Time Setup first.";

    /// <summary>Created on first use. It runs nothing until Connect, or the one startup connection below.</summary>
    public AssistantViewModel Assistant => _assistant ??= CreateAssistant();

    private AssistantViewModel CreateAssistant()
    {
        var claudeLocator = new ClaudeCodeLocator(_processRunner);
        var assistant = new AssistantViewModel(new AssistantCliService(_wsl, _processRunner), this, new AssistantStateStore(), new AssistantCatalogStore(),
            new AssistantStateStore(fileName: AssistantStateStore.ClaudeFileName), new ClaudeChatStore(), claudeLocator.GetStatusAsync, _processRunner,
            new AssistantStateStore(fileName: AssistantStateStore.OpenRouterFileName), new OpenRouterChatStore(),
            // Key metadata only (global-status), read on Connect or when the page is shown again without a usable key; the secret stays in DwemerDistro.
            _ => CreateApiKeysService().RunAsync("global-status"),
            // Whether startup connects, and to which provider: a usable connection turns it on, the user's Disconnect off.
            new AssistantConnectionPreferenceStore());
        // Whether an idle session can be closed for maintenance follows its state, so the maintenance buttons do too.
        assistant.PropertyChanged += Assistant_PropertyChanged;
        // Before the page first binds to it: created during startup, it waits for startup like Connect does.
        if (_isAssistantStartupHeld) assistant.BeginStartupHold(_assistantStartupPhase);
        return assistant;
    }

    /// <summary>Names what startup is doing on the assistant's waiting card; ignored once the hold has ended.</summary>
    private void SetAssistantStartupPhase(string phase)
    {
        _ = _dispatcher.BeginInvoke(() =>
        {
            if (!_isAssistantStartupHeld) return;
            _assistantStartupPhase = phase;
            _assistant?.SetStartupPhase(phase);
        });
    }

    /// <summary>
    /// Ends startup's hold on the assistant, once: later calls do nothing. <paramref name="detail"/> says why startup will not
    /// connect; null when startup's connection follows. Queued on the UI thread, in order with the other startup calls.
    /// </summary>
    private void ReleaseAssistantStartupHold(string? detail)
    {
        _ = _dispatcher.BeginInvoke(() => EndAssistantStartupHold(detail));
    }

    private void EndAssistantStartupHold(string? detail)
    {
        if (!_isAssistantStartupHeld) return;
        _isAssistantStartupHeld = false;
        LauncherLogService.Startup(detail is null ? "Assistant startup hold ended." : "Assistant startup hold ended without a startup connection.");
        _assistant?.EndStartupHold(detail);
    }

    /// <summary>The waiting card's phase for an update stage marker; a later update, after the hold ended, changes nothing.</summary>
    private void SetAssistantStartupUpdateStage(string stage) => SetAssistantStartupPhase(DescribeUpdateStage(stage));

    /// <summary>An update stage marker as a phase: the assistant's waiting card and the upgrade progress window both show it.</summary>
    internal static string DescribeUpdateStage(string stage) => stage switch
    {
        UpdateStageDownload => "Updating DwemerDistro: downloading the latest distro files.",
        UpdateStageCore => "Updating DwemerDistro core.",
        UpdateStageShared => "Updating DwemerDistro's shared components.",
        UpdateStageDatabase => "Upgrading the DwemerDistro database to PostgreSQL 17.",
        UpdateStageOperatingSystem => "Upgrading DwemerDistro to Debian Trixie.",
        UpdateStageRelease => "Finishing the DwemerDistro update.",
        _ => "Preparing the DwemerDistro update."
    };

    private void Assistant_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AssistantViewModel.State)) NotifyDistroOperationGateChanged();
    }

    public bool IsAssistantLiveChangeInProgress => _isAssistantLiveChangeInProgress;
    /// <summary>Closing now would end the assistant session and stop a live command the user has not stopped.</summary>
    public bool IsAssistantLiveCommandRunning => _assistant?.BlocksSessionEnd == true;

    /// <summary>Custom mod mutations and assistant applies both change live files, so they share one gate.</summary>
    private bool IsLiveFileMutationInProgress => _isCustomModMutationInProgress || _isAssistantLiveChangeInProgress;

    /// <summary>
    /// The one passive lease an exclusive operation may look past when deciding whether it can be offered: an idle
    /// connected assistant session, which <see cref="PauseAssistantForMaintenanceAsync"/> closes before the claim.
    /// A responding, reviewing, applying or checking assistant, and every other passive activity, still block.
    /// </summary>
    private int YieldablePassiveDistroActivityCount() =>
        Volatile.Read(ref _isAssistantSessionLeaseHeld) && _assistant?.CanYieldForMaintenance == true ? 1 : 0;

    /// <summary>
    /// Closes an idle assistant session and waits for it to release the distro before Compact, Export, Import or DNS
    /// repair claims the exclusive slot. A busy assistant refuses and the operation is reported as blocked.
    /// </summary>
    private async Task<bool> PauseAssistantForMaintenanceAsync(string title)
    {
        // Some maintenance paths continue off the UI thread; the assistant's state lives on it.
        if (_assistant is not { } assistant || await _dispatcher.InvokeAsync(assistant.SuspendForMaintenanceAsync).Task.Unwrap().ConfigureAwait(true))
        {
            return true;
        }

        AppendLog(AssistantBusyForMaintenanceMessage + Environment.NewLine, "yellow");
        RunOnUi(() => MessageBox.Show(AssistantBusyForMaintenanceMessage, title, MessageBoxButton.OK, MessageBoxImage.Warning));
        return false;
    }

    internal const string AssistantBusyForMaintenanceMessage =
        "The Assistant is responding, signing in, checking, reviewing or applying changes. Let it finish or stop it on the Assistant page, then try again.";

    /// <summary>
    /// Ends the maintenance hold. The assistant reconnects only when its session was open and still wanted, and the
    /// operation finished safely; a session the user disconnected stays disconnected.
    /// </summary>
    private void ResumeAssistantAfterMaintenance(bool reconnect)
    {
        if (_assistant is { } assistant) RunOnUi(() => assistant.ResumeAfterMaintenance(reconnect));
    }

    /// <summary>
    /// Startup's one automatic Connect, queued after first-time setup was found unnecessary and the launcher-version
    /// sync settled. Skipped while the launcher closes or an update, install or maintenance holds the distro; the
    /// tab then waits for Connect. The assistant itself connects only if its last usable connection was not ended by
    /// Disconnect (see <see cref="AssistantViewModel.ConnectOnStartupAsync"/>). It never switches tabs or takes focus.
    /// </summary>
    private void QueueAssistantStartupConnect()
    {
        _ = _dispatcher.BeginInvoke(() =>
        {
            if (_systemUpgradeBlocksWrites)
            {
                LauncherLogService.Startup("Assistant startup connection skipped: the system upgrade is unfinished.");
                EndAssistantStartupHold(SystemUpgradeBlockedMessage);
                return;
            }

            if (_shutdownCts.IsCancellationRequested || IsCriticalMaintenanceInProgress || !CanRunUpdateOperation())
            {
                LauncherLogService.Startup("Assistant startup connection skipped: the launcher is closing or busy.");
                EndAssistantStartupHold("Another DwemerDistro operation is running, so the assistant did not connect automatically. Press Connect when it finishes.");
                return;
            }

            EndAssistantStartupHold(null);
            _ = Assistant.ConnectOnStartupAsync();
        });
    }

    /// <summary>
    /// Startup's connection when the sync found another update still running: it waits for that update to release
    /// its lock, then takes the same path as <see cref="QueueAssistantStartupConnect"/>, with all its checks. It holds
    /// no distro lease while it waits, and gives up (leaving Connect) if the update outlives its own time limit or the
    /// launcher closes. Connect pressed meanwhile wins: the startup connection then does nothing.
    /// </summary>
    private void QueueAssistantStartupConnectAfterOtherUpdate()
    {
        LauncherLogService.Startup("Assistant startup connection waiting: another distro update is still running.");
        SetAssistantStartupPhase(AssistantStartupPhaseOtherUpdate);
        const string otherUpdateDetail = "Another DwemerDistro update did not finish in time, so the assistant did not connect automatically. "
            + "Press Connect once it is done.";
        _ = Task.Run(async () =>
        {
            CommandResult released;
            try
            {
                released = await _wsl.RunBashAsync(DistroUpdateRunner.BuildWaitForOtherUpdateCommand(), loginShell: false,
                    cancellationToken: _shutdownCts.Token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                LauncherLogService.Startup($"Assistant startup connection skipped: waiting for the other distro update ended ({ex.GetType().Name}).");
                ReleaseAssistantStartupHold(otherUpdateDetail);
                return;
            }

            if (!released.Succeeded)
            {
                LauncherLogService.Startup($"Assistant startup connection skipped: the other distro update did not finish (exit code {released.ExitCode}).");
                ReleaseAssistantStartupHold(otherUpdateDetail);
                return;
            }

            LauncherLogService.Startup("Assistant startup connection resumed: the other distro update finished.");
            QueueAssistantStartupConnect();
        });
    }

    bool IAssistantHost.TryBeginDistroActivity() => TryBeginPassiveDistroActivity();

    void IAssistantHost.EndDistroActivity() => EndPassiveDistroActivity();

    bool IAssistantHost.TryBeginSessionActivity()
    {
        lock (_distroOperationGate)
        {
            if (_isExclusiveDistroOperationInProgress || _isAssistantSessionLeaseHeld)
            {
                return false;
            }

            _isAssistantSessionLeaseHeld = true;
            _passiveDistroActivityCount++;
        }

        NotifyDistroOperationGateChanged();
        return true;
    }

    void IAssistantHost.EndSessionActivity()
    {
        lock (_distroOperationGate)
        {
            if (!_isAssistantSessionLeaseHeld)
            {
                return;
            }

            _isAssistantSessionLeaseHeld = false;
            _passiveDistroActivityCount--;
        }

        NotifyDistroOperationGateChanged();
    }

    bool IAssistantHost.TryBeginLiveChange()
    {
        if (_isAssistantLiveChangeInProgress || !CanRunUpdateOperation())
        {
            return false;
        }

        _isAssistantLiveChangeInProgress = true;
        if (TryBeginPassiveDistroActivity())
        {
            return true;
        }

        _isAssistantLiveChangeInProgress = false;
        return false;
    }

    void IAssistantHost.EndLiveChange()
    {
        _isAssistantLiveChangeInProgress = false;
        EndPassiveDistroActivity();
    }

    void IAssistantHost.OpenUrl(string url) => _processRunner.OpenExternalUrl(url);

    /// <summary>Raised when the assistant asks for the API Keys page; the window selects its navigation entry.</summary>
    public event EventHandler? ApiKeysPageRequested;

    void IAssistantHost.OpenApiKeys() => ApiKeysPageRequested?.Invoke(this, EventArgs.Empty);

    string? IAssistantHost.GetDistroInstallationId() => _wsl.GetDistroRegistrationId();

    // Only from the user's own click: the dialog is shown before anything is read or written.
    string? IAssistantHost.ChooseSavePath(string title, string fileName, string filter)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog { Title = title, FileName = fileName, Filter = filter, AddExtension = true, OverwritePrompt = true };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }
}
