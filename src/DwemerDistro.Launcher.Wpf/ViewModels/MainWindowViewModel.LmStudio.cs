using DwemerDistro.Launcher.Wpf.Services;

namespace DwemerDistro.Launcher.Wpf.ViewModels;

/// <summary>
/// The LLM Studio entry in the Official mods rail. It is not a <see cref="Models.GameProfile"/>: SelectedGame keeps
/// its last game while this panel shows. The window reports when the panel is on screen; only then is the helper's
/// read-only status polled, and never while the distro is stopped, so viewing it cannot boot WSL or start the engine.
/// </summary>
public sealed partial class MainWindowViewModel
{
    private static readonly TimeSpan LmStudioRefreshInterval = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan LmStudioReadTimeout = TimeSpan.FromSeconds(20);
    private bool _isLmStudioSelected;
    private CancellationTokenSource? _lmStudioRefreshCts;
    // Serializes status reads so a restarted loop waits until the cancelled read has finished and left the guard.
    private readonly SemaphoreSlim _lmStudioReadGate = new(1, 1);
    private bool _lmStudioInstalled;
    private string _lmStudioInstalledText = "Checking...";
    private string _lmStudioEngineText = "Checking...";
    private string _lmStudioMessage = string.Empty;
    private IReadOnlyList<LmStudioLoadedModel> _lmStudioModels = [];

    public bool IsLmStudioSelected
    {
        get => _isLmStudioSelected;
        set
        {
            if (SetProperty(ref _isLmStudioSelected, value))
            {
                RaiseLmStudioViewChanged();
            }
        }
    }

    public bool IsOfficialGameView => IsOfficialModsView && !_isLmStudioSelected;
    public bool IsLmStudioView => IsOfficialModsView && _isLmStudioSelected;
    public string LmStudioInstalledText { get => _lmStudioInstalledText; private set => SetProperty(ref _lmStudioInstalledText, value); }
    public string LmStudioEngineText { get => _lmStudioEngineText; private set => SetProperty(ref _lmStudioEngineText, value); }
    public bool IsLmStudioMissing => LmStudioInstalledText == "Not installed";

    public string LmStudioMessage
    {
        get => _lmStudioMessage;
        private set
        {
            if (SetProperty(ref _lmStudioMessage, value)) OnPropertyChanged(nameof(HasLmStudioMessage));
        }
    }

    public bool HasLmStudioMessage => _lmStudioMessage.Length > 0;

    public IReadOnlyList<LmStudioLoadedModel> LmStudioModels
    {
        get => _lmStudioModels;
        private set
        {
            if (SetProperty(ref _lmStudioModels, value)) OnPropertyChanged(nameof(HasLmStudioModels));
        }
    }

    public bool HasLmStudioModels => _lmStudioModels.Count > 0;
    public AsyncRelayCommand OpenLmStudioCommand { get; private set; } = null!;
    public RelayCommand RefreshLmStudioCommand { get; private set; } = null!;

    private void InitializeLmStudioCommands()
    {
        OpenLmStudioCommand = new AsyncRelayCommand(async () =>
        {
            try { await new LmStudioService(_wsl).OpenManagerAsync(); }
            // The exception can embed the tokenized page URL, so it is never shown or logged.
            catch (Exception) { LmStudioMessage = "Could not open the LLM Studio page. Make sure the server is running, then try again."; }
        }, () => _lmStudioInstalled && CanAccessDistro());
        RefreshLmStudioCommand = new RelayCommand(() =>
        {
            if (_lmStudioRefreshCts is not null) StartLmStudioRefresh();
        });
    }

    private void RaiseLmStudioViewChanged()
    {
        OnPropertyChanged(nameof(IsOfficialGameView));
        OnPropertyChanged(nameof(IsLmStudioView));
    }

    /// <summary>Called by the window whenever the panel's visibility may have changed; polling runs only while true.</summary>
    public void SetLmStudioPanelActive(bool active)
    {
        if (!active)
        {
            StopLmStudioRefresh();
        }
        else if (_lmStudioRefreshCts is null && !_shutdownCts.IsCancellationRequested)
        {
            StartLmStudioRefresh();
        }
    }

    private void StopLmStudioRefresh()
    {
        _lmStudioRefreshCts?.Cancel();
        _lmStudioRefreshCts?.Dispose();
        _lmStudioRefreshCts = null;
    }

    // One loop at a time: a restart cancels the previous read (its hidden wsl.exe is killed) and drops its result.
    private void StartLmStudioRefresh()
    {
        StopLmStudioRefresh();
        _lmStudioRefreshCts = new CancellationTokenSource();
        _ = RunLmStudioRefreshAsync(_lmStudioRefreshCts.Token);
    }

    private async Task RunLmStudioRefreshAsync(CancellationToken token)
    {
        try
        {
            while (true)
            {
                await RefreshLmStudioAsync(token);
                await Task.Delay(LmStudioRefreshInterval, token);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
    }

    // Waits for any earlier read before taking the maintenance guard, so the guard is never held while queued.
    private async Task RefreshLmStudioAsync(CancellationToken token)
    {
        await _lmStudioReadGate.WaitAsync(token);
        try
        {
            token.ThrowIfCancellationRequested();
            if (!CanAccessDistro() || !TryBeginPassiveDistroActivity())
            {
                ShowLmStudioUnavailable("Unavailable while distro maintenance runs. It refreshes when maintenance finishes.");
                return;
            }

            try
            {
                await ReadLmStudioStatusAsync(token);
            }
            finally
            {
                EndPassiveDistroActivity();
            }
        }
        finally
        {
            _lmStudioReadGate.Release();
        }
    }

    private async Task ReadLmStudioStatusAsync(CancellationToken token)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(LmStudioReadTimeout);
            if (!await _wsl.DistroRunningAsync(timeout.Token))
            {
                token.ThrowIfCancellationRequested();
                ShowLmStudioUnavailable("Dwemer Distro is not running. Start the server, then LLM Studio's status appears here.");
                return;
            }

            var status = await new LmStudioService(_wsl).ReadAsync("status", timeout.Token);
            token.ThrowIfCancellationRequested();
            ShowLmStudioOverview(LmStudioOverview.FromStatus(status));
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            ShowLmStudioUnavailable("LLM Studio did not answer in time. Select Refresh to try again.");
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            // Helper output is left out; Update Distro covers an old or missing helper.
            if (token.IsCancellationRequested) return;
            ShowLmStudioUnavailable("Could not read LLM Studio's status. Select Refresh to try again, or Update Distro if it keeps failing.");
        }
    }

    private void ShowLmStudioOverview(LmStudioOverview overview)
    {
        SetLmStudioInstalled(overview.Installed, overview.Installed ? "Installed" : "Not installed");
        LmStudioEngineText = overview.Running ? "Running" : "Stopped";
        LmStudioModels = overview.Models;
        if (!overview.Installed)
            LmStudioMessage = "LLM Studio is not installed. Set it up from Quickstart.";
        else if (!overview.Running)
            LmStudioMessage = "The engine is stopped, so no language models are loaded.";
        else
            LmStudioMessage = overview.Models.Count == 0 ? "The engine is running with no language models loaded." : string.Empty;
    }

    // Stale names are cleared rather than shown as if still loaded.
    private void ShowLmStudioUnavailable(string message)
    {
        SetLmStudioInstalled(false, "Unknown");
        LmStudioEngineText = "Unavailable";
        LmStudioModels = [];
        LmStudioMessage = message;
    }

    private void SetLmStudioInstalled(bool installed, string text)
    {
        _lmStudioInstalled = installed;
        LmStudioInstalledText = text;
        OnPropertyChanged(nameof(IsLmStudioMissing));
        OpenLmStudioCommand.RaiseCanExecuteChanged();
    }
}
