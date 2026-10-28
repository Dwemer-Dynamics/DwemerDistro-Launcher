using System.Collections.ObjectModel;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DwemerDistro.Launcher.Wpf.Services;

namespace DwemerDistro.Launcher.Wpf.ViewModels;

/// <summary>One configured global OpenRouter key, by its id and label only. Never its secret or preview.</summary>
public sealed record OpenRouterKeyOption(string KeyId, string Label)
{
    public string Display => $"{Label} ({KeyId[..8]})";
    public override string ToString() => Display;
}

/// <summary>
/// OpenRouter as a third assistant engine, hosted entirely by Core: Core runs the model loop with the user's global
/// OpenRouter key (chosen here by its <c>key_id</c>, resolved privately by Core on every turn), declares and runs its own
/// tools, and keeps the transcript. The launcher sends <c>ddistro/external/turn/start</c> and renders Core's notifications;
/// it runs no relay or local process, never sees the secret, and never sends <c>tool/call</c> or <c>turn/end</c>.
/// OpenRouter chats have their own task reference (task.openrouter.json), History entries (provider <c>openrouter</c>) and
/// a per-chat key/model binding; no other provider's chat is reused.
/// </summary>
public sealed partial class AssistantViewModel
{
    internal const string OpenRouterEngine = "openrouter";
    internal const string OpenRouterCatalogProvider = "openrouter";
    internal const string OpenRouterRole = "OpenRouter";
    internal const string OpenRouterKeyProvider = "openrouter";
    internal const string OpenRouterKeyOrigin = "https://openrouter.ai:443";
    internal const string OpenRouterCoreTooOldText = "Update DwemerDistro Core to use OpenRouter. Run Update Distro, then Connect again.";
    internal const string OpenRouterDefaultModel = "z-ai/glm-5.3";
    internal const string OpenRouterIdleText = "Press Connect to use your global OpenRouter key from API Keys.";
    private const string OpenRouterKeyInUseText = "Uses your global OpenRouter key from API Keys. DwemerDistro reads it on each message.";
    // Core appends these under "Launcher instructions" and refuses more than 32 KiB.
    internal const int MaxOpenRouterInstructionsBytes = 32 * 1024;

    internal static readonly AssistantModelOption[] OpenRouterModels =
    [
        // Text only: images are not attached while it is chosen. Earlier Gemma chats still open; Gemma is no longer offered.
        new("z-ai/glm-5.3", "GLM 5.3 (Z.ai)", SupportsImages: false),
        new("openai/gpt-6.1-sol", "GPT-6.1 Sol (OpenAI)"),
        new("anthropic/claude-sonnet-5.5", "Claude Sonnet 5.5 (Anthropic)")
    ];

    private readonly IAssistantStateStore? _openRouterStore;
    private readonly OpenRouterChatStore? _openRouterChats;
    private readonly Func<CancellationToken, Task<ApiKeysResult>>? _openRouterKeySource;
    private OpenRouterChat? _openRouterChat;
    private OpenRouterKeyOption? _selectedOpenRouterKey;
    private bool _openRouterKeysLoading;
    private bool _openRouterKeysRead;
    private int _openRouterKeyCheck;
    private bool _openRouterKeysFailed;
    private string _openRouterKeyStatus = OpenRouterIdleText;

    public bool IsOpenRouter => _selectedProvider.Id == OpenRouterProviderId;

    /// <summary>Claude and OpenRouter run on Core's external engine: no ChatGPT account, no Codex thread, no Codex sign-in import.</summary>
    private bool IsExternalEngine => IsClaude || IsOpenRouter;

    private string ExternalEngine => IsOpenRouter ? OpenRouterEngine : ClaudeEngine;

    /// <summary>Configured global keys of provider openrouter with OpenRouter's origin, from the last explicit read.</summary>
    public ObservableCollection<OpenRouterKeyOption> OpenRouterKeys { get; } = [];

    /// <summary>
    /// The key this chat uses. The Assistant page offers no picker: a saved key still configured is kept, otherwise the one
    /// configured global key is chosen when Connect, or the page shown again, reads key metadata.
    /// Setting it is still an explicit binding by id, refused (and reverted) unless the tab is idle.
    /// </summary>
    public OpenRouterKeyOption? SelectedOpenRouterKey
    {
        get => _selectedOpenRouterKey;
        set
        {
            if (ReferenceEquals(value, _selectedOpenRouterKey)) return;
            if (value is null || !CanSwitchProvider || !OpenRouterKeys.Contains(value))
            {
                if (value is not null) StatusDetail = "Change the key when nothing is connecting, responding, running or exporting.";
                _ = _dispatcher.BeginInvoke(() => OnPropertyChanged(nameof(SelectedOpenRouterKey)));
                return;
            }
            SetSelectedOpenRouterKey(value);
            BindOpenRouterChat(value.KeyId, null);
            _openRouterKeysFailed = false;
            OpenRouterKeyStatus = OpenRouterKeyInUseText;
            RaiseOpenRouter();
        }
    }

    /// <summary>Why the key is or is not ready, in a sentence or two: the status tooltip and the reason a message was not sent.</summary>
    public string OpenRouterKeyStatus
    {
        get => _openRouterKeyStatus;
        private set { if (SetProperty(ref _openRouterKeyStatus, value)) RaiseOpenRouterConnection(); }
    }

    /// <summary>API Keys could not be read, or no key can be used for this chat: the API Keys link is offered.</summary>
    public bool IsOpenRouterKeyMissing => IsOpenRouter && _client is not null && !_openRouterKeysLoading
        && (_openRouterKeysFailed || _openRouterKeysRead && SelectedOpenRouterKey is null);

    /// <summary>ChatGPT and Claude show their account line; OpenRouter shows its connection status instead.</summary>
    public bool IsAccountTextShown => !IsOpenRouter;

    /// <summary>Connected to DwemerDistro with this chat's global key ready. OpenRouter itself is first asked on the next message.</summary>
    public bool IsOpenRouterConnected => IsOpenRouter && _client is not null && SelectedOpenRouterKey is not null && !_openRouterKeysLoading;

    /// <summary>A few words beside the status dot; never a key label, id or preview. <see cref="OpenRouterKeyStatus"/> says more.</summary>
    public string OpenRouterConnectionText =>
        !IsOpenRouter ? string.Empty
        : _openRouterKeysLoading ? "Checking key…"
        : _client is null ? "Not connected"
        : SelectedOpenRouterKey is not null ? "Connected"
        : _openRouterKeysFailed ? "Keys unavailable"
        : !_openRouterKeysRead ? "Connecting…"
        : "No key";

    public bool CanChooseOpenRouterKey => IsOpenRouter && CanSwitchProvider && !_openRouterKeysLoading && OpenRouterKeys.Count > 0;

    public AsyncRelayCommand ReloadOpenRouterKeysCommand { get; private set; } = null!;
    public RelayCommand OpenApiKeysCommand { get; private set; } = null!;

    private void InitializeOpenRouter()
    {
        ReloadOpenRouterKeysCommand = new AsyncRelayCommand(ReloadOpenRouterKeysAsync,
            () => IsOpenRouter && _openRouterKeySource is not null && !_openRouterKeysLoading && CanSwitchProvider);
        OpenApiKeysCommand = new RelayCommand(() => _host.OpenApiKeys(), () => IsOpenRouter);
    }

    private void SetSelectedOpenRouterKey(OpenRouterKeyOption? key)
    {
        _selectedOpenRouterKey = key;
        OnPropertyChanged(nameof(SelectedOpenRouterKey));
    }

    private void RaiseOpenRouter()
    {
        OnPropertyChanged(nameof(IsOpenRouter));
        OnPropertyChanged(nameof(IsAccountTextShown));
        OnPropertyChanged(nameof(CanChooseOpenRouterKey));
        RaiseOpenRouterConnection();
        ReloadOpenRouterKeysCommand.RaiseCanExecuteChanged();
        OpenApiKeysCommand.RaiseCanExecuteChanged();
    }

    private void RaiseOpenRouterConnection()
    {
        OnPropertyChanged(nameof(IsOpenRouterKeyMissing));
        OnPropertyChanged(nameof(IsOpenRouterConnected));
        OnPropertyChanged(nameof(OpenRouterConnectionText));
    }

    /// <summary>
    /// The Assistant page is shown again, e.g. after API Keys: a connected, idle OpenRouter chat without a usable key reads key
    /// metadata once more. Nothing is read while a key is in use, before Connect or while anything runs; never polled.
    /// </summary>
    internal void OnPageShown()
    {
        if (IsOpenRouter && _client is not null && SelectedOpenRouterKey is null && !_openRouterKeysLoading && CanSwitchProvider && !_isShutDown)
            _ = LoadOpenRouterKeysAsync();
    }

    // --- Keys ---------------------------------------------------------------------------------

    private async Task ReloadOpenRouterKeysAsync()
    {
        if (!IsOpenRouter) return;
        await LoadOpenRouterKeysAsync();
    }

    /// <summary>
    /// Reads global key metadata (<c>ddistro_keys global-status</c>) once, on Connect or when the page is shown again without a
    /// usable key; never polled. Offers
    /// only configured keys of provider openrouter with OpenRouter's origin. A result for an earlier read or another provider
    /// is dropped. Nothing is created, edited or revealed.
    /// </summary>
    private async Task LoadOpenRouterKeysAsync()
    {
        if (_openRouterKeySource is null) return;
        var check = ++_openRouterKeyCheck;
        var provider = _selectedProvider;
        _openRouterKeysLoading = true;
        OpenRouterKeyStatus = "Checking API Keys…";
        RaiseOpenRouter();
        ApiKeysResult result;
        try
        {
            result = await _openRouterKeySource(_lifetime.Token);
        }
        catch (Exception ex) when (ex is OperationCanceledException or JsonException or InvalidOperationException or System.IO.IOException)
        {
            result = new ApiKeysResult(false, "API Keys could not be read.", "unavailable", null);
        }
        if (check != _openRouterKeyCheck || !ReferenceEquals(provider, _selectedProvider) || _isShutDown) return;
        _openRouterKeysLoading = false;
        SharedKeysData? snapshot = null;
        try
        {
            snapshot = result.Ok ? result.Snapshot : null;
        }
        catch (JsonException)
        {
        }
        OpenRouterKeys.Clear();
        if (snapshot is null)
        {
            _openRouterKeysRead = false;
            _openRouterKeysFailed = true;
            SetSelectedOpenRouterKey(null);
            OpenRouterKeyStatus = "API Keys could not be read" + (result.Error is { Length: > 0 } error ? ": " + Shorten(error, 120) : ".")
                                  + " Check API Keys, then return here to try again.";
            RaiseOpenRouter();
            return;
        }
        foreach (var key in FilterOpenRouterKeys(snapshot.Keys ?? [])) OpenRouterKeys.Add(key);
        _openRouterKeysRead = true;
        _openRouterKeysFailed = false;
        ApplyOpenRouterBinding();
        RaiseOpenRouter();
    }

    internal static IReadOnlyList<OpenRouterKeyOption> FilterOpenRouterKeys(IEnumerable<SharedKey> keys) =>
        keys.Where(key => key is not null && key.Provider == OpenRouterKeyProvider && key.Configured
                          && string.Equals(key.Origin, OpenRouterKeyOrigin, StringComparison.Ordinal) && OpenRouterChat.IsKeyId(key.Id))
            .Select(key => new OpenRouterKeyOption(key.Id, string.IsNullOrWhiteSpace(key.Label) ? "OpenRouter key" : Shorten(key.Label.Trim(), 48)))
            .ToList();

    /// <summary>
    /// Selects this chat's saved key while it is still configured. Otherwise (no binding yet, a lost record, or a key that was
    /// removed or re-created) the chat, with its thread, model and history, is bound to the one configured global key. With
    /// several and none of them saved for this chat, none is chosen.
    /// </summary>
    private void ApplyOpenRouterBinding()
    {
        if (!_openRouterKeysRead) return;
        var bound = _openRouterChat?.KeyId;
        if (OpenRouterKeys.FirstOrDefault(key => key.KeyId == bound) is { } match)
        {
            SetSelectedOpenRouterKey(match);
            OpenRouterKeyStatus = OpenRouterKeyInUseText;
            return;
        }
        var recovering = bound is not null || OpenRouterBindingLost;
        if (OpenRouterKeys.Count == 1 && _openRouterChat is not null)
        {
            var only = OpenRouterKeys[0];
            SetSelectedOpenRouterKey(only);
            BindOpenRouterChat(only.KeyId, null);
            OpenRouterKeyStatus = OpenRouterKeyInUseText;
            if (recovering) AddSystem("This chat's earlier OpenRouter key is no longer available, so it now uses your global OpenRouter key from API Keys.");
            return;
        }
        SetSelectedOpenRouterKey(null);
        OpenRouterKeyStatus = (recovering ? "This chat's earlier OpenRouter key is no longer available. " : string.Empty) + OpenRouterKeys.Count switch
        {
            0 => "No OpenRouter key is set in API Keys. Add one there.",
            1 => "Press Connect to use your OpenRouter key.",
            _ => "API Keys has several OpenRouter keys, so none was chosen for this chat. Keep one OpenRouter key in API Keys."
        };
    }

    /// <summary>
    /// Core reported this chat's key missing: it is no longer used, so the page offers API Keys and, shown again, reads key
    /// metadata once. The saved binding stays until a read finds the key gone.
    /// </summary>
    private void ForgetOpenRouterKey()
    {
        SetSelectedOpenRouterKey(null);
        OpenRouterKeyStatus = "This chat's earlier OpenRouter key is no longer available. Keep one OpenRouter key in API Keys.";
        RaiseOpenRouter();
    }

    /// <summary>Saves the open chat's key and/or model ids. Never the secret.</summary>
    private void BindOpenRouterChat(string? keyId, string? model)
    {
        if (_openRouterChat is not { } chat || _threadId != chat.ThreadId || _isShutDown) return;
        _openRouterChat = chat with { KeyId = keyId ?? chat.KeyId, Model = model ?? chat.Model };
        if (_openRouterChats?.Save(_openRouterChat) == false)
            HistoryStatus = "This OpenRouter chat's key and model choice could not be saved, so a reopened chat may not keep them.";
    }

    // --- Connect ------------------------------------------------------------------------------

    /// <summary>Connect's OpenRouter gate: Core must offer the external engine with OpenRouter. No ChatGPT account or local tool is needed.</summary>
    private bool CheckOpenRouterOffered(JsonElement capabilities)
    {
        var features = Prop(capabilities, "features");
        var engines = Prop(Prop(capabilities, "assistant_external_engine"), "engines");
        var offered = AssistantCliService.GetBool(features, "assistant_external_engine") && engines.ValueKind == JsonValueKind.Array
                      && engines.EnumerateArray().Any(engine => engine.ValueKind == JsonValueKind.String && engine.GetString() == OpenRouterEngine);
        if (offered && _toolRevision > 0) return true;
        SetState(AssistantState.RuntimeAbsent, OpenRouterCoreTooOldText);
        return false;
    }

    /// <summary>
    /// After <c>initialize</c> with <c>ddistroEngine:"openrouter"</c>: no account read, model list or sign-in. Opens or resumes
    /// the chat's OpenRouter thread, reads key metadata once, then becomes Ready and shows Core's stored history.
    /// </summary>
    private async Task StartOpenRouterSessionAsync(CodexAppServerClient client)
    {
        var previous = SelectedModel?.Id;
        await EnsureOpenRouterThreadAsync(client);
        if (!ReferenceEquals(_client, client)) return;
        // The chat's own saved model wins; the picker's previous choice only seeds a chat that has none.
        var preferred = _openRouterChat?.Model is { } saved && Models.Any(model => model.Id == saved) ? saved : previous ?? OpenRouterDefaultModel;
        SelectedModel = Models.FirstOrDefault(model => model.Id == preferred) ?? Models.FirstOrDefault(model => model.Id == OpenRouterDefaultModel) ?? Models.First();
        await LoadOpenRouterKeysAsync();
        if (!ReferenceEquals(_client, client)) return;
        AccountText = OpenRouterConnectionText;
        await LoadRecoveryAsync(client, supplyContext: true);
        if (!ReferenceEquals(_client, client)) return;
        SetState(AssistantState.Ready, ReadyText());
        if (_historyLoadPending)
        {
            _historyLoadPending = false;
            await LoadEarlierAsync(client, initial: true);
            if (!ReferenceEquals(_client, client)) return;
        }
        if (_pendingContinuation is { } continuation)
        {
            _pendingContinuation = null;
            await StartTurnAsync(continuation);
        }
        else
        {
            await RefreshChangesAsync();
        }
    }

    private async Task EnsureOpenRouterThreadAsync(CodexAppServerClient client)
    {
        JsonElement result = default;
        var resumed = false;
        if (_threadId is { } existing)
        {
            try
            {
                result = await client.RequestAsync("ddistro/external/thread/resume", new JsonObject { ["threadId"] = existing }, RpcTimeout);
                resumed = true;
            }
            catch (AppServerException ex) when (client.IsConnected)
            {
                AddSystem("DwemerDistro could not resume this OpenRouter chat (" + Shorten(ex.Message, 160) + "), so its earlier messages cannot be "
                          + "shown and a new OpenRouter chat was started. The earlier one stays in History.");
            }
        }
        if (!resumed)
            result = await client.RequestAsync("ddistro/external/thread/start", new JsonObject { ["engine"] = OpenRouterEngine }, RpcTimeout);
        var threadId = AssistantCliService.GetString(result, "threadId");
        if (!Guid.TryParseExact(threadId, "D", out _) || AssistantCliService.GetString(result, "engine") != OpenRouterEngine)
            throw new AppServerException(0, "DwemerDistro did not open an OpenRouter chat.");
        if (resumed && threadId != _threadId) throw new AppServerException(0, "DwemerDistro resumed a different chat than this one.");
        // Only the fixed presets Core also offers; Core never substitutes another model, and neither does the launcher.
        var offered = Prop(result, "models") is { ValueKind: JsonValueKind.Array } models
            ? models.EnumerateArray().Select(model => AssistantCliService.GetString(model, "id")).ToHashSet(StringComparer.Ordinal)
            : [];
        var usable = OpenRouterModels.Where(model => offered.Contains(model.Id)).ToList();
        if (usable.Count == 0) throw new AppServerException(0, "DwemerDistro offered none of the OpenRouter models this launcher supports. Run Update Distro.");
        Models.Clear();
        foreach (var model in usable) Models.Add(model);

        if (!resumed && _threadId is { } replaced && replaced != threadId) KeepSuperseded(replaced);
        _threadToolRevision = _toolRevision;
        _threadId = threadId;
        SaveReference();
        OnPropertyChanged(nameof(HasReadTools));

        if (_openRouterChat?.ThreadId != threadId)
        {
            var saved = resumed ? _openRouterChats?.Load(threadId) : null;
            // A resumed chat without its record: an empty key with a model marks it lost, so its recovery is stated when the one
            // global key is bound to it, and nothing is chosen while API Keys has several.
            _openRouterChat = saved ?? new OpenRouterChat(OpenRouterChat.CurrentVersion, threadId, null, resumed ? OpenRouterDefaultModel : null);
            if (saved is null && !resumed) _openRouterChats?.Save(_openRouterChat);
        }
        if (_shownThreadId != threadId)
        {
            ResetHistory();
            _historyThreadId = threadId;
            if (resumed) _historyPages.Add((null, 0));
            _historyLoadPending = resumed;
            RaiseHistory();
        }
        _shownThreadId = threadId;
    }

    /// <summary>A resumed chat whose binding record was lost; it is bound only to a sole configured global key.</summary>
    private bool OpenRouterBindingLost => _openRouterChat is { KeyId: null, Model: not null };

    // --- Turns --------------------------------------------------------------------------------

    /// <summary>
    /// One OpenRouter turn: <c>ddistro/external/turn/start {threadId, model, keyId, input, instructions}</c> through the same
    /// runner, reservation, queue and Stop as a Codex turn. Core runs the model and tools and its <c>turn/completed</c> is the
    /// only end; nothing is retried or replayed. Not sent at all without a bound, configured key. True once
    /// Core accepted the turn.
    /// </summary>
    private async Task<bool> StartOpenRouterTurnAsync(AssistantTurnRunner turns, JsonArray input)
    {
        var threadId = _threadId!;
        if (SelectedOpenRouterKey is not { } key)
        {
            AddSystem("The message was not sent: " + OpenRouterKeyStatus);
            SetState(AssistantState.Ready);
            return false;
        }
        if (SelectedModel is not { } model || !OpenRouterModels.Any(option => option.Id == model.Id))
        {
            AddSystem("The message was not sent: choose one of the OpenRouter models first.");
            SetState(AssistantState.Ready);
            return false;
        }
        if (!model.SupportsImages && input.Any(item => item?["type"]?.GetValue<string>() == "image"))
        {
            AddSystem($"The message was not sent: {model.DisplayName} reads text only. Remove the image, or choose another model.");
            SetState(AssistantState.Ready);
            return false;
        }
        var instructions = InstructionsFor(ActiveToolRevision);
        if (Encoding.UTF8.GetByteCount(instructions) > MaxOpenRouterInstructionsBytes)
        {
            AddSystem("The message was not sent: this launcher's assistant instructions are larger than DwemerDistro accepts for OpenRouter. Update the launcher.");
            SetState(AssistantState.Ready);
            return false;
        }
        var turn = new JsonObject
        {
            ["threadId"] = threadId, ["model"] = model.Id, ["keyId"] = key.KeyId, ["input"] = input, ["instructions"] = instructions
        };
        if (Encoding.UTF8.GetByteCount(turn.ToJsonString()) > AssistantAttachments.MaxTurnWireBytes)
        {
            AddSystem("The message was not sent: with its attachments it is too large. Remove an attachment and send again.");
            SetState(AssistantState.Ready);
            return false;
        }
        BindOpenRouterChat(key.KeyId, model.Id);
        await turns.StartAsync(threadId, turn, "ddistro/external/turn/start");
        return true;
    }

    /// <summary>Core's safe <c>turn.error</c> message with what to do next. Shown whatever the turn's status.</summary>
    internal static string DescribeOpenRouterError(JsonElement error)
    {
        var code = AssistantCliService.GetString(error, "code");
        var message = Shorten(AssistantCliService.GetString(error, "message"), 240);
        var hint = code switch
        {
            "key_missing" => "This chat's key is no longer in API Keys. Keep one OpenRouter key there, and this chat uses it.",
            "key_wrong_provider" => "The key is not an OpenRouter key. Check API Keys.",
            "key_changed" => "The key changed while DwemerDistro read it. Send the message again.",
            "key_unavailable" => "DwemerDistro could not read the key. Check API Keys, then send again.",
            "provider_auth" => "OpenRouter rejected the key. Check or replace it in API Keys.",
            "provider_credits" => "OpenRouter says this key has no credits left. Add credits at openrouter.ai, then send again.",
            "provider_refused" => "OpenRouter refused this request for this key or model.",
            "provider_request" => "OpenRouter did not accept this request for this model.",
            "provider_unavailable" => "OpenRouter or this model is busy or unavailable. Try again later.",
            "provider_redirect" => "OpenRouter answered with a redirect, which DwemerDistro never follows.",
            "network_error" or "incomplete_response" or "malformed_response" or "response_too_large" or "provider_error" or "provider_finish" =>
                "The response may be incomplete and nothing was retried. Check what ran before sending again.",
            "round_limit" => "The assistant reached its limit of model requests for one message.",
            "history_unsaved" => "The response ran, but DwemerDistro could not save it, so a reopened chat will not show it.",
            _ => string.Empty
        };
        var text = "OpenRouter: " + (message.Length > 0 ? message : "the response ended with an error");
        if (!text.EndsWith('.') && !text.EndsWith('!') && !text.EndsWith('?') && !text.EndsWith('…')) text += ".";
        if (hint.Length > 0) text += " " + hint;
        return code.Length > 0 ? text + $" ({Shorten(code, 40)})" : text;
    }

    // --- History ------------------------------------------------------------------------------

    /// <summary>
    /// Reads page <paramref name="page"/> of an OpenRouter chat through Core's <c>ddistro/external/thread/turns/list</c>, newest
    /// turns first with their items. Each turn's error is shown under it; the oldest page says how many turns Core dropped.
    /// </summary>
    private async Task<(List<AssistantTranscriptItem> Items, int Omitted)> ReadOpenRouterHistoryPageAsync(CodexAppServerClient client, string threadId, int page)
    {
        var (cursor, limit) = _historyPages[page];
        if (limit == 0) limit = Math.Clamp(_historyPageTurns, 1, 10);
        var request = new JsonObject { ["threadId"] = threadId, ["limit"] = limit, ["sortDirection"] = "desc" };
        if (cursor is not null) request["cursor"] = cursor;
        var result = await client.RequestAsync("ddistro/external/thread/turns/list", request, RpcTimeout);
        var turns = Prop(result, "data") is { ValueKind: JsonValueKind.Array } data ? data.EnumerateArray().Reverse().ToList() : [];
        var restored = new List<AssistantTranscriptItem>();
        var omitted = 0;
        foreach (var turn in turns)
        {
            IEnumerable<JsonElement> items = Prop(turn, "items") is { ValueKind: JsonValueKind.Array } turnItems ? turnItems.EnumerateArray() : Array.Empty<JsonElement>();
            foreach (var item in ReadItems(items, ref omitted))
                restored.Add(item.Role == "Codex" ? new AssistantTranscriptItem(OpenRouterRole, item.Text, item.ItemId) : item);
            if (Prop(turn, "error") is { ValueKind: JsonValueKind.Object } error)
                restored.Add(new AssistantTranscriptItem("DwemerDistro", DescribeOpenRouterError(error)));
        }
        _historyPages[page] = (cursor, limit);
        while (_historyPageItems.Count <= page) _historyPageItems.Add(0);
        var next = AssistantCliService.GetString(result, "nextCursor");
        if (next.Length == 0 && Prop(result, "compacted") is { ValueKind: JsonValueKind.Number } compacted && compacted.TryGetInt32(out var dropped) && dropped > 0)
            restored.Insert(0, new AssistantTranscriptItem("DwemerDistro",
                $"DwemerDistro dropped the {dropped} oldest turn(s) of this long chat to stay within its history limit; they cannot be shown."));
        _historyPageItems[page] = restored.Count;
        if (next.Length > 0 && _historyPages.Count == page + 1) _historyPages.Add((next, 0));
        return (restored, omitted);
    }
}
