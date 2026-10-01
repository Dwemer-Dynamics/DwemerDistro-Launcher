using DwemerDistro.Launcher.Wpf.Services;

namespace DwemerDistro.Launcher.Wpf.ViewModels;

public sealed partial class MainWindowViewModel
{
    private PocketTtsConnectorService? _pocketTtsConnectorService;
    private PocketTtsConnectorResult? _startupPocketTtsResult;
    private int _startupPocketTtsHandled;
    private int _pendingVoiceApplyRunning;

    private PocketTtsConnectorService PocketTtsConnectors => _pocketTtsConnectorService ??= new PocketTtsConnectorService(_wsl);

    /// <summary>Runs the Core connector check once after an explicit install or configure action.</summary>
    internal async Task<PocketTtsConnectorResult> SyncPocketTtsConnectorsAsync(string backend)
    {
        var result = await PocketTtsConnectors.SyncAsync(backend).ConfigureAwait(true);
        AppendLog($"Pocket-TTS connectors: {result.Summary}{Environment.NewLine}", result.NeedsAttention ? "yellow" : null);
        return result;
    }

    private void ResetStartupPocketTtsState()
    {
        Volatile.Write(ref _startupPocketTtsResult, null);
        Interlocked.Exchange(ref _startupPocketTtsHandled, 0);
    }

    /// <summary>Captures the machine-readable line start_env prints; it is not shown in the console.</summary>
    private bool TryCaptureStartupPocketTtsLine(string line)
    {
        if (!PocketTtsConnectorService.TryParseStartupLine(line, out var result))
        {
            return false;
        }

        Volatile.Write(ref _startupPocketTtsResult, result);
        return true;
    }

    private void HandleServerReadyForPocketTts()
    {
        if (Interlocked.Exchange(ref _startupPocketTtsHandled, 1) == 1)
        {
            return;
        }

        // start_env already printed the readable per-game lines; only flag what needs attention.
        var result = Volatile.Read(ref _startupPocketTtsResult);
        if (result is null)
        {
            AppendLog(PocketTtsConnectorResult.CoreUpdateRequired().Message + Environment.NewLine, "yellow");
        }
        else if (result.NeedsAttention)
        {
            AppendLog($"Pocket-TTS connectors need attention: {result.Summary}{Environment.NewLine}", "yellow");
        }

        _ = RunPendingVoiceApplyAsync();
    }

    /// <summary>
    /// Runs the Quickstart voice setup saved in onboarding.json, if any; ordinary starts without a
    /// saved request never run the full setup. A stopped voice service or any error keeps the
    /// request for the next start.
    /// </summary>
    private async Task RunPendingVoiceApplyAsync()
    {
        // Server-ready callbacks can overlap; only one setup runs at a time.
        if (Interlocked.Exchange(ref _pendingVoiceApplyRunning, 1) == 1)
        {
            return;
        }

        try
        {
            var targets = await new OnboardingStateService().RunPendingVoiceApplyAsync((engineKey, backend) =>
                {
                    AppendLog("Setting up game voice connectors from Quickstart..." + Environment.NewLine);
                    return new VoiceEngineService(_wsl).ApplyVoiceEngineAsync(engineKey, backend);
                })
                .ConfigureAwait(false);
            if (targets is null)
            {
                return;
            }

            foreach (var target in targets)
            {
                var detail = string.IsNullOrWhiteSpace(target.Error) ? string.Empty : $" ({target.Error})";
                AppendLog($"  {target.TargetName}: {target.StatusText}{detail}{Environment.NewLine}", target.Applied || target.Skipped ? null : "yellow");
            }

            if (!VoiceEngineService.IsInitialApplyComplete(targets))
            {
                AppendLog("Some game voice connectors were not set up yet. They will be tried again the next time DwemerDistro starts." + Environment.NewLine, "yellow");
            }
        }
        catch (Exception ex)
        {
            LauncherLogService.Startup("Quickstart voice connector setup failed after server start.", ex);
            AppendLog("Game voice connectors could not be set up yet. They will be tried again the next time DwemerDistro starts." + Environment.NewLine, "yellow");
        }
        finally
        {
            Interlocked.Exchange(ref _pendingVoiceApplyRunning, 0);
        }
    }
}
