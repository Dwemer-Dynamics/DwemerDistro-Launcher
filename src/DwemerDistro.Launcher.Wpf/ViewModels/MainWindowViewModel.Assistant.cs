using System.ComponentModel;
using System.Windows;
using DwemerDistro.Launcher.Wpf.Services;
using MessageBox = System.Windows.MessageBox;

namespace DwemerDistro.Launcher.Wpf.ViewModels;

public sealed partial class MainWindowViewModel : IAssistantHost
{
    private AssistantViewModel? _assistant;
    private bool _isAssistantLiveChangeInProgress;
    // The connected session's own passive lease, apart from the short leases each assistant CLI call holds.
    private bool _isAssistantSessionLeaseHeld;

    /// <summary>Created on first use. It runs nothing until Connect, or the one startup connection below.</summary>
    public AssistantViewModel Assistant => _assistant ??= CreateAssistant();

    private AssistantViewModel CreateAssistant()
    {
        var assistant = new AssistantViewModel(new AssistantCliService(_wsl, _processRunner), this, new AssistantStateStore(), new AssistantCatalogStore());
        // Whether an idle session can be closed for maintenance follows its state, so the maintenance buttons do too.
        assistant.PropertyChanged += Assistant_PropertyChanged;
        return assistant;
    }

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
    /// tab then waits for Connect. It never switches tabs or takes focus.
    /// </summary>
    private void QueueAssistantStartupConnect()
    {
        _ = _dispatcher.BeginInvoke(() =>
        {
            if (_shutdownCts.IsCancellationRequested || IsCriticalMaintenanceInProgress || !CanRunUpdateOperation())
            {
                LauncherLogService.Startup("Assistant startup connection skipped: the launcher is closing or busy.");
                return;
            }

            _ = Assistant.ConnectOnStartupAsync();
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

    string? IAssistantHost.GetDistroInstallationId() => _wsl.GetDistroRegistrationId();

    // Only from the user's own click: the dialog is shown before anything is read or written.
    string? IAssistantHost.ChooseSavePath(string title, string fileName, string filter)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog { Title = title, FileName = fileName, Filter = filter, AddExtension = true, OverwritePrompt = true };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }
}
