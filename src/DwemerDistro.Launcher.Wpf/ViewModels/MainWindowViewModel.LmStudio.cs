using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Input;
using DwemerDistro.Launcher.Wpf.Services;
using MessageBox = System.Windows.MessageBox;

namespace DwemerDistro.Launcher.Wpf.ViewModels;

/// <summary>One model slot on the LLM Studio page: the instance it holds, or why it is empty.</summary>
public sealed class LmStudioSlotItem(string title, LmStudioLoadedModel? model, string emptyText, ICommand? testCommand, ICommand? unloadCommand)
{
    public string Title { get; } = title;
    public LmStudioLoadedModel? Model { get; } = model;
    public bool IsFilled => Model is not null;
    public bool IsEmpty => Model is null;
    public string EmptyText { get; } = emptyText;
    public ICommand? TestCommand { get; } = testCommand;
    public ICommand? UnloadCommand { get; } = unloadCommand;
}

/// <summary>
/// A downloaded model in the library. MayNotFit and the Delete hints follow each status read in place, so the row is not
/// rebuilt for them.
/// </summary>
public sealed class LmStudioLibraryItem : ObservableObject
{
    private bool _mayNotFit;
    private LmStudioLoadForm? _loadForm;
    private string _deleteHint = string.Empty, _deleteNote = string.Empty;

    public LmStudioLibraryItem(LmStudioDownloadedModel model, string name, int slot, string startupRole, bool hasDefaults)
    {
        Key = model.Key;
        Name = name;
        Bytes = model.Bytes;
        Variant = model.Variant;
        Facts = string.Join(" · ", new[] { model.Variant, model.Bytes > 0 ? LocalAiModel.Gigabytes(model.Bytes) : string.Empty }.Where(part => part.Length > 0));
        Slot = slot;
        StartupRole = startupRole;
        HasDefaults = hasDefaults;
        Signature = Sign(model, name, slot, startupRole, hasDefaults);
    }

    public string Key { get; }
    public string Name { get; }
    public long Bytes { get; }
    public string Variant { get; }
    public string Facts { get; }
    public int Slot { get; }
    public bool IsLoaded => Slot != 0;
    public string LoadedText => Slot > 0 ? $"Loaded · slot {Slot}" : "Loaded";
    public string StartupRole { get; }
    public bool HasStartupRole => StartupRole.Length > 0;
    public bool HasDefaults { get; }
    public bool HasFacts => Facts.Length > 0;
    public ICommand ToggleLoadCommand { get; init; } = null!;
    public ICommand UnloadCommand { get; init; } = null!;
    public ICommand DeleteCommand { get; init; } = null!;

    public string Signature { get; }

    /// <summary>Why Delete is unavailable, or what it does; the button's tooltip and help text.</summary>
    public string DeleteHint { get => _deleteHint; set => SetProperty(ref _deleteHint, value); }

    /// <summary>A loaded or startup model's reason shown on the row itself, since a disabled button cannot be focused.</summary>
    public string DeleteNote
    {
        get => _deleteNote;
        set
        {
            if (SetProperty(ref _deleteNote, value)) OnPropertyChanged(nameof(HasDeleteNote));
        }
    }

    public bool HasDeleteNote => _deleteNote.Length > 0;

    /// <summary>The confirmation names the model, its variant and size, and that deletion is permanent.</summary>
    public string DeleteConfirmation =>
        $"Permanently delete {Name}?\n\n" +
        (Variant.Length > 0 ? $"Variant: {Variant}\n" : string.Empty) +
        $"File size: {LocalAiModel.Gigabytes(Bytes)}\n" +
        $"Model: {Key}\n\n" +
        "The model file is removed from disk and cannot be restored; download it again to use it. " +
        "Other downloaded models and their files are kept.";

    /// <summary>
    /// Why this row cannot be deleted now, or null. A loaded or saved startup model is never deleted; the helper checks
    /// the same again, and also refuses links and anything it cannot identify exactly.
    /// </summary>
    public static string? DeleteProblem(LmStudioLibraryItem item, LmStudioOverview? overview, bool busy, bool distroAvailable) =>
        overview is not { Installed: true } ? "LLM Studio's status is not known yet."
        : !overview.CanDeleteModels ? "Update Distro to delete models from here."
        : item.IsLoaded ? $"Unload {item.Name} before deleting it. Deleting never unloads a model."
        : item.HasStartupRole ? $"{item.Name} is a saved startup model. Choose another startup model or None, save, then delete it."
        : !distroAvailable ? "Unavailable while distro maintenance runs."
        : busy ? "Deleting waits until the running operation finishes."
        : !overview.Running ? "Start the engine to delete a model, so LLM Studio can confirm it is not loaded."
        : item.Bytes <= 0 ? "LLM Studio did not report this model's size. Refresh, then try again."
        : null;

    // Everything shown except MayNotFit and the open form; a status read rebuilds the list only when this changes.
    public static string Sign(LmStudioDownloadedModel model, string name, int slot, string startupRole, bool hasDefaults) =>
        string.Join("\u001f", model.Key, name, model.Variant, model.Bytes, slot, startupRole, hasDefaults);

    public bool MayNotFit { get => _mayNotFit; set => SetProperty(ref _mayNotFit, value); }

    public LmStudioLoadForm? LoadForm
    {
        get => _loadForm;
        set
        {
            if (SetProperty(ref _loadForm, value))
            {
                OnPropertyChanged(nameof(IsLoadOpen));
                OnPropertyChanged(nameof(ShowLoad));
            }
        }
    }

    public bool IsLoadOpen => _loadForm is not null;
    public bool ShowLoad => !IsLoaded && !IsLoadOpen;
}

/// <summary>
/// The inline load options for one model, seeded from its saved defaults. SDK options this form does not edit stay as
/// saved. Problems are found before anything is sent; the helper validates the same ranges again.
/// </summary>
public sealed class LmStudioLoadForm : ObservableObject
{
    private static readonly string[] Toggles = ["Default", "On", "Off"];
    private static readonly string[] CacheTypes = ["Default", "f16", "q8_0", "q4_0", "q4_1", "q5_0", "q5_1", "iq4_nl", "f32"];
    private static readonly string[] EditedAdvanced = ["flashAttention", "keepModelInMemory", "llamaKCacheQuantizationType", "llamaVCacheQuantizationType"];
    private readonly JsonObject _savedAdvanced;
    private readonly Action _changed;
    private string _context, _gpu, _idleMinutes, _flashAttention, _keepInMemory, _kCache, _vCache;
    private string? _slot;
    private IReadOnlyList<string> _slotChoices = [];
    private bool _remember;
    private string? _error;
    private string _outcome = string.Empty;

    public LmStudioLoadForm(string key, string name, LmStudioLoadDefaults? defaults, bool advancedAvailable, Action changed)
    {
        Key = key;
        Name = name;
        _changed = changed;
        AdvancedAvailable = advancedAvailable;
        _savedAdvanced = defaults?.Advanced.DeepClone().AsObject() ?? new JsonObject();
        _context = (defaults?.Context ?? 4096).ToString(CultureInfo.InvariantCulture);
        _gpu = (defaults?.Gpu ?? 100).ToString(CultureInfo.InvariantCulture);
        _idleMinutes = Math.Clamp((int)Math.Round((defaults?.Ttl ?? 600) / 60.0), 1, 1440).ToString(CultureInfo.InvariantCulture);
        _flashAttention = SavedToggle("flashAttention");
        _keepInMemory = SavedToggle("keepModelInMemory");
        _kCache = SavedCache("llamaKCacheQuantizationType");
        _vCache = SavedCache("llamaVCacheQuantizationType");
        var others = _savedAdvanced.Count(entry => !EditedAdvanced.Contains(entry.Key));
        AdvancedNote = !advancedAvailable
            ? _savedAdvanced.Count > 0
                ? "This model's saved settings use advanced options, which need the LM Studio SDK. Reinstall the LLM Studio component from Components, then retry."
                : "Advanced options need the LM Studio SDK. Reinstall the LLM Studio component from Components to add them."
            : others > 0 ? $"{others} other saved advanced setting{(others == 1 ? " is" : "s are")} kept as saved." : string.Empty;
        Validate();
    }

    public string Key { get; }
    public string Name { get; }
    public bool AdvancedAvailable { get; }
    public IReadOnlyList<string> ToggleChoices => Toggles;
    public IReadOnlyList<string> CacheChoices => CacheTypes;
    public string AdvancedNote { get; }
    public bool HasAdvancedNote => AdvancedNote.Length > 0;
    public ICommand LoadCommand { get; set; } = null!;
    public ICommand CloseCommand { get; set; } = null!;

    public string Context { get => _context; set { if (SetProperty(ref _context, value ?? string.Empty)) Validate(); } }
    public string Gpu { get => _gpu; set { if (SetProperty(ref _gpu, value ?? string.Empty)) Validate(); } }
    public string IdleMinutes { get => _idleMinutes; set { if (SetProperty(ref _idleMinutes, value ?? string.Empty)) Validate(); } }
    public string FlashAttention { get => _flashAttention; set { if (value is not null && SetProperty(ref _flashAttention, value)) Validate(); } }
    public string KeepInMemory { get => _keepInMemory; set { if (value is not null && SetProperty(ref _keepInMemory, value)) Validate(); } }
    public string KCache { get => _kCache; set { if (value is not null && SetProperty(ref _kCache, value)) Validate(); } }
    public string VCache { get => _vCache; set { if (value is not null && SetProperty(ref _vCache, value)) Validate(); } }
    public bool Remember { get => _remember; set => SetProperty(ref _remember, value); }

    /// <summary>Free slots, as "Slot N". With one free slot there is nothing to choose.</summary>
    public IReadOnlyList<string> SlotChoices
    {
        get => _slotChoices;
        private set
        {
            if (SetProperty(ref _slotChoices, value))
            {
                OnPropertyChanged(nameof(HasSlotChoice));
                OnPropertyChanged(nameof(SlotText));
            }
        }
    }

    public string? Slot { get => _slot; set { if (value is not null && SetProperty(ref _slot, value)) Validate(); } }
    public bool HasSlotChoice => _slotChoices.Count > 1;
    public string SlotText => _slotChoices.Count == 1 ? $"Loads into {_slotChoices[0].ToLowerInvariant()}." : _slotChoices.Count == 0 ? "No slot is free." : string.Empty;

    public string? Error
    {
        get => _error;
        private set
        {
            if (SetProperty(ref _error, value)) OnPropertyChanged(nameof(HasError));
        }
    }

    public bool HasError => _error is not null;

    /// <summary>The last Load's outcome, shown beside its button while the form stays open (a failed load keeps it open).</summary>
    public string Outcome
    {
        get => _outcome;
        set
        {
            if (SetProperty(ref _outcome, value)) OnPropertyChanged(nameof(HasOutcome));
        }
    }

    public bool HasOutcome => _outcome.Length > 0;

    public void SetFreeSlots(IReadOnlyList<int> free)
    {
        var choices = free.Select(n => $"Slot {n}").ToList();
        if (!choices.SequenceEqual(_slotChoices)) SlotChoices = choices;
        if (_slot is null || !choices.Contains(_slot)) _slot = choices.FirstOrDefault();
        OnPropertyChanged(nameof(Slot));
        Validate();
    }

    /// <summary>The load request (context, gpu, ttl, slot, advanced), or null with Error set.</summary>
    public JsonObject? BuildRequest()
    {
        Validate();
        if (_error is not null) return null;
        var request = new JsonObject
        {
            ["model"] = Key,
            ["context"] = Whole(_context),
            ["gpu"] = Whole(_gpu),
            ["ttl"] = Whole(_idleMinutes)!.Value * 60,
        };
        if (_slot is { } slot) request["slot"] = int.Parse(slot["Slot ".Length..], CultureInfo.InvariantCulture);
        var advanced = Advanced();
        if (advanced.Count > 0) request["advanced"] = advanced;
        return request;
    }

    private JsonObject Advanced()
    {
        var advanced = _savedAdvanced.DeepClone().AsObject();
        if (!AdvancedAvailable) return advanced;
        SetToggle(advanced, "flashAttention", _flashAttention);
        SetToggle(advanced, "keepModelInMemory", _keepInMemory);
        SetChoice(advanced, "llamaKCacheQuantizationType", _kCache);
        SetChoice(advanced, "llamaVCacheQuantizationType", _vCache);
        return advanced;
    }

    // The helper's own ranges and its quantized V cache rule.
    private void Validate()
    {
        var advanced = Advanced();
        var vCache = advanced["llamaVCacheQuantizationType"]?.GetValue<string>();
        var flash = advanced["flashAttention"] is JsonValue flag && flag.TryGetValue<bool>(out var on) && on;
        Error = Whole(_context) is not (>= 512 and <= 131072) ? "Context length must be a whole number from 512 to 131,072 tokens."
            : Whole(_gpu) is not (>= 0 and <= 100) ? "GPU offload must be a whole number from 0 to 100 percent."
            : Whole(_idleMinutes) is not (>= 1 and <= 1440) ? "Unload after idle must be from 1 to 1,440 minutes."
            : _slotChoices.Count == 0 ? "No slot is free. Unload a model first."
            : vCache is not null and not "f16" and not "f32" && !flash ? "A quantized V cache needs Flash attention set to On."
            : null;
        _changed();
    }

    private string SavedToggle(string key) =>
        _savedAdvanced[key] is JsonValue value && value.TryGetValue<bool>(out var on) ? on ? "On" : "Off" : "Default";

    private string SavedCache(string key) =>
        _savedAdvanced[key] is JsonValue value && value.TryGetValue<string>(out var type) && CacheTypes.Contains(type) ? type : "Default";

    private static void SetToggle(JsonObject advanced, string key, string choice)
    {
        if (choice == "Default") advanced.Remove(key);
        else advanced[key] = choice == "On";
    }

    private static void SetChoice(JsonObject advanced, string key, string choice)
    {
        if (choice == "Default") advanced.Remove(key);
        else advanced[key] = choice;
    }

    private static int? Whole(string text) =>
        int.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : null;
}

/// <summary>A catalog preset offered for download; Installed comes from the helper's exact file check.</summary>
public sealed class LmStudioCatalogItem(LocalAiModel model, ICommand downloadCommand)
{
    public LocalAiModel Model { get; } = model;
    public string Title => Model.Size.Length > 0 ? $"{Model.Name} · {Model.Size}" : Model.Name;
    public bool IsInstalled => Model.Installed;
    public bool CanDownload => !Model.Installed;
    public ICommand DownloadCommand { get; } = downloadCommand;
}

/// <summary>
/// The LLM Studio entry in the Official mods rail. It is not a <see cref="Models.GameProfile"/>: SelectedGame keeps
/// its last game while this panel shows. The window reports when the panel is on screen; only then is the helper's
/// read-only status polled, and never while the distro is stopped, so viewing it cannot boot WSL or start the engine.
/// Start, Stop, load, unload, test, download and saving startup models act only when chosen; Force stop is recovery and
/// stays available while an action or a helper job runs. Nothing on this page starts the engine or loads a model on its own.
/// </summary>
public sealed partial class MainWindowViewModel
{
    private static readonly TimeSpan LmStudioRefreshInterval = TimeSpan.FromSeconds(10);
    // A running helper job (a download, or a load from the full studio) is followed more closely.
    private static readonly TimeSpan LmStudioJobRefreshInterval = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan LmStudioReadTimeout = TimeSpan.FromSeconds(20);
    // Start may load two models (the helper waits up to 360 seconds for each); force stop is bounded at about 25 seconds.
    private static readonly TimeSpan LmStudioActionTimeout = TimeSpan.FromMinutes(20);
    private static readonly TimeSpan LmStudioForceTimeout = TimeSpan.FromSeconds(90);
    private const long LmStudioHeadroomBytes = 2L * 1024 * 1024 * 1024;
    private static readonly string[] LmStudioReasoning = ["Default", "Off", "On", "Low", "Medium", "High"];
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
    // The last status read that succeeded; null while the status is unknown.
    private LmStudioOverview? _lmStudioOverview;
    private string _lmStudioEngineLine = string.Empty, _lmStudioResourcesText = string.Empty, _lmStudioJobText = string.Empty;
    private int? _lmStudioJobPercent;
    private IReadOnlyList<LmStudioSlotItem> _lmStudioSlots = [];
    private IReadOnlyList<LmStudioLibraryItem> _lmStudioLibrary = [];
    private string _lmStudioLibraryNote = string.Empty;
    // At most one load form is open. It outlives list rebuilds so its edits are kept.
    private LmStudioLoadForm? _lmStudioLoadForm;
    private LmStudioLoadedModel? _lmStudioTestModel;
    private string _lmStudioTestPrompt = "Reply with one short sentence to confirm you are working.";
    private string _lmStudioTestReasoning = "Default";
    private string _lmStudioTestMaxTokens = "192";
    private string _lmStudioTestTemperature = "0.2";
    private string _lmStudioTestNote = string.Empty, _lmStudioTestOutput = string.Empty, _lmStudioTestResult = string.Empty;
    private bool _isLmStudioDownloadsExpanded, _lmStudioCatalogLoading;
    private IReadOnlyList<LmStudioCatalogItem> _lmStudioCatalogDialogue = [], _lmStudioCatalogDecision = [];
    private string _lmStudioCatalogNote = string.Empty, _lmStudioDownloadText = string.Empty;
    // The inventory the shown catalog was read against; a different inventory (a finished download) reads it again.
    private string? _lmStudioCatalogInventory;
    private int _lmStudioCatalogGeneration;
    private string? _lmStudioDownloadJobId;
    // A deleted model's key until its row leaves the library, when focus moves back into the list.
    private string? _lmStudioDeletedKey;
    // The permanent-delete question (message, title); true only when the user confirms. Cancel is the default answer.
    private Func<string, string, bool> _confirmLmStudioDelete = (message, title) =>
        MessageBox.Show(message, title, MessageBoxButton.OKCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel) == MessageBoxResult.OK;

    /// <summary>Raised when a slot's Test is chosen, so the window can move focus to the prompt.</summary>
    public event EventHandler? LmStudioTestRequested;

    /// <summary>Raised when a deleted model's row has left the library, so the window can keep keyboard focus in the list.</summary>
    public event EventHandler? LmStudioLibraryFocusRequested;

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

    /// <summary>Running, Stopped, Not installed, Unavailable or Checking...</summary>
    public string LmStudioEngineText { get => _lmStudioEngineText; private set => SetProperty(ref _lmStudioEngineText, value); }
    public bool IsLmStudioMissing => LmStudioInstalledText == "Not installed";
    // Engine controls stay on screen unless the engine is known to be missing, so Force stop remains reachable when status fails.
    public bool HasLmStudioEngine => !IsLmStudioMissing;
    // Models, library, test, downloads and connection details need a status read of an installed engine.
    public bool HasLmStudioStatus => _lmStudioOverview is { Installed: true };
    public bool IsLmStudioStatusUnknown => HasLmStudioEngine && !HasLmStudioStatus;
    public bool IsLmStudioRunning => _lmStudioRunning;
    public bool IsLmStudioStopped => HasLmStudioEngine && !_lmStudioRunning;

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
            if (SetProperty(ref _lmStudioModels, value))
            {
                OnPropertyChanged(nameof(HasLmStudioModels));
                OnPropertyChanged(nameof(HasNoLmStudioModels));
            }
        }
    }

    public bool HasLmStudioModels => _lmStudioModels.Count > 0;
    public bool HasNoLmStudioModels => _lmStudioModels.Count == 0;

    /// <summary>The outcome of the last Start, Stop, Force stop, load, unload, download or save, kept apart from the polled status message.</summary>
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

    /// <summary>"N of M model slots in use" while running, always noting the engine runs inside Dwemer Distro.</summary>
    public string LmStudioEngineLine { get => _lmStudioEngineLine; private set => SetProperty(ref _lmStudioEngineLine, value); }

    /// <summary>Free RAM, VRAM and disk as the helper measured them; empty while the status is unknown.</summary>
    public string LmStudioResourcesText
    {
        get => _lmStudioResourcesText;
        private set
        {
            if (SetProperty(ref _lmStudioResourcesText, value)) OnPropertyChanged(nameof(HasLmStudioResources));
        }
    }

    public bool HasLmStudioResources => _lmStudioResourcesText.Length > 0;

    /// <summary>The helper job running now (also one started from the full studio), with its own message.</summary>
    public string LmStudioJobText
    {
        get => _lmStudioJobText;
        private set
        {
            if (SetProperty(ref _lmStudioJobText, value)) OnPropertyChanged(nameof(HasLmStudioJob));
        }
    }

    public bool HasLmStudioJob => _lmStudioJobText.Length > 0;
    public double LmStudioJobPercent => _lmStudioJobPercent ?? 0;
    public bool IsLmStudioJobIndeterminate => _lmStudioJobPercent is null;

    public IReadOnlyList<LmStudioSlotItem> LmStudioSlots { get => _lmStudioSlots; private set => SetProperty(ref _lmStudioSlots, value); }
    public string LmStudioSlotsText => _lmStudioRunning && _lmStudioOverview is { } overview
        ? $"{_lmStudioModels.Count(m => m.Slot > 0)} of {overview.MaxLoaded} slots" : string.Empty;

    public IReadOnlyList<LmStudioLibraryItem> LmStudioLibrary
    {
        get => _lmStudioLibrary;
        private set
        {
            if (SetProperty(ref _lmStudioLibrary, value))
            {
                OnPropertyChanged(nameof(HasLmStudioLibrary));
                OnPropertyChanged(nameof(LmStudioLibraryCountText));
            }
        }
    }

    public bool HasLmStudioLibrary => _lmStudioLibrary.Count > 0;
    public string LmStudioLibraryCountText => _lmStudioLibrary.Count > 0 ? $"{_lmStudioLibrary.Count} downloaded" : string.Empty;

    /// <summary>Why Load is unavailable now, or where the list comes from; empty when loading is open.</summary>
    public string LmStudioLibraryNote
    {
        get => _lmStudioLibraryNote;
        private set
        {
            if (SetProperty(ref _lmStudioLibraryNote, value)) OnPropertyChanged(nameof(HasLmStudioLibraryNote));
        }
    }

    public bool HasLmStudioLibraryNote => _lmStudioLibraryNote.Length > 0;

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

    // Only a loaded instance can be tested; a ComboBox writes null while its items are replaced.
    public LmStudioLoadedModel? LmStudioTestModel
    {
        get => _lmStudioTestModel;
        set
        {
            if (value is not null && SetProperty(ref _lmStudioTestModel, value)) UpdateLmStudioTestNote();
        }
    }

    public string LmStudioTestPrompt
    {
        get => _lmStudioTestPrompt;
        set
        {
            if (SetProperty(ref _lmStudioTestPrompt, value ?? string.Empty))
            {
                OnPropertyChanged(nameof(LmStudioTestPromptCount));
                UpdateLmStudioTestNote();
            }
        }
    }

    public IReadOnlyList<string> LmStudioReasoningChoices => LmStudioReasoning;
    public string LmStudioTestPromptCount => $"{_lmStudioTestPrompt.Length:N0} / 4,000";

    public string? LmStudioTestReasoning
    {
        get => _lmStudioTestReasoning;
        set
        {
            if (value is not null) SetProperty(ref _lmStudioTestReasoning, value);
        }
    }

    public string LmStudioTestMaxTokens
    {
        get => _lmStudioTestMaxTokens;
        set
        {
            if (SetProperty(ref _lmStudioTestMaxTokens, value ?? string.Empty)) UpdateLmStudioTestNote();
        }
    }

    public string LmStudioTestTemperature
    {
        get => _lmStudioTestTemperature;
        set
        {
            if (SetProperty(ref _lmStudioTestTemperature, value ?? string.Empty)) UpdateLmStudioTestNote();
        }
    }

    /// <summary>Why Run test cannot run, or what it will do.</summary>
    public string LmStudioTestNote { get => _lmStudioTestNote; private set => SetProperty(ref _lmStudioTestNote, value); }

    public string LmStudioTestOutput
    {
        get => _lmStudioTestOutput;
        private set
        {
            if (SetProperty(ref _lmStudioTestOutput, value)) OnPropertyChanged(nameof(HasLmStudioTestOutput));
        }
    }

    public bool HasLmStudioTestOutput => _lmStudioTestOutput.Length > 0;

    public string LmStudioTestResult
    {
        get => _lmStudioTestResult;
        private set
        {
            if (SetProperty(ref _lmStudioTestResult, value)) OnPropertyChanged(nameof(HasLmStudioTestResult));
        }
    }

    public bool HasLmStudioTestResult => _lmStudioTestResult.Length > 0;

    /// <summary>The catalog is read the first time Recommended models opens, never on page load.</summary>
    public bool IsLmStudioDownloadsExpanded
    {
        get => _isLmStudioDownloadsExpanded;
        set
        {
            if (SetProperty(ref _isLmStudioDownloadsExpanded, value) && value && _lmStudioCatalogInventory is null) _ = LoadLmStudioCatalogAsync();
        }
    }

    public IReadOnlyList<LmStudioCatalogItem> LmStudioCatalogDialogue
    {
        get => _lmStudioCatalogDialogue;
        private set
        {
            if (SetProperty(ref _lmStudioCatalogDialogue, value)) OnPropertyChanged(nameof(HasLmStudioCatalogDialogue));
        }
    }

    public bool HasLmStudioCatalogDialogue => _lmStudioCatalogDialogue.Count > 0;

    public IReadOnlyList<LmStudioCatalogItem> LmStudioCatalogDecision
    {
        get => _lmStudioCatalogDecision;
        private set
        {
            if (SetProperty(ref _lmStudioCatalogDecision, value)) OnPropertyChanged(nameof(HasLmStudioCatalogDecision));
        }
    }

    public bool HasLmStudioCatalogDecision => _lmStudioCatalogDecision.Count > 0;

    public string LmStudioCatalogNote
    {
        get => _lmStudioCatalogNote;
        private set
        {
            if (SetProperty(ref _lmStudioCatalogNote, value)) OnPropertyChanged(nameof(HasLmStudioCatalogNote));
        }
    }

    public bool HasLmStudioCatalogNote => _lmStudioCatalogNote.Length > 0;

    /// <summary>The last download request's outcome, repeated inside Recommended models where it was chosen.</summary>
    public string LmStudioDownloadText
    {
        get => _lmStudioDownloadText;
        private set
        {
            if (SetProperty(ref _lmStudioDownloadText, value)) OnPropertyChanged(nameof(HasLmStudioDownloadText));
        }
    }

    public bool HasLmStudioDownloadText => _lmStudioDownloadText.Length > 0;
    public string LmStudioEndpoint => _lmStudioOverview?.Endpoint ?? string.Empty;
    public string LmStudioChatUrl => _lmStudioOverview?.ChatUrl ?? string.Empty;

    public AsyncRelayCommand OpenLmStudioCommand { get; private set; } = null!;
    public RelayCommand RefreshLmStudioCommand { get; private set; } = null!;
    public AsyncRelayCommand StartLmStudioCommand { get; private set; } = null!;
    public AsyncRelayCommand StopLmStudioCommand { get; private set; } = null!;
    public AsyncRelayCommand ForceStopLmStudioCommand { get; private set; } = null!;
    public AsyncRelayCommand SaveLmStudioStartupCommand { get; private set; } = null!;
    public AsyncRelayCommand RunLmStudioTestCommand { get; private set; } = null!;
    public AsyncRelayCommand ReloadLmStudioCatalogCommand { get; private set; } = null!;
    // Commands owned by slot, library, form and catalog items, raised with the page's own.
    private readonly List<ICommand> _lmStudioItemCommands = [];

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
            () => RunLmStudioActionAsync("Stopping the engine...", "LLM Studio stopped.",
                async (service, token) => await service.StopAsync(token) ? null : "Dwemer Distro is not running, so LLM Studio is already stopped."),
            () => CanUseLmStudioControls() && _lmStudioRunning);
        SaveLmStudioStartupCommand = new AsyncRelayCommand(SaveLmStudioStartupAsync,
            () => CanUseLmStudioControls() && _lmStudioStartupDirty && _lmStudioSavedStartup is not null && LmStudioStartupProblem() is null);
        ForceStopLmStudioCommand = new AsyncRelayCommand(ForceStopLmStudioAsync, () => !_lmStudioForcing && CanAccessDistro());
        RunLmStudioTestCommand = new AsyncRelayCommand(RunLmStudioTestAsync,
            () => CanUseLmStudioControls() && _lmStudioRunning && _lmStudioTestModel is not null && LmStudioTestProblem() is null);
        ReloadLmStudioCatalogCommand = new AsyncRelayCommand(LoadLmStudioCatalogAsync, () => !_lmStudioCatalogLoading && CanAccessDistro());
    }

    // A helper job (also one started from the LLM Studio page) blocks the normal controls; only Force stop bypasses it.
    private bool CanUseLmStudioControls() =>
        _lmStudioInstalled && !_lmStudioActing && !_lmStudioForcing && !_lmStudioJobRunning && CanAccessDistro();

    // Loading needs the running engine and a free slot; the helper checks both again.
    private bool CanLoadLmStudioModel() =>
        CanUseLmStudioControls() && _lmStudioRunning && _lmStudioOverview is { } overview && LmStudioFreeSlots(overview).Count > 0;

    private void RaiseLmStudioCommands()
    {
        OpenLmStudioCommand.RaiseCanExecuteChanged();
        StartLmStudioCommand.RaiseCanExecuteChanged();
        StopLmStudioCommand.RaiseCanExecuteChanged();
        SaveLmStudioStartupCommand.RaiseCanExecuteChanged();
        ForceStopLmStudioCommand.RaiseCanExecuteChanged();
        RunLmStudioTestCommand.RaiseCanExecuteChanged();
        ReloadLmStudioCatalogCommand.RaiseCanExecuteChanged();
        foreach (var command in _lmStudioItemCommands)
        {
            if (command is AsyncRelayCommand asyncCommand) asyncCommand.RaiseCanExecuteChanged();
            else if (command is RelayCommand relayCommand) relayCommand.RaiseCanExecuteChanged();
        }
        UpdateLmStudioDeleteHints();
        OnPropertyChanged(nameof(IsLmStudioRunning));
        OnPropertyChanged(nameof(IsLmStudioStopped));
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
                await Task.Delay(_lmStudioJobRunning ? LmStudioJobRefreshInterval : LmStudioRefreshInterval, token);
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

    // Projects one status read onto the page. Called on the UI thread (the refresh loop resumes on it).
    private void ShowLmStudioOverview(LmStudioOverview overview)
    {
        _lmStudioOverview = overview;
        SetLmStudioInstalled(overview.Installed, overview.Installed ? "Installed" : "Not installed");
        LmStudioEngineText = !overview.Installed ? "Not installed" : overview.Running ? "Running" : "Stopped";
        LmStudioModels = overview.Models;
        if (!overview.Installed)
            LmStudioMessage = "LLM Studio is not installed. Set it up from Quickstart.";
        else
            LmStudioMessage = overview.Running && overview.Models.Count == 0 ? "The engine is running with no language models loaded." : string.Empty;
        _lmStudioRunning = overview.Running;
        _lmStudioJobRunning = overview.JobRunning;
        _lmStudioLoadedKeys = overview.LoadedKeys;
        _lmStudioInventoryCached = overview.InventoryCached;
        _lmStudioSavedStartup = overview.Installed ? overview.Startup : null;
        ApplyLmStudioStartup(overview.Inventory);
        ShowLmStudioEngine(overview);
        // Follow the accepted download across refreshes without claiming success if another client replaced its job.
        if (_lmStudioDownloadJobId is { } downloadId && !_lmStudioActing)
        {
            if (overview.LastJobId != downloadId)
            {
                LmStudioDownloadText = "The download status changed. Check the library or full studio for its result.";
                _lmStudioDownloadJobId = null;
            }
            else if (overview.LastJobState is "completed" or "failed")
            {
                LmStudioDownloadText = overview.LastJobState == "completed"
                    ? "Download complete. The model is ready in the library; it has not been loaded."
                    : "Download failed: " + LmStudioErrorText(new InvalidOperationException(overview.LastJobMessage));
                LmStudioActionText = LmStudioDownloadText;
                _lmStudioDownloadJobId = null;
            }
        }
        ShowLmStudioSlots(overview);
        ShowLmStudioLibrary(overview);
        ShowLmStudioTestChoices(overview.Models);
        OnPropertyChanged(nameof(LmStudioEndpoint));
        OnPropertyChanged(nameof(LmStudioChatUrl));
        // A finished download changes the inventory; the open catalog then reads its Downloaded marks again.
        if (_isLmStudioDownloadsExpanded && _lmStudioCatalogInventory is { } read && read != LmStudioInventorySignature(overview) &&
            !overview.JobRunning && !_lmStudioCatalogLoading)
            _ = LoadLmStudioCatalogAsync();
        RaiseLmStudioCommands();
    }

    // Stale names are cleared rather than shown as if still loaded. The startup form and its unsaved edits are kept.
    private void ShowLmStudioUnavailable(string message)
    {
        _lmStudioOverview = null;
        SetLmStudioInstalled(false, "Unknown");
        LmStudioEngineText = "Unavailable";
        LmStudioModels = [];
        LmStudioMessage = message;
        _lmStudioRunning = _lmStudioJobRunning = false;
        _lmStudioSavedStartup = null;
        LmStudioEngineLine = "Status unknown · inside Dwemer Distro";
        LmStudioResourcesText = string.Empty;
        SetLmStudioJob(null);
        _lmStudioItemCommands.RemoveAll(command =>
            _lmStudioSlots.Any(slot => slot.TestCommand == command || slot.UnloadCommand == command) ||
            _lmStudioLibrary.Any(item => item.ToggleLoadCommand == command || item.UnloadCommand == command || item.DeleteCommand == command));
        LmStudioSlots = [];
        CloseLmStudioLoadForm();
        LmStudioLibrary = [];
        LmStudioLibraryNote = string.Empty;
        ShowLmStudioTestChoices([]);
        OnPropertyChanged(nameof(LmStudioSlotsText));
        UpdateLmStudioStartupText();
        RaiseLmStudioCommands();
    }

    private void SetLmStudioInstalled(bool installed, string text)
    {
        _lmStudioInstalled = installed;
        LmStudioInstalledText = text;
        OnPropertyChanged(nameof(IsLmStudioMissing));
        OnPropertyChanged(nameof(HasLmStudioEngine));
        OnPropertyChanged(nameof(HasLmStudioStatus));
        OnPropertyChanged(nameof(IsLmStudioStatusUnknown));
        OpenLmStudioCommand.RaiseCanExecuteChanged();
    }

    private void ShowLmStudioEngine(LmStudioOverview overview)
    {
        var used = overview.Models.Count(m => m.Slot > 0);
        LmStudioEngineLine = !overview.Installed ? "Not installed · runs inside Dwemer Distro"
            : overview.Running ? $"{used} of {overview.MaxLoaded} model slot{(overview.MaxLoaded == 1 ? string.Empty : "s")} in use · inside Dwemer Distro"
            : "Inside Dwemer Distro";
        // Measured by the helper inside the distro. It reports 0 VRAM when it cannot read the GPU, so that is not shown as free.
        LmStudioResourcesText = overview.Resources is { } r && overview.Installed
            ? string.Join("   ", new[]
            {
                r.RamTotal > 0 ? $"RAM {LocalAiModel.Gigabytes(r.RamAvailable)} free of {LocalAiModel.Gigabytes(r.RamTotal)}" : string.Empty,
                r.VramFree > 0 ? $"VRAM {LocalAiModel.Gigabytes(r.VramFree)} free" : "VRAM not reported",
                r.DiskFree > 0 ? $"Disk {LocalAiModel.Gigabytes(r.DiskFree)} free" : string.Empty,
            }.Where(part => part.Length > 0))
            : string.Empty;
        SetLmStudioJob(overview.Job);
    }

    private void SetLmStudioJob(LmStudioJob? job)
    {
        _lmStudioJobPercent = job?.Percent;
        var action = job?.Action switch
        {
            "start" => "Starting the engine",
            "stop" => "Stopping the engine",
            "restart" => "Restarting the engine",
            "load" => "Loading " + LmStudioModelName(job.Model),
            "unload" => "Unloading " + LmStudioModelName(job.Model),
            "test" => "Testing a model",
            "download" => "Downloading a model",
            "import" => "Importing a model",
            "settings" => "Saving startup models",
            "model-defaults" => "Saving load settings",
            "delete-model" => "Deleting " + LmStudioModelName(job.Model),
            null => null,
            _ => "Working",
        };
        var message = job?.Message.Trim() ?? string.Empty;
        if (message.Length > 300) message = message[..300].TrimEnd() + "...";
        LmStudioJobText = action is null ? string.Empty
            : message.Length > 0 && message != "Starting..." ? $"{action}: {message}" : action + "...";
        OnPropertyChanged(nameof(LmStudioJobPercent));
        OnPropertyChanged(nameof(IsLmStudioJobIndeterminate));
    }

    private string LmStudioModelName(string key) =>
        _lmStudioOverview?.Downloaded.FirstOrDefault(m => m.Key == key)?.Name ??
        _lmStudioModels.FirstOrDefault(m => m.Key == key || m.InstanceId == key)?.Name ?? (key.Length > 0 ? key : "a model");

    private static List<int> LmStudioFreeSlots(LmStudioOverview overview) =>
        overview.Running ? Enumerable.Range(1, overview.MaxLoaded).Where(n => overview.Models.All(m => m.Slot != n)).ToList() : [];

    private void ShowLmStudioSlots(LmStudioOverview overview)
    {
        var emptyText = overview.Running ? "Empty. Load a downloaded model from the library." : "Engine stopped.";
        var expected = overview.Installed
            ? Enumerable.Range(1, overview.MaxLoaded)
                .Select(n => (Title: $"Slot {n}", Model: overview.Models.FirstOrDefault(m => m.Slot == n), EmptyText: emptyText))
                .Concat(overview.Models.Where(m => m.Slot == 0).Select(m => (Title: "Loaded", Model: (LmStudioLoadedModel?)m, EmptyText: string.Empty)))
                .ToList()
            : [];
        // Keep focused slot buttons alive across unchanged status polls.
        if (expected.SequenceEqual(_lmStudioSlots.Select(slot => (slot.Title, slot.Model, slot.EmptyText)))) return;
        _lmStudioItemCommands.RemoveAll(command => _lmStudioSlots.Any(slot => slot.TestCommand == command || slot.UnloadCommand == command));
        var slots = new List<LmStudioSlotItem>();
        foreach (var slot in expected) slots.Add(LmStudioSlot(slot.Title, slot.Model, slot.EmptyText));
        LmStudioSlots = slots;
        OnPropertyChanged(nameof(LmStudioSlotsText));
    }

    private LmStudioSlotItem LmStudioSlot(string title, LmStudioLoadedModel? model, string emptyText)
    {
        if (model is null) return new LmStudioSlotItem(title, null, emptyText, null, null);
        var test = new RelayCommand(() =>
        {
            LmStudioTestModel = _lmStudioModels.FirstOrDefault(m => m.InstanceId == model.InstanceId);
            LmStudioTestRequested?.Invoke(this, EventArgs.Empty);
        }, () => _lmStudioModels.Any(m => m.InstanceId == model.InstanceId));
        var unload = new AsyncRelayCommand(() => UnloadLmStudioModelAsync(model),
            () => CanUseLmStudioControls() && _lmStudioRunning && _lmStudioModels.Any(current => current.InstanceId == model.InstanceId));
        _lmStudioItemCommands.Add(test);
        _lmStudioItemCommands.Add(unload);
        return new LmStudioSlotItem(title, model, emptyText, test, unload);
    }

    private void ShowLmStudioLibrary(LmStudioOverview overview)
    {
        var startup = overview.Startup;
        var items = overview.Downloaded.Select(model =>
        {
            var name = overview.Inventory.FirstOrDefault(choice => choice.Key == model.Key)?.Name ?? model.Name;
            var slot = overview.Models.FirstOrDefault(m => m.Key == model.Key) is { } loaded ? loaded.Slot > 0 ? loaded.Slot : -1 : 0;
            var role = model.Key == startup.Dialogue && model.Key == startup.Decision ? "Startup dialogue / decision"
                : model.Key == startup.Dialogue ? "Startup dialogue"
                : model.Key == startup.Decision ? "Startup decision" : string.Empty;
            return (Model: model, Name: name, Slot: slot, Role: role, Defaults: overview.Defaults.ContainsKey(model.Key));
        }).ToList();
        var signature = items.Select(i => LmStudioLibraryItem.Sign(i.Model, i.Name, i.Slot, i.Role, i.Defaults));
        if (!signature.SequenceEqual(_lmStudioLibrary.Select(item => item.Signature)))
        {
            _lmStudioItemCommands.RemoveAll(command =>
                _lmStudioLibrary.Any(item => item.ToggleLoadCommand == command || item.UnloadCommand == command || item.DeleteCommand == command));
            LmStudioLibrary = items.Select(i =>
            {
                LmStudioLibraryItem? item = null;
                var toggle = new RelayCommand(() => ToggleLmStudioLoadForm(item!), () => item!.IsLoadOpen || CanLoadLmStudioModel());
                var unload = new AsyncRelayCommand(
                    () => UnloadLmStudioModelAsync(_lmStudioModels.FirstOrDefault(model => model.Key == item!.Key)),
                    () => CanUseLmStudioControls() && _lmStudioRunning && _lmStudioModels.Any(model => model.Key == item!.Key));
                var delete = new AsyncRelayCommand(() => DeleteLmStudioModelAsync(item!), () => LmStudioDeleteProblem(item!) is null);
                item = new LmStudioLibraryItem(i.Model, i.Name, i.Slot, i.Role, i.Defaults)
                    { ToggleLoadCommand = toggle, UnloadCommand = unload, DeleteCommand = delete };
                _lmStudioItemCommands.Add(toggle);
                _lmStudioItemCommands.Add(unload);
                _lmStudioItemCommands.Add(delete);
                return item;
            }).ToList();
        }
        // The deleted row is gone: focus returns to the list instead of being lost with its button.
        if (_lmStudioDeletedKey is { } deleted && _lmStudioLibrary.All(item => item.Key != deleted))
        {
            _lmStudioDeletedKey = null;
            LmStudioLibraryFocusRequested?.Invoke(this, EventArgs.Empty);
        }
        // The helper's own fit rule, assuming full GPU offload: size against free RAM and VRAM less 2 GB headroom.
        foreach (var item in _lmStudioLibrary)
            item.MayNotFit = !item.IsLoaded && item.Bytes > 0 && overview.Resources is { RamTotal: > 0 } r &&
                             item.Bytes > r.RamAvailable + r.VramFree - LmStudioHeadroomBytes;
        // The open form follows its model; it closes when that model loads or leaves the list.
        var form = _lmStudioLoadForm;
        var host = form is null ? null : _lmStudioLibrary.FirstOrDefault(item => item.Key == form.Key && !item.IsLoaded);
        if (form is not null && host is null) CloseLmStudioLoadForm();
        else if (form is not null && host is not null)
        {
            foreach (var item in _lmStudioLibrary) item.LoadForm = item == host ? form : null;
            form.SetFreeSlots(LmStudioFreeSlots(overview));
        }

        var free = LmStudioFreeSlots(overview).Count;
        var note = !overview.Installed ? string.Empty
            : overview.Downloaded.Count == 0 ? "No downloaded language models yet. Get one from Recommended models, or from the full studio."
            : !overview.Running ? "Start the engine to load or delete a model." + (overview.InventoryCached ? " This list is from the last check while the engine was running." : string.Empty)
            : overview.JobRunning ? "Loading and deleting wait until the running operation finishes."
            : free == 0 ? $"All {overview.MaxLoaded} slot{(overview.MaxLoaded == 1 ? " is" : "s are")} in use. Unload a model to load another."
            : string.Empty;
        // An older helper has no delete job.
        if (overview.Installed && overview.Downloaded.Count > 0 && !overview.CanDeleteModels)
            note = (note + " Update Distro to delete models from here.").TrimStart();
        LmStudioLibraryNote = note;
    }

    private string? LmStudioDeleteProblem(LmStudioLibraryItem item) =>
        LmStudioLibraryItem.DeleteProblem(item, _lmStudioOverview, _lmStudioActing || _lmStudioForcing || _lmStudioJobRunning, CanAccessDistro());

    // Hints follow every state change; the row note only names a loaded or startup model's own reason.
    private void UpdateLmStudioDeleteHints()
    {
        foreach (var item in _lmStudioLibrary)
        {
            item.DeleteHint = LmStudioDeleteProblem(item) ?? $"Permanently delete {item.Name} from disk. You are asked to confirm first.";
            item.DeleteNote = _lmStudioOverview is not { CanDeleteModels: true } ? string.Empty
                : item.IsLoaded ? "Unload to delete."
                : item.HasStartupRole ? "Startup model: choose another startup model and save to delete."
                : string.Empty;
        }
    }

    // Nothing is sent unless the user confirms. Polling continues while the question is open, so the row is checked again.
    private async Task DeleteLmStudioModelAsync(LmStudioLibraryItem item)
    {
        if (LmStudioDeleteProblem(item) is { } problem)
        {
            LmStudioActionText = problem;
            return;
        }
        if (!_confirmLmStudioDelete(item.DeleteConfirmation, "Delete model")) return;
        var current = _lmStudioLibrary.FirstOrDefault(row => row.Key == item.Key && row.Bytes == item.Bytes);
        if ((current is null ? $"{item.Name} changed or left the library." : LmStudioDeleteProblem(current)) is { } changed)
        {
            LmStudioActionText = changed + " Nothing was deleted.";
            return;
        }
        if (_lmStudioLoadForm?.Key == item.Key) CloseLmStudioLoadForm();
        var deleted = false;
        await RunLmStudioActionAsync($"Deleting {item.Name}...", $"{item.Name} was deleted from disk.", async (service, token) =>
        {
            var outcome = await service.DeleteModelAsync(item.Key, item.Bytes, token);
            deleted = true;
            return outcome;
        });
        if (!deleted) return;
        _lmStudioDeletedKey = item.Key;
        // A collapsed catalog reads its Downloaded marks again when next opened; an open one follows the new inventory.
        if (!_isLmStudioDownloadsExpanded) _lmStudioCatalogInventory = null;
    }

    private void ToggleLmStudioLoadForm(LmStudioLibraryItem item)
    {
        if (item.IsLoadOpen)
        {
            CloseLmStudioLoadForm();
            return;
        }
        if (_lmStudioOverview is not { } overview || !CanLoadLmStudioModel()) return;
        CloseLmStudioLoadForm();
        LmStudioLoadForm? created = null;
        var form = created = new LmStudioLoadForm(item.Key, item.Name, overview.Defaults.GetValueOrDefault(item.Key), overview.AdvancedAvailable,
            () => (created?.LoadCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged());
        var loadCommand = new AsyncRelayCommand(() => LoadLmStudioModelAsync(form), () => !form.HasError && CanLoadLmStudioModel());
        form.LoadCommand = loadCommand;
        form.CloseCommand = new RelayCommand(CloseLmStudioLoadForm);
        _lmStudioItemCommands.Add(loadCommand);
        form.SetFreeSlots(LmStudioFreeSlots(overview));
        _lmStudioLoadForm = form;
        item.LoadForm = form;
    }

    private void CloseLmStudioLoadForm()
    {
        if (_lmStudioLoadForm is { } form) _lmStudioItemCommands.Remove(form.LoadCommand);
        _lmStudioLoadForm = null;
        foreach (var item in _lmStudioLibrary) item.LoadForm = null;
    }

    private async Task LoadLmStudioModelAsync(LmStudioLoadForm form)
    {
        if (form.BuildRequest() is not { } request) return;
        var remember = form.Remember ? request.DeepClone().AsObject() : null;
        form.Outcome = $"Loading {form.Name}...";
        await RunLmStudioActionAsync($"Loading {form.Name}...", $"{form.Name} loaded.", async (service, token) =>
        {
            var job = await service.LoadModelAsync(request, token);
            var outcome = $"{form.Name} loaded" + (job.TryGetProperty("slot", out var slot) && slot.ValueKind == JsonValueKind.Number ? $" into slot {slot.GetInt32()}." : ".");
            if (remember is not null)
            {
                try
                {
                    await service.SaveModelDefaultsAsync(remember, token);
                    outcome += " Its load settings were saved; startup uses them too.";
                }
                catch (Exception error) when (error is not OperationCanceledException)
                {
                    outcome += " Its load settings were not saved: " + LmStudioErrorText(error);
                }
            }
            if (job.TryGetProperty("memoryLockWarning", out var warning) && warning.ValueKind == JsonValueKind.String)
                outcome += " " + warning.GetString();
            if (_lmStudioLoadForm == form) CloseLmStudioLoadForm();
            return outcome;
        });
        form.Outcome = LmStudioActionText;
    }

    private Task UnloadLmStudioModelAsync(LmStudioLoadedModel? model)
    {
        if (model is null || !_lmStudioModels.Any(current => current.InstanceId == model.InstanceId))
        {
            LmStudioActionText = "This model is no longer loaded. Refresh to see its current status.";
            return Task.CompletedTask;
        }
        return RunLmStudioActionAsync($"Unloading {model.Name}...", $"{model.Name} unloaded. It stays downloaded.", async (service, token) =>
        {
            await service.UnloadModelAsync(model.InstanceId, token);
            return null;
        });
    }

    // The selection follows the instance id; a model that unloaded leaves the picker.
    private void ShowLmStudioTestChoices(IReadOnlyList<LmStudioLoadedModel> models)
    {
        var selected = _lmStudioTestModel is { } current ? models.FirstOrDefault(m => m.InstanceId == current.InstanceId) : null;
        _lmStudioTestModel = selected ?? models.FirstOrDefault();
        OnPropertyChanged(nameof(LmStudioTestModel));
        OnPropertyChanged(nameof(HasLmStudioModels));
        UpdateLmStudioTestNote();
    }

    private string? LmStudioTestProblem() =>
        _lmStudioTestPrompt.Trim().Length == 0 ? "Enter a prompt."
        : _lmStudioTestPrompt.Length > 4000 ? "The prompt can be at most 4,000 characters."
        : !int.TryParse(_lmStudioTestMaxTokens.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var tokens) || tokens is < 1 or > 4096
            ? "Max reply length must be a whole number from 1 to 4,096 tokens."
        : LmStudioTemperature() is null ? "Temperature must be a number from 0 to 1."
        : null;

    private double? LmStudioTemperature()
    {
        var text = _lmStudioTestTemperature.Trim();
        return (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ||
                double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out value)) && double.IsFinite(value) && value is >= 0 and <= 1
            ? Math.Round(value, 2) : null;
    }

    private void UpdateLmStudioTestNote()
    {
        LmStudioTestNote = _lmStudioOverview is not { Installed: true } ? string.Empty
            : !_lmStudioRunning ? "Start the engine and load a model to test it. A test never starts the engine or loads a model."
            : _lmStudioModels.Count == 0 ? "Load a model first. A test never loads a model."
            : LmStudioTestProblem() is { } problem ? problem
            : "Sends one prompt to the selected loaded model. Nothing is saved to a game.";
        RunLmStudioTestCommand?.RaiseCanExecuteChanged();
    }

    private async Task RunLmStudioTestAsync()
    {
        if (_lmStudioTestModel is not { } model || LmStudioTestProblem() is not null) return;
        var prompt = _lmStudioTestPrompt;
        var reasoning = _lmStudioTestReasoning.ToLowerInvariant();
        var tokens = int.Parse(_lmStudioTestMaxTokens.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture);
        var temperature = LmStudioTemperature()!.Value;
        var generation = _lmStudioForceGeneration;
        LmStudioTestOutput = string.Empty;
        LmStudioTestResult = $"Testing {model.Name}...";
        var answered = false;
        try
        {
            await RunLmStudioActionAsync($"Testing {model.Name}...", "Test finished.", async (service, token) =>
            {
                var (output, message, seconds) = await service.TestAsync(model.InstanceId, prompt, reasoning, tokens, temperature, token);
                LmStudioTestOutput = output;
                answered = true;
                return message + (seconds is { } s ? string.Create(CultureInfo.InvariantCulture, $" {model.Name} replied in {s:0.0} s.") : string.Empty);
            });
        }
        finally
        {
            // The outcome, or the error, also shows beside the test; a force stop reports its own.
            LmStudioTestResult = generation == _lmStudioForceGeneration || answered ? LmStudioActionText : "The test was interrupted by Force stop.";
        }
    }

    private async Task LoadLmStudioCatalogAsync()
    {
        if (_lmStudioCatalogLoading) return;
        var generation = ++_lmStudioCatalogGeneration;
        _lmStudioCatalogLoading = true;
        ReloadLmStudioCatalogCommand.RaiseCanExecuteChanged();
        LmStudioCatalogNote = "Reading the recommended models...";
        // Remembered even when the read fails, so a failure is retried by Try again, not by every status read.
        _lmStudioCatalogInventory = _lmStudioOverview is { } overview ? LmStudioInventorySignature(overview) : string.Empty;
        var ownsActivity = TryBeginPassiveDistroActivity();
        try
        {
            if (!ownsActivity)
            {
                LmStudioCatalogNote = "Unavailable while distro maintenance runs.";
                return;
            }
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_shutdownCts.Token);
            timeout.CancelAfter(LmStudioReadTimeout);
            // Reading the catalog never boots a stopped distro.
            if (!await _wsl.DistroRunningAsync(timeout.Token))
            {
                LmStudioCatalogNote = "Start Dwemer Distro to see the recommended models.";
                return;
            }
            var catalog = await new LmStudioService(_wsl).GetCatalogAsync(timeout.Token);
            if (generation != _lmStudioCatalogGeneration) return;
            _lmStudioItemCommands.RemoveAll(command => _lmStudioCatalogDialogue.Concat(_lmStudioCatalogDecision).Any(item => item.DownloadCommand == command));
            LmStudioCatalogDialogue = catalog.Dialogue.Select(LmStudioCatalogEntry).ToList();
            LmStudioCatalogDecision = catalog.Decision.Select(LmStudioCatalogEntry).ToList();
            LmStudioCatalogNote = catalog.Dialogue.Count + catalog.Decision.Count == 0
                ? "This distro's catalog lists no recommended models. Update Distro, or use the full studio."
                : string.Empty;
        }
        catch (Exception error)
        {
            if (!_shutdownCts.IsCancellationRequested && generation == _lmStudioCatalogGeneration)
                LmStudioCatalogNote = error is OperationCanceledException
                    ? "LLM Studio did not answer in time. Try again."
                    : "Could not read the recommended models. Try again, or Update Distro if it keeps failing.";
        }
        finally
        {
            if (ownsActivity) EndPassiveDistroActivity();
            _lmStudioCatalogLoading = false;
            ReloadLmStudioCatalogCommand.RaiseCanExecuteChanged();
        }
    }

    private LmStudioCatalogItem LmStudioCatalogEntry(LocalAiModel model)
    {
        var download = new AsyncRelayCommand(async () =>
            {
                LmStudioDownloadText = $"Starting the {model.Name} download...";
                await RunLmStudioActionAsync(LmStudioDownloadText,
                    $"{model.Name} is downloading. Progress shows at the top; Force stop interrupts it and a retry resumes it. Downloading does not load it.",
                    async (service, token) => { _lmStudioDownloadJobId = await service.StartPresetDownloadAsync(model.Id, token); return null; });
                LmStudioDownloadText = LmStudioActionText;
            },
            () => !model.Installed && CanUseLmStudioControls());
        _lmStudioItemCommands.Add(download);
        return new LmStudioCatalogItem(model, download);
    }

    private static string LmStudioInventorySignature(LmStudioOverview overview) => string.Join("\n", overview.Downloaded.Select(m => m.Key));

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
                : "No startup models selected.") +
                (_lmStudioStartupDirty ? " Unsaved startup changes are not used until saved." : string.Empty);
        if (_lmStudioJobRunning && !_lmStudioActing)
            LmStudioStartHint = "An LLM Studio operation is running. Wait for it to finish, or use Force stop engine from More if it is stuck.";

        LmStudioStartupNote =
            saved is null ? "Startup models appear when LLM Studio's status is read. Unsaved choices are kept."
            : LmStudioStartupProblem() is { } problem ? problem
            : _lmStudioStartupDirty ? "Unsaved changes. Saving does not load or unload any model."
            : _lmStudioRunning && !_lmStudioLoadedKeys.SetEquals(savedKeys)
                ? "The loaded models differ from the startup models. Startup models load the next time the engine starts."
            : _lmStudioStartupChoices.Count <= 1 ? "No downloaded language models found. Get one from Recommended models first."
            : _lmStudioInventoryCached ? "Model list from the last check while the engine was running."
            : "Saving changes only what loads at startup, never what is loaded now.";
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
        _lmStudioDownloadJobId = null;
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
