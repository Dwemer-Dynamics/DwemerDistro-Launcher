using DwemerDistro.Launcher.Wpf.Services;

namespace DwemerDistro.Launcher.Wpf.ViewModels;

public sealed partial class MainWindowViewModel : IAssistantHost
{
    private AssistantViewModel? _assistant;
    private bool _isAssistantLiveChangeInProgress;

    /// <summary>Created on first use. It runs nothing until the user presses Connect.</summary>
    public AssistantViewModel Assistant => _assistant ??=
        new AssistantViewModel(new AssistantCliService(_wsl, _processRunner), this, new AssistantStateStore());

    public bool IsAssistantLiveChangeInProgress => _isAssistantLiveChangeInProgress;

    /// <summary>Custom mod mutations and assistant applies both change live files, so they share one gate.</summary>
    private bool IsLiveFileMutationInProgress => _isCustomModMutationInProgress || _isAssistantLiveChangeInProgress;

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
