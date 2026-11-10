using DwemerDistro.Launcher.Wpf.Services;

namespace DwemerDistro.Launcher.Wpf.ViewModels;

/// <summary>
/// The header's component status belt. It lives as long as the window, unlike the Components page, and only reads:
/// it never starts WSL to look, never runs while distro maintenance or an operation owns the distro, and shares the
/// Components page's probe gate so the two never probe at once.
/// </summary>
public sealed partial class MainWindowViewModel
{
    private static readonly TimeSpan ComponentBeltInterval = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan ComponentBeltProbeTimeout = TimeSpan.FromSeconds(12);
    private CancellationTokenSource? _componentBeltCts;
    private bool _isComponentBeltActive;
    private DateTime _serverReadyAtUtc = DateTime.MinValue;

    public ComponentStatusBeltViewModel ComponentBelt { get; } = new();

    /// <summary>Held by whichever of the Components page or the belt is probing component status.</summary>
    internal SemaphoreSlim ComponentStatusProbeGate { get; } = new(1, 1);

    /// <summary>Called by the window whenever it may have been shown, hidden or minimised; polling runs only while true.</summary>
    public void SetComponentBeltActive(bool active)
    {
        if (_isComponentBeltActive == active) return;
        _isComponentBeltActive = active;
        if (active) RequestComponentBeltRefresh();
        else StopComponentBeltRefresh();
    }

    /// <summary>Re-probes now (e.g. after Start or Stop) and resumes the regular interval from there.</summary>
    private void RequestComponentBeltRefresh()
    {
        if (!_isComponentBeltActive || _shutdownCts.IsCancellationRequested) return;
        StopComponentBeltRefresh();
        _componentBeltCts = new CancellationTokenSource();
        _ = RunComponentBeltRefreshAsync(_componentBeltCts.Token);
    }

    private void StopComponentBeltRefresh()
    {
        _componentBeltCts?.Cancel();
        _componentBeltCts?.Dispose();
        _componentBeltCts = null;
    }

    private void NoteServerRunningChangedForComponentBelt()
    {
        if (IsServerRunning) _serverReadyAtUtc = DateTime.UtcNow;
        RequestComponentBeltRefresh();
    }

    private async Task RunComponentBeltRefreshAsync(CancellationToken token)
    {
        try
        {
            while (true)
            {
                await RefreshComponentBeltAsync(token).ConfigureAwait(true);
                await Task.Delay(ComponentBeltInterval, token).ConfigureAwait(true);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
    }

    /// <summary>Every operation that changes distro files or state pauses the belt; its next tick resumes it.</summary>
    private bool IsComponentBeltPaused() =>
        IsCriticalMaintenanceInProgress || IsDistroUpdateInProgress || _isComponentsOperationInProgress
        || IsQuickstartDistroActivityInProgress || IsLiveFileMutationInProgress || _isCustomModBusy
        || ServerManagers.Any(manager => manager.IsBusy);

    private async Task RefreshComponentBeltAsync(CancellationToken token)
    {
        if (IsComponentBeltPaused())
        {
            ComponentBelt.SetAll(ComponentServiceState.Paused, "Paused while a distro operation runs.",
                "Service status is paused while a distro operation runs.");
            return;
        }

        // The Components page is probing the same services; its result is minutes fresh, so skip this tick.
        if (!await ComponentStatusProbeGate.WaitAsync(0, token).ConfigureAwait(true)) return;
        var ownsActivity = false;
        try
        {
            ownsActivity = TryBeginPassiveDistroActivity();
            if (!ownsActivity) return;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(ComponentBeltProbeTimeout);
            // Checking costs one `wsl -l --running`; a stopped distro is never started just to look.
            if (!await _wsl.DistroRunningAsync(timeout.Token).ConfigureAwait(true))
            {
                token.ThrowIfCancellationRequested();
                if (IsServerStarting)
                {
                    ComponentBelt.SetAll(ComponentServiceState.Starting, "Starting with Dwemer Distro.",
                        "Dwemer Distro is starting. Service status appears once it is running.");
                    return;
                }

                ComponentBelt.SetAll(ComponentServiceState.DistroStopped, "Not running. Dwemer Distro is stopped.",
                    "Service status appears once Dwemer Distro is running.");
                return;
            }

            var result = await _wsl.RunBashAsync(ComponentServiceStatus.BuildProbeScript(), loginShell: false, cancellationToken: timeout.Token)
                .ConfigureAwait(true);
            token.ThrowIfCancellationRequested();
            var probes = result.Succeeded ? ComponentServiceStatus.ParseProbe(result.StandardOutput) : null;
            if (probes is null)
            {
                LauncherLogService.Operation(InstallComponentsWindowViewModel.BuildComponentProbeFailure(
                    result.Succeeded ? "unreadable belt probe output" : $"belt probe exit code {result.ExitCode}", result.StandardError));
                ComponentBelt.SetAll(ComponentServiceState.Unknown, "Status check failed. It retries shortly.",
                    "Service status could not be checked. It retries shortly.");
                return;
            }

            var starting = IsServerStarting || (IsServerRunning && DateTime.UtcNow - _serverReadyAtUtc < ComponentServiceStatus.StartupGrace);
            ComponentBelt.Apply(probes, starting);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            ComponentBelt.SetAll(ComponentServiceState.Unknown, "Status check timed out. It retries shortly.",
                "Service status check timed out. It retries shortly.");
        }
        finally
        {
            if (ownsActivity) EndPassiveDistroActivity();
            ComponentStatusProbeGate.Release();
        }
    }
}
