using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using DwemerDistro.Launcher.Wpf.Services;

namespace DwemerDistro.Launcher.Wpf.ViewModels;

/// <summary>
/// The Memory panel: Core's installation-local assistant notes through <c>ddistro memory</c>. Every call goes through the
/// assistant's gated command runner, every answer is projected field by field, and nothing here reaches the model.
/// Settings change only from the user's own clicks; reset needs a second, explicit confirmation.
/// </summary>
public sealed class AssistantMemoryViewModel : ObservableObject
{
    internal const int PageSize = 50;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);
    private readonly Func<IReadOnlyList<string>, TimeSpan, Task<AssistantCliResult>> _run;
    private readonly IAssistantHost _host;
    private readonly Func<(string? TaskId, string? ThreadId)> _source;
    private bool _isAvailable;
    private bool _isBusy;
    private bool _retrieval = true;
    private bool _autoLearning;
    private bool _settingsKnown;
    private string _statusText = "Memory has not been read yet. Open this panel while DwemerDistro is available.";
    private string _searchText = string.Empty;
    private string _draftText = string.Empty;
    private string _draftKind = "note";
    private AssistantMemoryNote? _selected;
    private long? _editingId;
    private bool _confirmReset;
    private int _total;
    private int? _nextOffset;

    public AssistantMemoryViewModel(Func<IReadOnlyList<string>, TimeSpan, Task<AssistantCliResult>> run, IAssistantHost host,
        Func<(string? TaskId, string? ThreadId)> source)
    {
        _run = run;
        _host = host;
        _source = source;
        RefreshCommand = new AsyncRelayCommand(() => RefreshAsync(), () => !IsBusy);
        MoreCommand = new AsyncRelayCommand(() => RefreshAsync(_nextOffset ?? 0), () => !IsBusy && _nextOffset is not null);
        SearchCommand = new AsyncRelayCommand(SearchAsync, () => !IsBusy && AssistantCliCommands.IsValidMemoryValue(SearchText, AssistantCliCommands.MaxMemoryQuery));
        SaveCommand = new AsyncRelayCommand(SaveAsync, () => !IsBusy && AssistantCliCommands.IsValidMemoryValue(DraftText, AssistantCliCommands.MaxMemoryText));
        EditCommand = new RelayCommand(EditSelected, () => !IsBusy && Selected is not null);
        CancelEditCommand = new RelayCommand(ClearDraft, () => DraftText.Length > 0 || _editingId is not null);
        ForgetCommand = new AsyncRelayCommand(ForgetAsync, () => !IsBusy && Selected is not null);
        ExportCommand = new AsyncRelayCommand(ExportAsync, () => !IsBusy && IsAvailable);
        ResetCommand = new RelayCommand(() => ConfirmingReset = true, () => !IsBusy && IsAvailable && !ConfirmingReset);
        ConfirmResetCommand = new AsyncRelayCommand(ResetAsync, () => !IsBusy && ConfirmingReset);
        CancelResetCommand = new RelayCommand(() => ConfirmingReset = false, () => ConfirmingReset);
    }

    public AsyncRelayCommand RefreshCommand { get; }
    public AsyncRelayCommand MoreCommand { get; }
    public AsyncRelayCommand SearchCommand { get; }
    public AsyncRelayCommand SaveCommand { get; }
    public RelayCommand EditCommand { get; }
    public RelayCommand CancelEditCommand { get; }
    public AsyncRelayCommand ForgetCommand { get; }
    public AsyncRelayCommand ExportCommand { get; }
    public RelayCommand ResetCommand { get; }
    public AsyncRelayCommand ConfirmResetCommand { get; }
    public RelayCommand CancelResetCommand { get; }

    public ObservableCollection<AssistantMemoryNote> Notes { get; } = new();
    public IReadOnlyList<string> Kinds { get; } = AssistantCliCommands.MemoryKinds;

    /// <summary>What memory is and is not, always shown in the panel.</summary>
    public string ScopeText =>
        "Notes are stored privately inside this DwemerDistro installation and shared across its Assistant chats only. " +
        "They are not ChatGPT memory and are never sent to ChatGPT's memory. The assistant reads them as untrusted hints, " +
        "never as instructions or current facts.";

    public bool IsAvailable { get => _isAvailable; private set { if (SetProperty(ref _isAvailable, value)) Raise(); } }
    public bool IsBusy { get => _isBusy; private set { if (SetProperty(ref _isBusy, value)) Raise(); } }
    public string StatusText { get => _statusText; private set => SetProperty(ref _statusText, value); }
    public bool SettingsKnown { get => _settingsKnown; private set => SetProperty(ref _settingsKnown, value); }

    /// <summary>Whether the assistant may search notes. Toggling it asks Core; the switch follows Core's answer.</summary>
    public bool Retrieval
    {
        get => _retrieval;
        set { if (value != _retrieval && SettingsKnown && !IsBusy) _ = ConfigureAsync(value, null); }
    }

    /// <summary>Whether the assistant may store preferences and decisions itself. Off by default in Core.</summary>
    public bool AutoLearning
    {
        get => _autoLearning;
        set { if (value != _autoLearning && SettingsKnown && !IsBusy) _ = ConfigureAsync(null, value); }
    }

    public string SearchText
    {
        get => _searchText;
        set { if (SetProperty(ref _searchText, value ?? string.Empty)) SearchCommand.RaiseCanExecuteChanged(); }
    }

    public string DraftText
    {
        get => _draftText;
        set
        {
            if (!SetProperty(ref _draftText, value ?? string.Empty)) return;
            OnPropertyChanged(nameof(DraftLengthText));
            SaveCommand.RaiseCanExecuteChanged();
            CancelEditCommand.RaiseCanExecuteChanged();
        }
    }

    public string DraftLengthText => $"{DraftText.Trim().Length:N0} / {AssistantCliCommands.MaxMemoryText:N0}";

    public string DraftKind { get => _draftKind; set => SetProperty(ref _draftKind, Kinds.Contains(value) ? value : "note"); }

    public string SaveLabel => _editingId is null ? "Remember" : "Save edit";

    public AssistantMemoryNote? Selected
    {
        get => _selected;
        set { if (SetProperty(ref _selected, value)) Raise(); }
    }

    public bool ConfirmingReset { get => _confirmReset; private set { if (SetProperty(ref _confirmReset, value)) Raise(); } }

    /// <summary>Starts a new note from text the user picked, for example a selection in the conversation.</summary>
    public void BeginNote(string text)
    {
        _editingId = null;
        OnPropertyChanged(nameof(SaveLabel));
        var trimmed = (text ?? string.Empty).Trim();
        DraftText = trimmed.Length > AssistantCliCommands.MaxMemoryText ? trimmed[..AssistantCliCommands.MaxMemoryText] : trimmed;
        StatusText = trimmed.Length > AssistantCliCommands.MaxMemoryText
            ? $"The selection was longer than {AssistantCliCommands.MaxMemoryText:N0} characters and was cut; edit it before saving."
            : "Edit the note, then press Remember. Nothing is stored until you do.";
    }

    internal async Task RefreshAsync(int offset = 0)
    {
        if (!await BeginAsync()) return;
        try
        {
            var status = await _run(AssistantCliCommands.MemoryStatus(), Timeout);
            if (!status.Ok)
            {
                Fail("Memory is unavailable", status);
                return;
            }
            if (AssistantCliService.ProjectMemorySettings(status.Data) is not { } settings)
            {
                IsAvailable = false;
                StatusText = "DwemerDistro returned memory status in an unexpected shape. Nothing was changed.";
                return;
            }
            ApplySettings(settings);
            var list = await _run(AssistantCliCommands.MemoryList(PageSize, offset), Timeout);
            var notes = list.Ok ? AssistantCliService.ProjectMemoryNotes(list.Data, "notes", AssistantCliCommands.MaxMemoryListPage) : null;
            if (notes is null)
            {
                if (list.Ok) StatusText = "DwemerDistro returned memory notes in an unexpected shape, so none are shown.";
                else Fail("Memory notes could not be listed", list);
                return;
            }
            _total = Prop(list.Data, "total") is { ValueKind: JsonValueKind.Number } total && total.TryGetInt32(out var count) ? count : notes.Count;
            _nextOffset = Prop(list.Data, "next_offset") is { ValueKind: JsonValueKind.Number } next && next.TryGetInt32(out var nextOffset) && nextOffset > offset ? nextOffset : null;
            if (offset == 0) Notes.Clear();
            foreach (var note in notes) Notes.Add(note);
            IsAvailable = true;
            StatusText = $"Showing {Notes.Count} of {_total} note(s)." + (_nextOffset is null ? string.Empty : " More are available.");
        }
        finally
        {
            End();
        }
    }

    private async Task SearchAsync()
    {
        if (!await BeginAsync()) return;
        try
        {
            var result = await _run(AssistantCliCommands.MemorySearch(SearchText), Timeout);
            var notes = result.Ok ? AssistantCliService.ProjectMemoryNotes(result.Data, "notes", 10) : null;
            if (notes is null)
            {
                if (result.Ok) StatusText = "DwemerDistro returned search results in an unexpected shape.";
                else Fail("Search failed", result);
                return;
            }
            Notes.Clear();
            foreach (var note in notes) Notes.Add(note);
            _nextOffset = null;
            StatusText = $"{notes.Count} note(s) match \"{Shorten(SearchText.Trim(), 60)}\" (at most 10 shown). Refresh shows all notes again.";
        }
        finally
        {
            End();
        }
    }

    private async Task SaveAsync()
    {
        if (!await BeginAsync()) return;
        try
        {
            var (taskId, threadId) = _source();
            var arguments = _editingId is { } id
                ? AssistantCliCommands.MemoryUpdate(id, DraftText, DraftKind)
                : AssistantCliCommands.MemoryAdd(DraftText, DraftKind,
                    taskId is not null && AssistantCliCommands.IsValidTaskId(taskId) ? taskId : null,
                    threadId is not null && AssistantCliCommands.IsValidMemoryThread(threadId) ? threadId : null);
            var result = await _run(arguments, Timeout);
            if (!result.Ok || AssistantCliService.ProjectMemoryNote(Prop(result.Data, "note")) is not { } note)
            {
                // The draft stays, so a refused note (for example one that looks like a secret) can be edited.
                if (result.Ok) StatusText = "DwemerDistro did not confirm the note. Refresh to check.";
                else Fail("The note was not saved", result);
                return;
            }
            var duplicate = AssistantCliService.GetBool(result.Data, "duplicate");
            var index = Notes.ToList().FindIndex(existing => existing.Id == note.Id);
            if (index >= 0) Notes[index] = note;
            else Notes.Insert(0, note);
            StatusText = _editingId is not null ? $"Updated note #{note.Id}." : duplicate ? $"Already remembered as note #{note.Id}." : $"Remembered as note #{note.Id}.";
            ClearDraft();
        }
        finally
        {
            End();
        }
    }

    private void EditSelected()
    {
        if (Selected is not { } note) return;
        _editingId = note.Id;
        DraftKind = note.Kind;
        DraftText = note.Text;
        OnPropertyChanged(nameof(SaveLabel));
        StatusText = $"Editing note #{note.Id}. An edited note becomes yours; its task and chat source are kept.";
    }

    private void ClearDraft()
    {
        _editingId = null;
        DraftText = string.Empty;
        OnPropertyChanged(nameof(SaveLabel));
    }

    private async Task ForgetAsync()
    {
        if (Selected is not { } note || !await BeginAsync()) return;
        try
        {
            var result = await _run(AssistantCliCommands.MemoryForget(note.Id), Timeout);
            if (!result.Ok)
            {
                Fail("The note was not forgotten", result);
                return;
            }
            Notes.Remove(note);
            StatusText = Prop(result.Data, "forgotten") is { ValueKind: JsonValueKind.Number } count && count.TryGetInt32(out var forgotten) && forgotten == 1
                ? $"Forgot note #{note.Id}." : $"Note #{note.Id} was already gone.";
        }
        finally
        {
            End();
        }
    }

    private async Task ExportAsync()
    {
        // The dialog comes first: nothing is read until the user picks where the notes go.
        var path = _host.ChooseSavePath("Export assistant memory", "dwemerdistro-assistant-memory.json", "JSON files (*.json)|*.json");
        if (path is null || !await BeginAsync()) return;
        try
        {
            var result = await _run(AssistantCliCommands.MemoryExport(), Timeout);
            var notes = result.Ok ? AssistantCliService.ProjectMemoryNotes(result.Data, "notes", 1000) : null;
            if (notes is null || AssistantCliService.ProjectMemorySettings(result.Data) is not { } settings)
            {
                if (result.Ok) StatusText = "DwemerDistro returned the export in an unexpected shape, so nothing was written.";
                else Fail("Export failed", result);
                return;
            }
            var document = new System.Text.Json.Nodes.JsonObject
            {
                ["exported_at"] = AssistantCliService.GetString(result.Data, "exported_at"),
                ["scope"] = "installation",
                ["settings"] = new System.Text.Json.Nodes.JsonObject { ["retrieval"] = settings.Retrieval, ["auto_learning"] = settings.AutoLearning },
                ["notes"] = JsonSerializer.SerializeToNode(notes)
            };
            await File.WriteAllTextAsync(path, document.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            StatusText = $"Exported {notes.Count} note(s) to {path}.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            StatusText = "The export file could not be written: " + Shorten(ex.Message, 160);
        }
        finally
        {
            End();
        }
    }

    private async Task ResetAsync()
    {
        ConfirmingReset = false;
        if (!await BeginAsync()) return;
        try
        {
            var result = await _run(AssistantCliCommands.MemoryReset(), Timeout);
            if (!result.Ok)
            {
                Fail("Memory was not reset", result);
                return;
            }
            Notes.Clear();
            StatusText = "Every memory note was forgotten. Your settings were kept. Chats, task receipts and sign-in are unchanged.";
        }
        finally
        {
            End();
        }
    }

    private async Task ConfigureAsync(bool? retrieval, bool? autoLearning)
    {
        if (!await BeginAsync()) return;
        try
        {
            var result = await _run(AssistantCliCommands.MemoryConfigure(retrieval, autoLearning), Timeout);
            if (result.Ok && AssistantCliService.ProjectMemorySettings(result.Data) is { } settings)
            {
                ApplySettings(settings);
                // Core declares the memory tool only as a session starts, so the open session keeps what it started with.
                StatusText = "Memory settings saved. They apply to a new chat, or after Disconnect and Connect; the open session is unchanged " +
                             "until then. A reconnected chat moves once to a new thread with the memory tool on your next message.";
            }
            else
            {
                if (result.Ok) StatusText = "DwemerDistro did not confirm the settings.";
                else Fail("Settings were not changed", result);
            }
        }
        finally
        {
            // A refused change puts the switch back to what Core holds.
            OnPropertyChanged(nameof(Retrieval));
            OnPropertyChanged(nameof(AutoLearning));
            End();
        }
    }

    private void ApplySettings((bool Retrieval, bool AutoLearning) settings)
    {
        _retrieval = settings.Retrieval;
        _autoLearning = settings.AutoLearning;
        SettingsKnown = true;
        OnPropertyChanged(nameof(Retrieval));
        OnPropertyChanged(nameof(AutoLearning));
        OnPropertyChanged(nameof(SettingsSummary));
    }

    /// <summary>The quiet state line shown next to the conversation.</summary>
    public string SettingsSummary => !SettingsKnown ? "Memory: not read yet"
        : "Memory: " + (_retrieval ? "assistant may search notes" : "off for the assistant") + " · automatic learning " + (_autoLearning ? "on" : "off");

    private Task<bool> BeginAsync()
    {
        if (IsBusy) return Task.FromResult(false);
        IsBusy = true;
        return Task.FromResult(true);
    }

    private void End() => IsBusy = false;

    private void Fail(string what, AssistantCliResult result)
    {
        if (result.ErrorCode is "invalid_argument" or "usage" or "unsupported" && !result.Ok && Notes.Count == 0) IsAvailable = false;
        StatusText = what + ": " + result.ErrorCode switch
        {
            "busy" => "DwemerDistro is busy (maintenance or another memory change). Try again.",
            "refused" => Shorten(result.ErrorMessage, 240),
            "disabled" or "limit_reached" or "not_found" => Shorten(result.ErrorMessage, 240),
            AssistantCliResult.CliMissing or AssistantCliResult.DistroMissing => "the DwemerDistro command is not available.",
            "invalid_argument" or "usage" or "unsupported" or AssistantCliResult.Protocol => "this DwemerDistro Core has no assistant memory yet. Run Update Distro.",
            _ => Shorten(result.ErrorMessage.Length > 0 ? result.ErrorMessage : result.ErrorCode, 240)
        };
    }

    private void Raise()
    {
        foreach (var command in new System.Windows.Input.ICommand[]
                 { RefreshCommand, MoreCommand, SearchCommand, SaveCommand, EditCommand, CancelEditCommand, ForgetCommand, ExportCommand, ResetCommand, ConfirmResetCommand, CancelResetCommand })
        {
            switch (command)
            {
                case AsyncRelayCommand asyncCommand: asyncCommand.RaiseCanExecuteChanged(); break;
                case RelayCommand relayCommand: relayCommand.RaiseCanExecuteChanged(); break;
            }
        }
    }

    private static JsonElement Prop(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) ? value : default;

    private static string Shorten(string value, int max) =>
        string.IsNullOrEmpty(value) ? string.Empty : value.Length <= max ? value : value[..max] + "…";
}
