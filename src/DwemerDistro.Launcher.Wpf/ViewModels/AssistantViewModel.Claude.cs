using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DwemerDistro.Launcher.Wpf.Services;

namespace DwemerDistro.Launcher.Wpf.ViewModels;

/// <summary>One choice of the Provider picker. An unavailable one is listed, but cannot be chosen, and says why.</summary>
public sealed record AssistantProviderOption(string Id, string DisplayName, bool IsAvailable, string Note)
{
    public string Label => IsAvailable ? DisplayName : DisplayName + " (coming later)";
    public override string ToString() => Label;
}

/// <summary>
/// Local Claude Code as a second assistant engine. The session, tools, approval cards, live runs and Stop are the same
/// Core bridge and handlers ChatGPT uses; only the model runs differently: each turn is one <c>claude.exe</c> process
/// (<see cref="ClaudeTurnProcess"/>) signed in with the user's own Claude account, whose only tools are Core's broker tools
/// through <see cref="AssistantMcpRelay"/>. Claude chats have their own task reference (task.claude.json), their own
/// History entries (provider <c>claude</c>) and a private local transcript; the ChatGPT reference and history are untouched.
/// </summary>
public sealed partial class AssistantViewModel
{
    internal const string ChatGptProviderId = "chatgpt";
    internal const string ClaudeProviderId = "claude";
    internal const string OpenRouterProviderId = "openrouter";
    // The provider name Claude's task reference and History entries carry; ChatGPT's come from Core's agent status (codex).
    internal const string ClaudeCatalogProvider = "claude";
    internal const string CodexCatalogProvider = "codex";
    internal const string ClaudeEngine = "claude";
    internal const string ClaudeRole = "Claude";
    internal const string ClaudeCoreTooOldText = "Update DwemerDistro Core to use Claude. Run Update Distro, then Connect again.";
    internal const string ClaudeDefaultModel = "claude-sonnet-5-5";

    internal static readonly AssistantModelOption[] ClaudeModels =
    [
        new("claude-haiku-5-5", "Claude Haiku 5.5"),
        new("claude-sonnet-5-5", "Claude Sonnet 5.5"),
        new("claude-opus-5-5", "Claude Opus 5.5")
    ];

    private readonly IAssistantStateStore _codexStore;
    private readonly IAssistantStateStore? _claudeStore;
    private readonly ClaudeChatStore? _claudeChats;
    private readonly Func<CancellationToken, Task<ClaudeCodeStatus>>? _claudeStatus;
    private readonly ProcessRunner? _claudeRunner;
    private AssistantProviderOption _selectedProvider = null!;
    private ClaudeCodeStatus? _claudeCode;
    private int _claudeStatusCheck;
    private bool _claudeStatusChecking;
    private string _claudeStatusText = "Not checked";
    // The broker's tools for the open Claude thread, from ddistro/external/thread/start or resume, passed verbatim to the relay.
    private JsonElement _claudeTools;
    private ClaudeChat? _claudeChat;
    private ClaudeTurnProcess? _claudeTurn;
    private string? _claudeTurnId;
    private bool _claudeStopRequested;

    public ObservableCollection<AssistantProviderOption> Providers { get; } = [];

    /// <summary>The engine for this tab. Changing it is a request: refused (and reverted) unless the tab is idle.</summary>
    public AssistantProviderOption SelectedProvider
    {
        get => _selectedProvider;
        set
        {
            if (value is null || ReferenceEquals(value, _selectedProvider)) return;
            _ = SwitchProviderAsync(value);
        }
    }

    public bool IsClaude => _selectedProvider.Id == ClaudeProviderId;

    /// <summary>Switching providers waits until nothing connects, responds, runs, applies, exports or loads.</summary>
    public bool CanSwitchProvider => CanSwitchChat && _claudeTurn is null && _actionRunningId is null && State != AssistantState.Checking;

    public string AccountLabelText => IsClaude ? "Claude Code" : IsOpenRouter ? "Key" : "Account";

    /// <summary>ChatGPT's in-launcher sign-in; Claude Code signs in on its own, in a terminal, and OpenRouter uses a global API key.</summary>
    public bool IsChatGptSignInShown => !IsExternalEngine;

    /// <summary>Local Claude Code readiness: found, new enough, signed in with a Claude account. Never the account's identity.</summary>
    public string ClaudeStatusText { get => _claudeStatusText; private set => SetProperty(ref _claudeStatusText, value); }

    public bool IsClaudeStatusChecking { get => _claudeStatusChecking; private set => SetProperty(ref _claudeStatusChecking, value); }

    public string ProviderHint => IsClaude
        ? "Claude runs with Claude Code on this PC and your own Claude sign-in. DwemerDistro gives it only its own tools, with the same approvals."
        : IsOpenRouter
        ? "OpenRouter runs inside DwemerDistro with an OpenRouter key from API Keys. The key stays in DwemerDistro; the model gets only DwemerDistro's tools, with the same approvals."
        : "ChatGPT runs Codex inside DwemerDistro with your ChatGPT sign-in.";

    private string AgentRole => IsClaude ? ClaudeRole : IsOpenRouter ? OpenRouterRole : "Codex";

    // Where a deleted chat's conversation lived, for the delete card and its outcome.
    private string ChatHome => IsClaude ? "DwemerDistro and this launcher" : IsOpenRouter ? "DwemerDistro" : "Codex";

    private void InitializeProviders()
    {
        Providers.Add(new AssistantProviderOption(ChatGptProviderId, "ChatGPT", true, string.Empty));
        Providers.Add(new AssistantProviderOption(ClaudeProviderId, "Claude", _claudeStore is not null && _claudeStatus is not null && _claudeRunner is not null,
            "Claude is not available in this launcher build."));
        Providers.Add(new AssistantProviderOption(OpenRouterProviderId, "OpenRouter",
            _openRouterStore is not null && _openRouterChats is not null && _openRouterKeySource is not null,
            "OpenRouter is not available in this launcher build."));
        _selectedProvider = Providers[0];
    }

    private async Task SwitchProviderAsync(AssistantProviderOption option)
    {
        var previous = _selectedProvider;
        if (!option.IsAvailable || !CanSwitchProvider)
        {
            StatusDetail = !option.IsAvailable ? option.Note : "Switch providers when nothing is connecting, responding, running or exporting.";
            // The picker shows the provider in use again.
            _ = _dispatcher.BeginInvoke(() => OnPropertyChanged(nameof(SelectedProvider)));
            return;
        }

        var reconnect = _client is not null;
        ClearAttachments();
        await EndSessionAsync(AssistantState.NotChecked, $"Switching to {option.DisplayName}…");
        if (!ReferenceEquals(_selectedProvider, previous)) return;
        // The open chat stays saved and in History under its own provider; nothing is carried to the other one.
        UpsertCurrentEntry(null);
        _selectedProvider = option;
        _store = option.Id switch
        {
            ClaudeProviderId => _claudeStore!,
            OpenRouterProviderId => _openRouterStore!,
            _ => _codexStore
        };
        _provider = option.Id switch
        {
            ClaudeProviderId => ClaudeCatalogProvider,
            OpenRouterProviderId => OpenRouterCatalogProvider,
            _ => CodexCatalogProvider
        };
        ResetChatState();
        Models.Clear();
        if (IsClaude) foreach (var model in ClaudeModels) Models.Add(model);
        if (IsOpenRouter) foreach (var model in OpenRouterModels) Models.Add(model);
        SelectedModel = IsClaude ? ClaudeModels.First(model => model.Id == ClaudeDefaultModel)
            : IsOpenRouter ? OpenRouterModels.First(model => model.Id == OpenRouterDefaultModel) : null;
        ProviderText = option.DisplayName;
        AccountText = IsClaude ? ClaudeStatusText : IsOpenRouter ? "Chosen from API Keys on Connect" : "Not connected";
        OnPropertyChanged(nameof(SelectedProvider));
        OnPropertyChanged(nameof(IsClaude));
        OnPropertyChanged(nameof(AccountLabelText));
        OnPropertyChanged(nameof(IsChatGptSignInShown));
        OnPropertyChanged(nameof(ProviderHint));
        RaiseOpenRouter();
        RefreshHistoryItems();
        SetState(AssistantState.NotChecked, $"{option.DisplayName} chats are separate from other providers' chats. Press Connect to open your {option.DisplayName} chat.");
        if (IsClaude) _ = RefreshClaudeStatusAsync();
        if (reconnect && !_isShutDown) await ConnectAsync();
    }

    /// <summary>Everything that belongs to the open chat, cleared; the saved references and History stay as they are.</summary>
    private void ResetChatState()
    {
        _taskId = null;
        _threadId = null;
        _threadToolRevision = 0;
        _supersededThreads = [];
        _earlierTasks = [];
        _pendingUpgrade = null;
        ClearRecovery();
        _upgradeFailedThread = null;
        UsageText = string.Empty;
        _shownThreadId = null;
        ResetHistory();
        _historyLoadPending = false;
        _canRollback = false;
        _canCheckOutcome = false;
        _pendingContinuation = null;
        _lastUserRequest = string.Empty;
        ClearFileRequest();
        HasChanges = false;
        OutcomeText = string.Empty;
        Transcript.Clear();
        TaskText = "No task yet";
        _claudeChat = null;
        _claudeTools = default;
        // Another provider's or chat's key read and binding never carry over; a read still running is dropped when it returns.
        _openRouterChat = null;
        _openRouterKeyCheck++;
        _openRouterKeysLoading = false;
        _openRouterKeysRead = false;
        _openRouterKeysFailed = false;
        OpenRouterKeys.Clear();
        SetSelectedOpenRouterKey(null);
        OpenRouterKeyStatus = OpenRouterIdleText;
    }

    // --- Readiness ----------------------------------------------------------------------------

    /// <summary>
    /// Reads local Claude Code's state (installed, new enough, signed in with a Claude account) through the locator's three
    /// metadata commands. No auth file is read, no model is called. A result for an earlier check or provider is ignored.
    /// </summary>
    private async Task<ClaudeCodeStatus?> RefreshClaudeStatusAsync()
    {
        if (_claudeStatus is null) return null;
        var check = ++_claudeStatusCheck;
        IsClaudeStatusChecking = true;
        ClaudeStatusText = "Checking Claude Code on this PC…";
        if (IsClaude && _client is null) AccountText = ClaudeStatusText;
        ClaudeCodeStatus status;
        try
        {
            status = await _claudeStatus(_lifetime.Token);
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            status = new ClaudeCodeStatus(ClaudeCodeState.Unavailable);
        }
        if (check != _claudeStatusCheck || _isShutDown) return null;
        IsClaudeStatusChecking = false;
        _claudeCode = status;
        ClaudeStatusText = DescribeClaudeCode(status);
        if (IsClaude) AccountText = ClaudeStatusText;
        return status;
    }

    internal static string DescribeClaudeCode(ClaudeCodeStatus status) => status.State switch
    {
        ClaudeCodeState.Ready => "Ready" + (status.Version is { } version ? $" (version {version})" : string.Empty),
        ClaudeCodeState.NotFound => "Not installed",
        ClaudeCodeState.TooOld => "Update needed",
        ClaudeCodeState.SignedOut => "Not signed in with a Claude account",
        _ => "Could not be checked"
    };

    internal static string DescribeClaudeProblem(ClaudeCodeStatus status) => status.State switch
    {
        ClaudeCodeState.NotFound => "Claude Code is not installed on this PC. Install it from claude.com/code, sign in once in a terminal, then press Connect.",
        ClaudeCodeState.TooOld => $"This Claude Code is too old for DwemerDistro (version {ClaudeCodeLocator.MinimumVersion} or newer is needed). Run claude update, then press Connect.",
        ClaudeCodeState.SignedOut => "Claude Code is not signed in with a Claude account. Run claude in a terminal and sign in with your Claude account, then press Connect.",
        _ => "Claude Code could not be checked. Make sure claude --version works in a terminal, then press Connect."
    };

    /// <summary>
    /// Connect's Claude gate: Core must offer the external engine with Claude, and local Claude Code must be ready. Neither
    /// needs a ChatGPT account. False means the state already says what to do.
    /// </summary>
    private async Task<bool> CheckClaudeReadyAsync(JsonElement capabilities)
    {
        var provider = _selectedProvider;
        var features = Prop(capabilities, "features");
        var engines = Prop(Prop(capabilities, "assistant_external_engine"), "engines");
        var offered = AssistantCliService.GetBool(features, "assistant_external_engine") && engines.ValueKind == JsonValueKind.Array
                      && engines.EnumerateArray().Any(engine => engine.ValueKind == JsonValueKind.String && engine.GetString() == ClaudeEngine);
        if (!offered || _toolRevision == 0)
        {
            SetState(AssistantState.RuntimeAbsent, ClaudeCoreTooOldText);
            return false;
        }
        SetState(AssistantState.Checking, "Checking Claude Code on this PC…");
        var status = await RefreshClaudeStatusAsync();
        if (!ReferenceEquals(provider, _selectedProvider) || _isShutDown || status is null) return false;
        if (status.State != ClaudeCodeState.Ready)
        {
            SetState(AssistantState.Error, DescribeClaudeProblem(status));
            StatusText = "Claude Code not ready";
            return false;
        }
        return true;
    }

    // --- Session and thread -------------------------------------------------------------------

    /// <summary>
    /// After <c>initialize</c> with <c>ddistroEngine:"claude"</c>: no account read, no model list, no sign-in. Opens or
    /// resumes the chat's external thread, then becomes Ready like a ChatGPT session.
    /// </summary>
    private async Task StartClaudeSessionAsync(CodexAppServerClient client)
    {
        AccountText = ClaudeStatusText;
        var previous = SelectedModel?.Id;
        Models.Clear();
        foreach (var model in ClaudeModels) Models.Add(model);
        await EnsureClaudeThreadAsync(client);
        if (!ReferenceEquals(_client, client)) return;
        // The chat's own saved model wins; the picker's previous choice only seeds a new chat.
        var preferred = _claudeChat?.Model is { } saved && ClaudeModels.Any(model => model.Id == saved) ? saved : previous ?? ClaudeDefaultModel;
        SelectedModel = ClaudeModels.FirstOrDefault(model => model.Id == preferred) ?? ClaudeModels.First(model => model.Id == ClaudeDefaultModel);
        await LoadRecoveryAsync(client, supplyContext: true);
        if (!ReferenceEquals(_client, client)) return;
        SetState(AssistantState.Ready, ReadyText());
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

    private async Task EnsureClaudeThreadAsync(CodexAppServerClient client)
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
            catch (AppServerException) when (client.IsConnected)
            {
                AddSystem("DwemerDistro could not resume this Claude chat (it may have been forgotten), so a new Claude chat was started. " +
                          "The earlier one stays in History.");
            }
        }
        if (!resumed)
            result = await client.RequestAsync("ddistro/external/thread/start", new JsonObject { ["engine"] = ClaudeEngine }, RpcTimeout);
        var threadId = AssistantCliService.GetString(result, "threadId");
        if (!Guid.TryParseExact(threadId, "D", out _) || AssistantCliService.GetString(result, "engine") != ClaudeEngine)
            throw new AppServerException(0, "DwemerDistro did not open a Claude chat.");
        if (resumed && threadId != _threadId) throw new AppServerException(0, "DwemerDistro resumed a different chat than this one.");
        var tools = Prop(result, "tools");
        try
        {
            if (AssistantMcpRelay.ParseTools(tools).Count == 0) throw new ArgumentException("No tools.");
        }
        catch (ArgumentException)
        {
            throw new AppServerException(0, "DwemerDistro did not declare usable tools for Claude, so the session was closed.");
        }
        _claudeTools = tools.Clone();
        if (!resumed && _threadId is { } replaced && replaced != threadId) KeepSuperseded(replaced);
        _threadToolRevision = _toolRevision;
        _threadId = threadId;
        SaveReference();
        OnPropertyChanged(nameof(HasReadTools));

        if (_shownThreadId != threadId || _claudeChat?.ThreadId != threadId)
        {
            ResetHistory();
            var saved = resumed ? _claudeChats?.Load(threadId) : null;
            if (resumed && saved is null)
                AddSystem("The launcher's local copy of this Claude chat is missing or unreadable, so its earlier messages cannot be shown. " +
                          "Claude starts a fresh session for it and will not remember them.");
            _claudeChat = saved ?? new ClaudeChat(ClaudeChat.CurrentVersion, threadId, Guid.NewGuid().ToString("D"), false,
                SelectedModel?.Id ?? ClaudeDefaultModel, []);
            if (saved is not null)
            {
                // Restored before the save below, so the picker's previous choice never overwrites this chat's model.
                if (ClaudeModels.FirstOrDefault(model => model.Id == saved.Model) is { } savedModel) SelectedModel = savedModel;
                Transcript.Clear();
                foreach (var message in saved.Messages) AddItem(new AssistantTranscriptItem(message.Role, message.Text));
            }
            _historyThreadId = threadId;
            // The local copy is shown whole; there are no Codex pages to load.
            _historyUnavailable = true;
            RaiseHistory();
            SaveClaudeChat();
        }
        _shownThreadId = threadId;
    }

    /// <summary>
    /// Claude Code created this chat's session (its init reported the requested id), so later turns must resume it. Saved
    /// now, not when the turn ends, so a crash or abort mid-turn never leads to reusing the id for a new session. Recorded
    /// even after the session closed or another chat was opened; only this chat's own record, and only for its own id.
    /// </summary>
    internal void RecordClaudeSessionStarted(string threadId, string sessionId)
    {
        if (_claudeChats is null || _isShutDown) return;
        if (_claudeChat is { } open && open.ThreadId == threadId)
        {
            if (open.SessionStarted || open.SessionId != sessionId) return;
            _claudeChat = open with { SessionStarted = true };
            if (_threadId == threadId)
            {
                SaveClaudeChat();
                return;
            }
        }
        if (_claudeChats.Load(threadId) is { SessionStarted: false } saved && saved.SessionId == sessionId)
            _claudeChats.Save(saved with { SessionStarted = true });
    }

    internal const string ClaudeSessionReplacedText =
        "Claude Code no longer has this chat's earlier session, so Claude started a fresh session for it. The earlier messages " +
        "stay shown here, but Claude does not remember them.";

    internal const string ClaudeSessionMissingText =
        "Claude Code no longer has this chat's earlier session, so the message was not answered. Send it again to continue in a " +
        "fresh Claude session; the earlier messages stay shown here, but Claude will not remember them.";

    /// <summary>
    /// Claude Code said <paramref name="chat"/>'s session does not exist, so the chat gets a new, not yet started session id;
    /// its messages, model and thread stay. Only this chat's own record, and only while it still names that session: the open
    /// chat (saved at once when it is shown), or else its saved file. Returns the open, shown chat's new record, else null.
    /// </summary>
    internal ClaudeChat? ForgetMissingClaudeSession(ClaudeChat chat)
    {
        if (_claudeChats is null || _isShutDown) return null;
        var sessionId = Guid.NewGuid().ToString("D");
        if (_claudeChat is { } open && open.ThreadId == chat.ThreadId)
        {
            if (open.SessionId != chat.SessionId || !open.SessionStarted) return null;
            _claudeChat = open with { SessionId = sessionId, SessionStarted = false };
            if (_threadId == chat.ThreadId)
            {
                SaveClaudeChat();
                return _claudeChat;
            }
        }
        if (_claudeChats.Load(chat.ThreadId) is { SessionStarted: true } saved && saved.SessionId == chat.SessionId)
            _claudeChats.Save(saved with { SessionId = sessionId, SessionStarted = false });
        return null;
    }

    /// <summary>Saves the shown conversation of the open Claude chat (bounded), with its session and model.</summary>
    private void SaveClaudeChat()
    {
        if (_claudeChats is null || _claudeChat is not { } chat || _threadId != chat.ThreadId || _isShutDown) return;
        var shown = Transcript.Where(item => ClaudeChat.Roles.Contains(item.Role) && item.Text.Length > 0)
            .Select(item => new ClaudeChatMessage(item.Role, item.Text)).ToArray();
        // The store keeps a bounded copy; the in-memory chat matches it, so Export never claims more than was kept.
        _claudeChat = ClaudeChatStore.Bound(chat with { Messages = shown, Model = SelectedModel?.Id ?? chat.Model });
        if (!_claudeChats.Save(_claudeChat))
            HistoryStatus = "This Claude chat's local copy could not be saved, so a reopened chat may not show its latest messages.";
    }

    // --- Turns --------------------------------------------------------------------------------

    /// <summary>
    /// One Claude turn: Core's <c>ddistro/external/turn/start</c> (the same model-turn reservation, queue and Stop as a
    /// Codex turn), then this turn's relay and <c>claude.exe</c>. Core's <c>turn/completed</c> stays authoritative for the
    /// UI; the process ends the turn with <c>ddistro/external/turn/end</c>. A turn that never started is reported unsent.
    /// </summary>
    private async Task StartClaudeTurnAsync(CodexAppServerClient client, AssistantTurnRunner turns, JsonArray input)
    {
        var threadId = _threadId!;
        var generation = _sessionGeneration;
        if (_claudeCode is not { State: ClaudeCodeState.Ready } code || _claudeRunner is null || _claudeChat is not { } chat)
        {
            AddSystem("The message was not sent: Claude Code is not ready. Disconnect and Connect to check it again.");
            SetState(AssistantState.Ready);
            return;
        }
        var model = SelectedModel is { } selected && ClaudeModels.Any(option => option.Id == selected.Id) ? selected.Id : ClaudeDefaultModel;
        _claudeStopRequested = false;
        SaveClaudeChat();

        await turns.StartAsync(threadId, new JsonObject { ["threadId"] = threadId }, "ddistro/external/turn/start");
        var turnId = turns.ActiveTurnId;
        if (!ReferenceEquals(_client, client) || generation != _sessionGeneration) return;
        // Stop pressed before the turn had an id: the runner interrupts it as soon as the id arrives; nothing is launched.
        if (_claudeStopRequested || turnId is null) return;
        await LaunchClaudeProcessAsync(client, generation, threadId, turnId, code, chat, model, input, recovering: false);
    }

    /// <summary>
    /// Starts <c>claude.exe</c> and its relay for Core's turn <paramref name="turnId"/>. <paramref name="recovering"/> marks the
    /// one fresh-session start after Claude Code reported the chat's session missing; that start is never repeated.
    /// </summary>
    private async Task LaunchClaudeProcessAsync(CodexAppServerClient client, int generation, string threadId, string turnId, ClaudeCodeStatus code,
        ClaudeChat chat, string model, JsonArray input, bool recovering)
    {
        ClaudeTurnProcess process;
        try
        {
            var relay = AssistantMcpRelay.Start(_claudeTools, threadId, turnId, AssistantMcpRelay.ForBroker(client));
            var request = new ClaudeTurnRequest(code.ExecutablePath, chat.SessionId, chat.SessionStarted, model, null, InstructionsFor(ActiveToolRevision), input);
            process = ClaudeTurnProcess.Start(request, relay, threadId, turnId, (method, parameters) => Post(generation, () => OnNotification(method, parameters)),
                _claudeRunner!, _claudeChats?.LocalAppDataDirectory,
                session => _dispatcher.BeginInvoke(() => RecordClaudeSessionStarted(threadId, session)));
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or InvalidOperationException
                                       or System.ComponentModel.Win32Exception or System.Net.Sockets.SocketException)
        {
            if (_claudeTurnId == turnId)
            {
                _claudeTurn = null;
                _claudeTurnId = null;
            }
            AddSystem("Claude Code could not start: " + Shorten(ex.Message, 200));
            await EndExternalTurnAsync(client, threadId, turnId, "failed");
            RaiseCommands();
            return;
        }
        _claudeTurn = process;
        _claudeTurnId = turnId;
        RaiseCommands();
        if (_claudeStopRequested) process.Stop();
        _ = FinishClaudeTurnAsync(client, generation, threadId, turnId, process, code, chat, model, input, recovering);
    }

    private async Task FinishClaudeTurnAsync(CodexAppServerClient client, int generation, string threadId, string turnId,
        ClaudeTurnProcess process, ClaudeCodeStatus code, ClaudeChat chat, string model, JsonArray input, bool recovered)
    {
        var result = await process.Completion;
        // Once Claude Code reported this session, later turns resume it, even if this session has since closed. Only an
        // allowed init proves that: a session id echoed by a refused init or a failed start was never created.
        if (!chat.SessionStarted && result.SessionReported && _claudeChat?.ThreadId == chat.ThreadId && _claudeChat.SessionId == chat.SessionId)
            _claudeChat = _claudeChat with { SessionStarted = true };

        if (result.Outcome == ClaudeTurnOutcome.SessionMissing && chat.SessionStarted)
        {
            var fresh = ForgetMissingClaudeSession(chat);
            // Claude Code ran nothing (no init, no model turn), so this same message goes once more, to the fresh session, in
            // the same Core turn: only while it is still this session's running turn, not stopped, and never a second time.
            if (fresh is not null && !recovered && ReferenceEquals(_claudeTurn, process) && ReferenceEquals(_client, client)
                && generation == _sessionGeneration && !_isShutDown && !_claudeStopRequested && _threadId == threadId
                && _turns?.ActiveTurnId == turnId)
            {
                AddSystem(ClaudeSessionReplacedText);
                SaveClaudeChat();
                await LaunchClaudeProcessAsync(client, generation, threadId, turnId, code, fresh, model, input, recovering: true);
                return;
            }
            result = result with { Message = ClaudeSessionMissingText };
        }

        if (ReferenceEquals(_claudeTurn, process))
        {
            _claudeTurn = null;
            _claudeTurnId = null;
        }

        // A user Stop already interrupted the turn through Core, and a broker turn/completed already ended it.
        if (result.Outcome != ClaudeTurnOutcome.Stopped)
            await EndExternalTurnAsync(client, threadId, turnId, result.Outcome == ClaudeTurnOutcome.Completed ? "completed" : "failed");
        if (generation != _sessionGeneration || _isShutDown)
        {
            SaveClaudeChat();
            return;
        }
        switch (result.Outcome)
        {
            case ClaudeTurnOutcome.SignInNeeded:
                AddSystem(result.Message);
                _claudeCode = null;
                ClaudeStatusText = DescribeClaudeCode(new ClaudeCodeStatus(ClaudeCodeState.SignedOut));
                AccountText = ClaudeStatusText;
                break;
            case ClaudeTurnOutcome.Refused or ClaudeTurnOutcome.Failed or ClaudeTurnOutcome.SessionMissing:
                AddSystem(result.Message);
                break;
        }
        SaveClaudeChat();
        RaiseCommands();
    }

    /// <summary>
    /// Ends the external turn in Core. Already ended (-32001) is fine; any other refusal asks Core to interrupt it, so the
    /// model-turn reservation is never left held. Nothing is retried beyond that, and no tool call is replayed.
    /// </summary>
    private async Task EndExternalTurnAsync(CodexAppServerClient client, string threadId, string turnId, string status)
    {
        if (!client.IsConnected) return;
        try
        {
            await client.RequestAsync("ddistro/external/turn/end", new JsonObject { ["threadId"] = threadId, ["turnId"] = turnId, ["status"] = status }, RpcTimeout);
            return;
        }
        catch (AppServerException ex) when (ex.Code == -32001)
        {
            return;
        }
        catch (AppServerException)
        {
        }
        try
        {
            await client.RequestAsync("turn/interrupt", new JsonObject { ["threadId"] = threadId, ["turnId"] = turnId }, RpcTimeout);
        }
        catch (AppServerException ex)
        {
            if (client.IsConnected) AddSystem("DwemerDistro did not confirm the end of this response: " + Shorten(ex.Message, 160) + " Press Stop to end it.");
        }
    }

    /// <summary>Ends the running Claude process now: the relay is revoked first, so no further tool call reaches Core.</summary>
    private void StopClaudeProcess()
    {
        _claudeStopRequested = true;
        _claudeTurn?.Stop();
    }

    /// <summary>Core's turn/completed for the running Claude turn ends the process, whatever the process thinks.</summary>
    private void OnExternalTurnCompleted(string threadId, string turnId)
    {
        if (_claudeTurn is { } running && threadId == _threadId && (turnId.Length == 0 || turnId == _claudeTurnId)) running.Stop();
    }

    // --- Export -------------------------------------------------------------------------------

    /// <summary>The open Claude chat as Markdown, from the launcher's local copy (Core keeps no Claude transcript).</summary>
    internal const string ClaudePartialExportText =
        "This is only the part of the chat the launcher kept locally: older messages were dropped or long ones shortened, as marked.";

    internal static string BuildClaudeExport(string title, IEnumerable<ClaudeChatMessage> messages, DateTimeOffset now, bool partial = false)
    {
        var builder = new StringBuilder();
        builder.Append("# ").AppendLine(title).AppendLine();
        builder.Append("Exported from the DwemerDistro Launcher on ").Append(now.ToString("yyyy-MM-dd HH:mm 'UTC'"))
            .AppendLine(". Local Claude chat, from the launcher's own copy.").AppendLine();
        if (partial) builder.AppendLine(ClaudePartialExportText).AppendLine();
        foreach (var message in messages.Where(message => message.Role is "You" or ClaudeRole))
            builder.Append("## ").AppendLine(message.Role).AppendLine().AppendLine(message.Text.TrimEnd()).AppendLine();
        return builder.ToString();
    }
}
