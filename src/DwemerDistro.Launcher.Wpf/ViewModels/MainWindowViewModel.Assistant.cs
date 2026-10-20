using DwemerDistro.Launcher.Wpf.Services;

namespace DwemerDistro.Launcher.Wpf.ViewModels;

public sealed partial class MainWindowViewModel : IAssistantHost
{
    private AssistantViewModel? _assistant;
    private bool _isAssistantLiveChangeInProgress;

    /// <summary>Created on first use. It runs nothing until Connect, or the one startup connection below.</summary>
    public AssistantViewModel Assistant => _assistant ??=
        new AssistantViewModel(new AssistantCliService(_wsl, _processRunner), this, new AssistantStateStore());

    public bool IsAssistantLiveChangeInProgress => _isAssistantLiveChangeInProgress;

    /// <summary>Custom mod mutations and assistant applies both change live files, so they share one gate.</summary>
    private bool IsLiveFileMutationInProgress => _isCustomModMutationInProgress || _isAssistantLiveChangeInProgress;

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
}
