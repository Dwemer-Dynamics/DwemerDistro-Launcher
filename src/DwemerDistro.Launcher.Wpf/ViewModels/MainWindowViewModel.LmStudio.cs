using System.Windows;
using DwemerDistro.Launcher.Wpf.Services;
using MessageBox = System.Windows.MessageBox;

namespace DwemerDistro.Launcher.Wpf.ViewModels;

/// <summary>
/// The LLM Studio entry in the Official mods rail. It is not a <see cref="Models.GameProfile"/>: SelectedGame keeps
/// its last game while this panel shows. The window reports when the panel is on screen; only then is the helper's
/// read-only status polled, and never while the distro is stopped, so viewing it cannot boot WSL or start the engine.
/// Start, Stop and saving startup models act only when chosen; Force stop is recovery and stays available while an
/// action or a helper job runs.
/// </summary>
public sealed partial class MainWindowViewModel
{
    private static readonly TimeSpan LmStudioRefreshInterval = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan LmStudioReadTimeout = TimeSpan.FromSeconds(20);
    // Start may load two models (the helper waits up to 360 seconds for each); force stop is bounded at about 25 seconds.
    private static readonly TimeSpan LmStudioActionTimeout = TimeSpan.FromMinutes(20);
    private static readonly TimeSpan LmStudioForceTimeout = TimeSpan.FromSeconds(90);
    private bool _isLmStudioSelected;
    private CancellationTokenSource? _lmStudioRefreshCts;
    // Serializes status reads so a restarted loop waits until the cancelled read has finished and left the guard.
    private readonly SemaphoreSlim _lmStudioReadGate = new(1, 1);
    private bool _lmStudioInstalled;
    private string _lmStudioInstalledText = "Checking...";
    private string _lmStudioEngineText = "Checking...";
    private string _lmStudioMessage = string.Empty;
    private IReadOnlyList<LmStudioLoadedModel> _lmStudioModels = [];
    private bool _lmStudioRunning, _lmStudioJobRunning, _lmStudioActing, _lmStudioForcing, _lmStudioInventoryCached;
    // Bumped whenever an action or force stop ends: a status read that began before it is dropped, not shown.
    private int _lmStudioStatusEpoch;
    // Bumped when a force stop begins: an action it overtook does not report its own (interrupted) outcome.
    private int _lmStudioForceGeneration;
    private string _lmStudioActionText = string.Empty;
    private IReadOnlySet<string> _lmStudioLoadedKeys = new HashSet<string>();
    // The saved startup choice, read only from status. The form below may hold unsaved edits, which polling never replaces.
    private LmStudioStartup? _lmStudioSavedStartup;
    private IReadOnlyList<LmStudioModelChoice> _lmStudioStartupChoices = [];
    private bool _lmStudioAutostart, _lmStudioStartupDirty, _lmStudioApplyingStartup;
    private string _lmStudioStartupDialogue = string.Empty;
    private string _lmStudioStartupDecision = string.Empty;
    private string _lmStudioStartupNote = string.Empty;
    private string _lmStudioStartHint = string.Empty;

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
    // Engine controls stay on screen unless the engine is known to be missing, so Force stop remains reachable when status fails.
    public bool HasLmStudioEngine => !IsLmStudioMissing;

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

    /// <summary>The outcome of the last Start, Stop, Force stop or save, kept apart from the polled status message.</summary>
    public string LmStudioActionText
    {
        get => _lmStudioActionText;
        private set
        {
            if (SetProperty(ref _lmStudioActionText, value)) OnPropertyChanged(nameof(HasLmStudioActionText));
        }
    }

    public bool HasLmStudioActionText => _lmStudioActionText.Length > 0;
    public string LmStudioStartHint
    {
        get => _lmStudioStartHint;
        private set
        {
            if (SetProperty(ref _lmStudioStartHint, value)) OnPropertyChanged(nameof(HasLmStudioStartHint));
        }
    }

    public bool HasLmStudioStartHint => _lmStudioStartHint.Length > 0;
    public string LmStudioStartupNote { get => _lmStudioStartupNote; private set => SetProperty(ref _lmStudioStartupNote, value); }

    /// <summary>None, every downloaded language model, and any saved or unsaved key missing from that list.</summary>
    public IReadOnlyList<LmStudioModelChoice> LmStudioStartupChoices
    {
        get => _lmStudioStartupChoices;
        private set => SetProperty(ref _lmStudioStartupChoices, value);
    }

    public bool LmStudioAutostart
    {
        get => _lmStudioAutostart;
        set
        {
            if (SetProperty(ref _lmStudioAutostart, value)) LmStudioStartupEdited();
        }
    }

    // A ComboBox writes null while its items are replaced; that is not a choice and is ignored.
    public string? LmStudioStartupDialogue
    {
        get => _lmStudioStartupDialogue;
        set
        {
            if (value is not null && SetProperty(ref _lmStudioStartupDialogue, value)) LmStudioStartupEdited();
        }
    }

    public string? LmStudioStartupDecision
    {
        get => _lmStudioStartupDecision;
        set
        {
            if (value is not null && SetProperty(ref _lmStudioStartupDecision, value)) LmStudioStartupEdited();
        }
    }

    public AsyncRelayCommand OpenLmStudioCommand { get; private set; } = null!;
    public RelayCommand RefreshLmStudioCommand { get; private set; } = null!;
    public AsyncRelayCommand StartLmStudioCommand { get; private set; } = null!;
    public AsyncRelayCommand StopLmStudioCommand { get; private set; } = null!;
    public AsyncRelayCommand ForceStopLmStudioCommand { get; private set; } = null!;
    public AsyncRelayCommand SaveLmStudioStartupCommand { get; private set; } = null!;

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
        StartLmStudioCommand = new AsyncRelayCommand(
            () => RunLmStudioActionAsync("Starting the engine and loading the startup models...", "LLM Studio started.",
                async (service, token) => { await service.StartSavedAsync(token); return null; }),
            () => CanUseLmStudioControls() && !_lmStudioRunning);
        StopLmStudioCommand = new AsyncRelayCommand(
            () => RunLmStudioActionAsync("Stopping the engine...", "LLM Studio stopped. Its models were unloaded.",
                async (service, token) => await service.StopAsync(token) ? null : "Dwemer Distro is not running, so LLM Studio is already stopped."),
            () => CanUseLmStudioControls() && _lmStudioRunning);
        SaveLmStudioStartupCommand = new AsyncRelayCommand(SaveLmStudioStartupAsync,
            () => CanUseLmStudioControls() && _lmStudioStartupDirty && _lmStudioSavedStartup is not null && LmStudioStartupProblem() is null);
        ForceStopLmStudioCommand = new AsyncRelayCommand(ForceStopLmStudioAsync, () => !_lmStudioForcing && CanAccessDistro());
    }

    // A helper job (also one started from the LLM Studio page) blocks the normal controls; only Force stop bypasses it.
    private bool CanUseLmStudioControls() =>
        _lmStudioInstalled && !_lmStudioActing && !_lmStudioForcing && !_lmStudioJobRunning && CanAccessDistro();

    private void RaiseLmStudioCommands()
    {
        OpenLmStudioCommand.RaiseCanExecuteChanged();
        StartLmStudioCommand.RaiseCanExecuteChanged();
        StopLmStudioCommand.RaiseCanExecuteChanged();
        SaveLmStudioStartupCommand.RaiseCanExecuteChanged();
        ForceStopLmStudioCommand.RaiseCanExecuteChanged();
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
        var epoch = _lmStudioStatusEpoch;
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
            // An action that ended during this read changed the state it describes; the restarted loop reads again.
            if (epoch == _lmStudioStatusEpoch) ShowLmStudioOverview(LmStudioOverview.FromStatus(status));
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
        _lmStudioRunning = overview.Running;
        _lmStudioJobRunning = overview.JobRunning;
        _lmStudioLoadedKeys = overview.LoadedKeys;
        _lmStudioInventoryCached = overview.InventoryCached;
        _lmStudioSavedStartup = overview.Installed ? overview.Startup : null;
        ApplyLmStudioStartup(overview.Inventory);
        RaiseLmStudioCommands();
    }

    // Stale names are cleared rather than shown as if still loaded. The startup form and its unsaved edits are kept.
    private void ShowLmStudioUnavailable(string message)
    {
        SetLmStudioInstalled(false, "Unknown");
        LmStudioEngineText = "Unavailable";
        LmStudioModels = [];
        LmStudioMessage = message;
        _lmStudioRunning = _lmStudioJobRunning = false;
        _lmStudioSavedStartup = null;
        UpdateLmStudioStartupText();
        RaiseLmStudioCommands();
    }

    private void SetLmStudioInstalled(bool installed, string text)
    {
        _lmStudioInstalled = installed;
        LmStudioInstalledText = text;
        OnPropertyChanged(nameof(IsLmStudioMissing));
        OnPropertyChanged(nameof(HasLmStudioEngine));
        OpenLmStudioCommand.RaiseCanExecuteChanged();
    }

    // The saved and any unsaved choice stay listed, so a model missing from the list is neither lost nor silently replaced.
    private void ApplyLmStudioStartup(IReadOnlyList<LmStudioModelChoice> inventory)
    {
        var saved = _lmStudioSavedStartup ?? new LmStudioStartup(false, string.Empty, string.Empty);
        var choices = new List<LmStudioModelChoice> { new(string.Empty, "None") };
        choices.AddRange(inventory);
        foreach (var key in new[] { saved.Dialogue, saved.Decision, _lmStudioStartupDialogue, _lmStudioStartupDecision })
            if (key.Length > 0 && choices.All(choice => choice.Key != key))
                choices.Add(new LmStudioModelChoice(key, LmStudioChoiceName(key) is { } name && name != key ? name : key + " (not in model list)"));
        _lmStudioApplyingStartup = true;
        try
        {
            if (!choices.SequenceEqual(_lmStudioStartupChoices)) LmStudioStartupChoices = choices;
            if (!_lmStudioStartupDirty)
            {
                LmStudioAutostart = saved.Autostart;
                LmStudioStartupDialogue = saved.Dialogue;
                LmStudioStartupDecision = saved.Decision;
            }
        }
        finally
        {
            _lmStudioApplyingStartup = false;
        }
        // Replacing the items can clear a ComboBox's selection; announcing the values again restores it.
        OnPropertyChanged(nameof(LmStudioStartupDialogue));
        OnPropertyChanged(nameof(LmStudioStartupDecision));
        UpdateLmStudioStartupText();
    }

    private string? LmStudioChoiceName(string key) => _lmStudioStartupChoices.FirstOrDefault(choice => choice.Key == key)?.Name;

    private void LmStudioStartupEdited()
    {
        if (_lmStudioApplyingStartup) return;
        _lmStudioStartupDirty = true;
        UpdateLmStudioStartupText();
        SaveLmStudioStartupCommand.RaiseCanExecuteChanged();
    }

    // The same rules the helper enforces, shown before saving.
    private string? LmStudioStartupProblem() =>
        _lmStudioStartupDecision.Length > 0 && _lmStudioStartupDecision == _lmStudioStartupDialogue
            ? "Choose a different decision model, or None." : null;

    private void UpdateLmStudioStartupText()
    {
        var saved = _lmStudioSavedStartup;
        string[] savedKeys = saved is null ? [] : new[] { saved.Dialogue, saved.Decision }.Where(key => key.Length > 0).ToArray();
        if (saved is null || _lmStudioRunning)
            LmStudioStartHint = string.Empty;
        else
            LmStudioStartHint = (savedKeys.Length > 0
                ? "Start loads the saved startup models: " + string.Join(" + ", savedKeys.Select(key => LmStudioChoiceName(key) ?? key)) + "."
                : "Start loads no models: none are saved for startup.") +
                (_lmStudioStartupDirty ? " Unsaved changes below are not used until saved." : string.Empty);
        if (_lmStudioJobRunning && !_lmStudioActing)
            LmStudioStartHint = "An LLM Studio operation is running. Wait for it to finish, or use Force stop engine if it is stuck.";

        LmStudioStartupNote =
            saved is null ? "Startup models appear when LLM Studio's status is read. Unsaved choices are kept."
            : LmStudioStartupProblem() is { } problem ? problem
            : _lmStudioStartupDirty ? "Unsaved changes. Saving does not load or unload any model."
            : _lmStudioRunning && !_lmStudioLoadedKeys.SetEquals(savedKeys)
                ? "The loaded models differ from the startup models. Startup models load the next time the engine starts."
            : _lmStudioStartupChoices.Count <= 1 ? "No downloaded language models found. Download one from the LLM Studio page."
            : _lmStudioInventoryCached ? "Model list from the last check while the engine was running."
            : string.Empty;
    }

    private async Task SaveLmStudioStartupAsync()
    {
        var startup = new LmStudioStartup(_lmStudioAutostart, _lmStudioStartupDialogue, _lmStudioStartupDecision);
        await RunLmStudioActionAsync("Saving startup models...", "Startup models saved. No model was loaded or unloaded.", async (service, token) =>
        {
            await service.SaveStartupAsync(startup, token);
            _lmStudioSavedStartup = startup;
            // Edits made while the save ran stay unsaved.
            if (new LmStudioStartup(_lmStudioAutostart, _lmStudioStartupDialogue, _lmStudioStartupDecision) == startup)
                _lmStudioStartupDirty = false;
            UpdateLmStudioStartupText();
            return null;
        });
    }

    private async Task RunLmStudioActionAsync(string working, string done, Func<LmStudioService, CancellationToken, Task<string?>> action)
    {
        if (!TryBeginPassiveDistroActivity())
        {
            LmStudioActionText = "Unavailable while distro maintenance runs.";
            return;
        }

        var generation = _lmStudioForceGeneration;
        _lmStudioActing = true;
        RaiseLmStudioCommands();
        LmStudioActionText = working;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_shutdownCts.Token);
            timeout.CancelAfter(LmStudioActionTimeout);
            // An action may replace the done text with its own outcome.
            var outcome = await action(new LmStudioService(_wsl), timeout.Token);
            if (generation == _lmStudioForceGeneration) LmStudioActionText = outcome ?? done;
        }
        catch (Exception error)
        {
            // A force stop that overtook this action reports its own outcome.
            if (!_shutdownCts.IsCancellationRequested && generation == _lmStudioForceGeneration)
                LmStudioActionText = error is OperationCanceledException
                    ? "LLM Studio did not finish in time. Check its status, or use Force stop engine if it is stuck."
                    : LmStudioErrorText(error);
        }
        finally
        {
            EndPassiveDistroActivity();
            _lmStudioActing = false;
            FinishLmStudioAction();
        }
    }

    private async Task ForceStopLmStudioAsync()
    {
        var confirmed = MessageBox.Show(
            "Force stop LLM Studio now?\n\nThis immediately ends the engine and interrupts any model load, download, test or reply " +
            "being generated. Loaded models are unloaded.\n\nSettings, startup models and downloaded models are kept. Game servers are not touched.",
            "Force stop LLM Studio",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);
        if (confirmed != MessageBoxResult.Yes) return;
        if (!TryBeginPassiveDistroActivity())
        {
            LmStudioActionText = "Unavailable while distro maintenance runs.";
            return;
        }

        _lmStudioForceGeneration++;
        _lmStudioForcing = true;
        RaiseLmStudioCommands();
        LmStudioActionText = "Force stopping LLM Studio...";
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_shutdownCts.Token);
            timeout.CancelAfter(LmStudioForceTimeout);
            var (distroRunning, interrupted) = await new LmStudioService(_wsl).ForceStopAsync(timeout.Token);
            LmStudioActionText = !distroRunning ? "Dwemer Distro is not running, so LLM Studio is already stopped."
                : interrupted is { Length: > 0 }
                ? $"LLM Studio was force stopped. The running {interrupted} operation was interrupted."
                : "LLM Studio was force stopped.";
        }
        catch (Exception error)
        {
            if (!_shutdownCts.IsCancellationRequested)
                LmStudioActionText = error is OperationCanceledException
                    ? "Force stop did not answer in time. Restart Dwemer Distro if LLM Studio stays stuck."
                    : "Force stop failed: " + LmStudioErrorText(error);
        }
        finally
        {
            EndPassiveDistroActivity();
            _lmStudioForcing = false;
            FinishLmStudioAction();
        }
    }

    // Long engine output stays in the helper; the panel shows its start.
    private static string LmStudioErrorText(Exception error) =>
        error.Message.Length > 400 ? error.Message[..400].TrimEnd() + "..." : error.Message;

    // Reads that began before the action are dropped and the state is read again at once.
    private void FinishLmStudioAction()
    {
        _lmStudioStatusEpoch++;
        UpdateLmStudioStartupText();
        RaiseLmStudioCommands();
        if (_lmStudioRefreshCts is not null && !_shutdownCts.IsCancellationRequested) StartLmStudioRefresh();
    }
}
