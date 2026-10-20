using System.Collections.ObjectModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows.Threading;
using DwemerDistro.Launcher.Wpf.Services;

namespace DwemerDistro.Launcher.Wpf.ViewModels;

public enum AssistantState
{
    NotChecked, Checking, CliAbsent, RuntimeAbsent, IsolationFailed, SignedOut, SigningIn,
    Ready, Busy, Streaming, Review, Applying, Error, Disconnected
}

/// <summary>What the Assistant tab needs from the launcher: the shared distro gate and a URL opener.</summary>
public interface IAssistantHost
{
    /// <summary>Holds off Compact, Export, Import and DNS repair for one assistant command.</summary>
    bool TryBeginDistroActivity();
    void EndDistroActivity();

    /// <summary>
    /// The same hold for an open session. Unlike a command's, the launcher may ask an idle session to close through
    /// <see cref="AssistantViewModel.SuspendForMaintenanceAsync"/> before it claims the distro.
    /// </summary>
    bool TryBeginSessionActivity();
    void EndSessionActivity();

    /// <summary>Claims the same slot server, component and system updates use, for live file changes.</summary>
    bool TryBeginLiveChange();
    void EndLiveChange();

    void OpenUrl(string url);

    /// <summary>The distro's WSL registration id, read locally; null when it is not registered.</summary>
    string? GetDistroInstallationId();
}

public sealed class AssistantTranscriptItem(string role, string text, string? itemId = null) : ObservableObject
{
    internal const int MaxTextChars = 64 * 1024;
    private string _text = Bound(text);

    public string Role { get; } = role;
    public string? ItemId { get; } = itemId;
    public string Text { get => _text; private set => SetProperty(ref _text, value); }

    internal void Append(string delta) => Text = Bound(_text + delta);
    internal void Replace(string text) => Text = Bound(text);

    private static string Bound(string value) =>
        value.Length <= MaxTextChars ? value : value[..MaxTextChars] + "\n[Message truncated.]";
}

public sealed record AssistantModelOption(string Id, string DisplayName)
{
    // The themed ComboBox renders items through ToString.
    public override string ToString() => DisplayName;
}

/// <summary>
/// The exact batch the user reviewed, with the validation warnings Core staged it with. It can be applied
/// once and only by its own hash.
/// </summary>
internal sealed record AssistantReviewBatch(string TaskId, string ManifestSha256, string Diff, IReadOnlyList<string> Files,
    IReadOnlyList<string> Warnings, int WarningTotal);

/// <summary>
/// Files the assistant asked for at the end of one completed turn. It is only a proposal: it is bound to the task,
/// thread and turn it came from, and nothing is copied until the user allows it while all three are still current.
/// </summary>
internal sealed record AssistantFileRequest(string TaskId, string ThreadId, int Turn, IReadOnlyList<string> Paths,
    string OriginalRequest, string Context);

internal enum FileRequestParse { None, Valid, Invalid }

public sealed class AssistantViewModel : ObservableObject
{
    internal const int MaxTranscriptItems = 200;
    internal const int MaxInputChars = 8000;
    // Core refuses to stage a diff over 8 MiB; the launcher shows every staged diff in full or refuses the review.
    private const int MaxDiffChars = 4 * 1024 * 1024;
    private static readonly TimeSpan QueryTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan PrepareTimeout = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan LiveChangeTimeout = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan RpcTimeout = TimeSpan.FromSeconds(45);
    // Read before every message; a slow or missing lookup must not hold the conversation up.
    private static readonly TimeSpan ModelsTimeout = TimeSpan.FromSeconds(10);
    // Core bounds its own server status read to 20 seconds; this covers that and the WSL round trip.
    private static readonly TimeSpan InventoryTimeout = TimeSpan.FromSeconds(30);
    internal const string FileRequestTag = "dwemer-file-request";
    private const int MaxContinuationContextChars = 4000;
    // Turns per history page, read with full items; Core's broker accepts at most 10. A page that fails is retried only on
    // request, smaller. A turn whose items are not loaded is read through thread/items/list, a bounded number of pages.
    internal const int HistoryPageTurns = 10;
    internal const int HistoryTurnItems = 50;
    internal const int MaxTurnItemPages = 8;
    private const int MaxCarriedMessages = 8;
    private const int MaxCarriedMessageChars = 1500;

    /// <summary>
    /// Sent as the thread's developer instructions. It describes the whole distro the assistant helps with, what it can
    /// really do, what the workspace holds and the one way to ask for files; the launcher, not the model, decides
    /// whether anything is copied.
    /// </summary>
    internal const string DeveloperInstructions =
        "You are the DwemerDistro Assistant. You help the user set up, check, understand and troubleshoot their whole DwemerDistro: " +
        "the managed game servers (CHIM/HerikaServer for Skyrim, LORKHAN, STOBE for Kenshi, Dialectic for Fallout: New Vegas/TTW, Reign), " +
        "the Dwemer Dashboard, voice and speech components (text to speech, speech to text), embeddings, the CHIM MCP server, and custom mods. " +
        "You are not limited to CHIM.\n" +
        "What you can do: read the launcher's per-message context described below, read and edit files in your isolated working copy at /work, " +
        "and explain. You cannot run commands on the live distro, install, update, start, stop or configure anything, or list or browse live " +
        "server folders. When the user asks for something you cannot execute, give concrete next steps grounded in the context (for example the " +
        "launcher page or button to use, or a ddistro command they can run themselves) and say plainly that you cannot do it from here. " +
        "Never claim you checked, ran or changed something you did not.\n" +
        "File editing currently supports only CHIM (HerikaServer) files: only they can be copied into /work, and they change live only after the " +
        "user reviews and approves the exact diff. Files of other servers, components or mods cannot be shared or edited through the Assistant yet. " +
        "A task with no selected files holds only .ddistro-context/status.json and .ddistro-context/recent.log (sanitized CHIM status and recent " +
        "CHIM log lines). Your edits stay in /work until the user reviews and approves them.\n" +
        "If you need CHIM files to read or edit, explain why in plain words, then end your message with exactly one block:\n" +
        "```" + FileRequestTag + "\n<one path per line, relative to the HerikaServer folder, e.g. ext/example/file.php>\n```\n" +
        "List at most 20 files, only paths you are confident exist or must be created. Never ask for credentials, .env files, keys, " +
        "databases, logs or user configuration; DwemerDistro refuses them. If you do not know the exact path, say so and ask the user " +
        "instead of guessing. The user decides; if they allow it, a fresh working copy with only those files is prepared and you are given " +
        "their request again. Do not write the block for any other purpose.\n" +
        "Each user message arrives after a separate input that starts with \"" + ContextLabel + "\". The launcher adds it, not the user. " +
        "It restates your scope, then holds a fresh DwemerDistro inventory and a fresh snapshot of CHIM's selected models, or statements that " +
        "they are unavailable. The inventory reports presence only: each server's install state, branch, version and whether its database " +
        "exists; the names of entries in the known extension folders (CHIM and Dialectic ext/); component startup markers; and registered " +
        "custom mods. Answer questions about what is installed, such as which server mods or extensions are installed, directly from it with " +
        "its capture time; never ask the user to paste a list it already holds. When asked to check or summarize a setup, give an " +
        "evidence-based summary from it and say what it cannot show: present does not mean enabled, loaded, running, healthy or correct, and " +
        "an inventory is not a code audit. When asked which models are selected, answer from the model snapshot: list them by profile and role " +
        "with the exact IDs, give the capture time, and say whether the list is truncated. It covers configured CHIM profiles, not which NPC or " +
        "profile is active in game. If any part is unavailable, say so. Never read configuration, database or credential files to find any of this.";

    /// <summary>First line of the per-message context, so a resumed thread with older instructions still sees what it is.</summary>
    internal const string ContextLabel = "[DwemerDistro context for this message - added by the launcher, not typed by the user]";

    /// <summary>
    /// Repeated in every message's context: a thread resumed from an older launcher keeps its original developer
    /// instructions, so the distro-wide scope and the CHIM-only file editing limit must arrive with each turn too.
    /// </summary>
    internal const string ScopeReminder =
        "Scope, which applies even if this conversation began with older instructions: you are the DwemerDistro Assistant for the whole " +
        "distro (all managed game servers, the Dwemer Dashboard, voice and speech components, and custom mods), not a CHIM-only helper. " +
        "File editing works only for CHIM (HerikaServer) files today. For anything you cannot execute, give grounded next steps and say what " +
        "cannot be done from here. Use the inventory below for what is installed instead of asking the user for it.";

    private readonly IAssistantCli _cli;
    private readonly IAssistantHost _host;
    private readonly IAssistantStateStore _store;
    private readonly Dispatcher _dispatcher;
    private CodexAppServerClient? _client;
    private AssistantTurnRunner? _turns;
    private int _sessionGeneration;
    private bool _ownsDistroActivity;
    private bool _startupConnectRequested;
    private bool _isShutDown;
    // The user, or startup on their behalf, wants a session; Disconnect clears it, so maintenance never reopens one the user closed.
    private bool _connectionIntent;
    // Distro maintenance closed the session and holds the distro; nothing connects until every operation that asked ends.
    private int _maintenanceHolds;
    private bool IsMaintenanceHeld => _maintenanceHolds > 0;
    private bool _reconnectAfterMaintenance;
    // One of the operations holding the distro did not end safely, so the session waits for Connect.
    private bool _maintenanceEndedUnsafely;
    // Core commands running now; each holds its own distro lease, so maintenance waits for them after closing the session.
    private int _commandsInFlight;
    // Older conversation pages for the thread the transcript shows, read on request through thread/turns/list, newest
    // first. Page k is always read with _historyPages[k]: its cursor (null for page 0) and the limit it was first read
    // with (0 until then), so a page read again holds the same turns.
    private string? _historyThreadId;
    private readonly List<(string? Cursor, int Limit)> _historyPages = [];
    // Messages shown from each page read, by page index.
    private readonly List<int> _historyPageItems = [];
    private bool _historyUnavailable;
    private bool _isLoadingHistory;
    private bool _historyLoadPending;
    private int _historyPageTurns = HistoryPageTurns;
    // The latest view shows pages [0, _latestPages) whole at its top, above them the newest _latestPartialItems
    // messages of page _latestPages once the cap trimmed some of it, and below them everything this session added.
    private int _latestPages;
    private int _latestPartialItems;
    // While an older window is shown, the latest view waits here and keeps receiving new messages; the window holds
    // pages _windowNewest.._windowOldest, oldest at its top, with _windowPageItems messages each (oldest page first).
    private List<AssistantTranscriptItem>? _latestView;
    private int _windowNewest;
    private int _windowOldest;
    private readonly List<int> _windowPageItems = [];
    // Changes whenever the transcript switches between the latest view and a window, so a page read across it is dropped.
    private int _historyViewGeneration;
    // Earlier tasks of this conversation line, newest last, kept so they stay reopenable and their receipts findable.
    private List<AssistantPriorTask> _earlierTasks = [];
    // Cancels Connect's own readiness reads and session start when the launcher closes.
    private readonly CancellationTokenSource _lifetime = new();
    private AssistantState _state = AssistantState.NotChecked;
    private string _statusText = string.Empty;
    private string _statusDetail = "The assistant connects after the launcher's startup checks, or press Connect. Nothing is sent until you ask.";
    private string _providerText = "Codex";
    private string _accountText = "Not connected";
    private AssistantModelOption? _selectedModel;
    private string _relativePathsText = string.Empty;
    private string _taskText = "No task yet";
    private string? _taskId;
    private string? _threadId;
    // The thread whose conversation the transcript already shows, so a reconnect never restores it twice.
    private string? _shownThreadId;
    private string? _installationId;
    private string _provider = string.Empty;
    private string _inputText = string.Empty;
    private string? _loginId;
    private string _signInUrl = string.Empty;
    private string _signInCode = string.Empty;
    private AssistantReviewBatch? _review;
    private string _outcomeText = string.Empty;
    private bool _canRollback;
    private bool _canCheckOutcome;
    private bool _hasChanges;
    private int _changeCheck;
    private int _turnSerial;
    private string _lastUserRequest = string.Empty;
    private AssistantTranscriptItem? _lastAgentItem;
    private string? _lastAgentText;
    private AssistantFileRequest? _fileRequest;
    private string? _pendingContinuation;
    private CancellationTokenSource? _contextLookup;

    /// <summary>
    /// Runs nothing and reads nothing: WSL, the saved task and the model are all touched on Connect, either the
    /// user's or the launcher's one <see cref="ConnectOnStartupAsync"/>.
    /// </summary>
    public AssistantViewModel(IAssistantCli cli, IAssistantHost host, IAssistantStateStore store)
    {
        _cli = cli;
        _host = host;
        _store = store;
        _dispatcher = Dispatcher.CurrentDispatcher;
        _statusText = Describe(AssistantState.NotChecked);
        ConnectCommand = new AsyncRelayCommand(ConnectAsync, () => _client is null && !IsMaintenanceHeld
            && State is not (AssistantState.Checking or AssistantState.Busy or AssistantState.Applying));
        RunProbeCommand = new AsyncRelayCommand(RunProbeAsync, () => State == AssistantState.IsolationFailed);
        SignInCommand = new AsyncRelayCommand(SignInAsync, () => State == AssistantState.SignedOut && _client is not null);
        OpenSignInUrlCommand = new RelayCommand(() => _host.OpenUrl(SignInUrl), () => Uri.TryCreate(SignInUrl, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps);
        // Stop only interrupts a response and Disconnect only closes an idle session, so a click that lands just as the
        // response ends does nothing instead of closing the session.
        StopCommand = new AsyncRelayCommand(InterruptAsync, () => State == AssistantState.Streaming);
        DisconnectCommand = new AsyncRelayCommand(DisconnectAsync, () => _client is not null && State is not (AssistantState.Streaming or AssistantState.Applying));
        LoadEarlierCommand = new AsyncRelayCommand(LoadEarlierAsync, () => HasEarlierHistory && !_isLoadingHistory && _client is not null
            && State is AssistantState.Ready or AssistantState.Streaming);
        LoadNewerCommand = new AsyncRelayCommand(LoadNewerAsync, () => IsBrowsingHistory && !_isLoadingHistory);
        BackToLatestCommand = new RelayCommand(ShowLatest, () => IsBrowsingHistory);
        SendCommand = new AsyncRelayCommand(SendAsync, () => State == AssistantState.Ready && InputText.Trim().Length > 0);
        NewTaskCommand = new AsyncRelayCommand(NewTaskAsync, () => State is AssistantState.Ready or AssistantState.Disconnected or AssistantState.Error or AssistantState.SignedOut);
        // An unconfirmed live change must be checked before another batch can be reviewed.
        ReviewCommand = new AsyncRelayCommand(ReviewAsync, () => State is AssistantState.Ready or AssistantState.Disconnected && _taskId is not null && !_canCheckOutcome);
        ApproveCommand = new AsyncRelayCommand(ApproveAsync, () => State == AssistantState.Review && _review is not null);
        RejectCommand = new RelayCommand(Reject, () => State == AssistantState.Review);
        RollbackCommand = new AsyncRelayCommand(RollbackAsync, () => _canRollback && _taskId is not null && State is AssistantState.Ready or AssistantState.Disconnected or AssistantState.Error);
        CheckOutcomeCommand = new AsyncRelayCommand(CheckOutcomeAsync, () => _canCheckOutcome && _taskId is not null && State is not (AssistantState.Applying or AssistantState.Busy));
        AllowFileAccessCommand = new AsyncRelayCommand(AllowFileAccessAsync, () => State == AssistantState.Ready && _fileRequest is not null);
        DeclineFileAccessCommand = new RelayCommand(DeclineFileAccess, () => _fileRequest is not null && State is not (AssistantState.Busy or AssistantState.Checking));
    }

    public AsyncRelayCommand ConnectCommand { get; }
    public AsyncRelayCommand RunProbeCommand { get; }
    public AsyncRelayCommand SignInCommand { get; }
    public RelayCommand OpenSignInUrlCommand { get; }
    public AsyncRelayCommand StopCommand { get; }
    public AsyncRelayCommand DisconnectCommand { get; }
    public AsyncRelayCommand LoadEarlierCommand { get; }
    public AsyncRelayCommand LoadNewerCommand { get; }
    public RelayCommand BackToLatestCommand { get; }
    public AsyncRelayCommand SendCommand { get; }
    public AsyncRelayCommand NewTaskCommand { get; }
    public AsyncRelayCommand ReviewCommand { get; }
    public AsyncRelayCommand ApproveCommand { get; }
    public RelayCommand RejectCommand { get; }
    public AsyncRelayCommand RollbackCommand { get; }
    public AsyncRelayCommand CheckOutcomeCommand { get; }
    public AsyncRelayCommand AllowFileAccessCommand { get; }
    public RelayCommand DeclineFileAccessCommand { get; }

    public ObservableCollection<AssistantTranscriptItem> Transcript { get; } = new();
    public ObservableCollection<AssistantModelOption> Models { get; } = new();

    /// <summary>Paths the assistant asked for, shown on the access card; empty when nothing is pending.</summary>
    public IReadOnlyList<string> FileRequestPaths => _fileRequest?.Paths ?? [];
    public bool HasFileRequest => _fileRequest is not null;

    /// <summary>Set from Core's own diff after a completed turn or a reopened task, never from the transcript.</summary>
    public bool HasChanges
    {
        get => _hasChanges;
        private set
        {
            if (!SetProperty(ref _hasChanges, value)) return;
            OnPropertyChanged(nameof(IsChangesVisible));
            OnPropertyChanged(nameof(IsSidePanelVisible));
            ReviewCommand.RaiseCanExecuteChanged();
        }
    }

    public bool IsChangesVisible => HasChanges && !IsReviewVisible;

    /// <summary>The right column exists only while it has something to show.</summary>
    public bool IsSidePanelVisible => IsChangesVisible || IsReviewVisible || HasOutcome;

    public AssistantState State
    {
        get => _state;
        private set
        {
            if (!SetProperty(ref _state, value)) return;
            OnPropertyChanged(nameof(IsSignInVisible));
            OnPropertyChanged(nameof(IsReviewVisible));
            OnPropertyChanged(nameof(IsChangesVisible));
            OnPropertyChanged(nameof(IsSidePanelVisible));
            OnPropertyChanged(nameof(IsProbeVisible));
        }
    }

    public string StatusText { get => _statusText; private set => SetProperty(ref _statusText, value); }
    public string StatusDetail { get => _statusDetail; private set => SetProperty(ref _statusDetail, value); }
    public string ProviderText { get => _providerText; private set => SetProperty(ref _providerText, value); }
    public string AccountText { get => _accountText; private set => SetProperty(ref _accountText, value); }
    public string TaskText { get => _taskText; private set => SetProperty(ref _taskText, value); }
    public string SignInUrl { get => _signInUrl; private set { if (SetProperty(ref _signInUrl, value)) OpenSignInUrlCommand.RaiseCanExecuteChanged(); } }
    public string SignInCode { get => _signInCode; private set { if (SetProperty(ref _signInCode, value)) OnPropertyChanged(nameof(HasSignInCode)); } }
    public bool HasSignInCode => SignInCode.Length > 0;
    public string OutcomeText
    {
        get => _outcomeText;
        private set
        {
            if (!SetProperty(ref _outcomeText, value)) return;
            OnPropertyChanged(nameof(HasOutcome));
            OnPropertyChanged(nameof(IsSidePanelVisible));
        }
    }
    public bool HasOutcome => OutcomeText.Length > 0;
    public string ReviewDiff => _review?.Diff ?? string.Empty;
    public string ReviewSummary => _review is null ? string.Empty
        : $"{_review.Files.Count} file(s), manifest {_review.ManifestSha256[..12]}…\n" + string.Join("\n", _review.Files);
    public bool HasReviewWarnings => _review is { WarningTotal: > 0 };

    /// <summary>Shown above Approve. A check DwemerDistro could not run is listed as a warning, never as passed.</summary>
    public string ReviewValidationText => _review switch
    {
        null => string.Empty,
        { WarningTotal: 0 } => "Validation: DwemerDistro reported no syntax errors or warnings. Only PHP and JSON files are syntax-checked.",
        var batch => $"Validation: {batch.WarningTotal} warning(s). These checks did NOT run or did not pass; review the files yourself before approving.\n"
            + string.Join("\n", batch.Warnings.Select(warning => "• " + Shorten(warning, 300)))
            + (batch.WarningTotal > batch.Warnings.Count ? $"\n…and {batch.WarningTotal - batch.Warnings.Count} more." : string.Empty)
    };

    public bool IsSignInVisible => State is AssistantState.SignedOut or AssistantState.SigningIn;
    public bool IsReviewVisible => State == AssistantState.Review && _review is not null;
    public bool IsProbeVisible => State == AssistantState.IsolationFailed;
    public bool IsApplying => State == AssistantState.Applying;

    /// <summary>The thread shown has older messages than the transcript shows that can still be read.</summary>
    public bool HasEarlierHistory => !_historyUnavailable && _historyThreadId is not null && _historyThreadId == _threadId
        && (_latestView is null ? _latestPages : _windowOldest + 1) < _historyPages.Count;

    /// <summary>The transcript shows an older window of the conversation; Newer messages or Back to latest return.</summary>
    public bool IsBrowsingHistory => _latestView is not null;

    internal bool IsLoadingHistory => _isLoadingHistory;

    /// <summary>The latest view, wherever it is held; new messages always go here.</summary>
    private IList<AssistantTranscriptItem> LatestItems => (IList<AssistantTranscriptItem>?)_latestView ?? Transcript;

    /// <summary>
    /// An open session with nothing in flight that the launcher may close for distro maintenance: a response, sign-in,
    /// review, live change or check keeps the distro held.
    /// </summary>
    internal bool CanYieldForMaintenance => !_isShutDown && _client is not null && !_isLoadingHistory
        && State is AssistantState.Ready or AssistantState.SignedOut;

    public AssistantModelOption? SelectedModel { get => _selectedModel; set => SetProperty(ref _selectedModel, value); }

    /// <summary>Optional working-copy files for the next task, one relative path per line.</summary>
    public string RelativePathsText { get => _relativePathsText; set => SetProperty(ref _relativePathsText, value ?? string.Empty); }

    public string InputText
    {
        get => _inputText;
        set
        {
            var bounded = (value ?? string.Empty).Length > MaxInputChars ? value![..MaxInputChars] : value ?? string.Empty;
            if (SetProperty(ref _inputText, bounded)) SendCommand.RaiseCanExecuteChanged();
        }
    }

    internal static string Describe(AssistantState state) => state switch
    {
        AssistantState.NotChecked => "Not connected",
        AssistantState.Checking => "Checking DwemerDistro…",
        AssistantState.CliAbsent => "DwemerDistro command unavailable",
        AssistantState.RuntimeAbsent => "Assistant runtime not installed",
        AssistantState.IsolationFailed => "Isolation check not passed",
        AssistantState.SignedOut => "Signed out",
        AssistantState.SigningIn => "Waiting for sign-in…",
        AssistantState.Ready => "Ready",
        AssistantState.Busy => "Working…",
        AssistantState.Streaming => "Responding…",
        AssistantState.Review => "Reviewing changes",
        AssistantState.Applying => "Applying changes…",
        AssistantState.Disconnected => "Disconnected",
        _ => "Error"
    };

    private void SetState(AssistantState state, string? detail = null)
    {
        State = state;
        StatusText = Describe(state);
        if (detail is not null) StatusDetail = detail;
        // Rollback and status availability can change without a state change.
        RaiseCommands();
    }

    /// <summary>Explains a blocked launcher close without changing the state or the running operation.</summary>
    internal void ShowCloseBlocked() =>
        StatusDetail = "Wait for the assistant to finish applying or rolling back reviewed changes before closing the launcher.";

    // --- Connection ---------------------------------------------------------------------------

    /// <summary>
    /// The launcher's one automatic Connect, after its startup checks. It does nothing once requested before, once the
    /// user has used Connect or the launcher is closing. Like Connect it only reads readiness: it never runs the isolation
    /// check, signs in, opens a page or sends a message, and any failure stays inline with Connect available.
    /// </summary>
    internal async Task ConnectOnStartupAsync()
    {
        if (_startupConnectRequested || _isShutDown || IsMaintenanceHeld) return;
        _startupConnectRequested = true;
        if (State != AssistantState.NotChecked || !ConnectCommand.CanExecute(null)) return;
        try
        {
            await ConnectAsync();
        }
        catch (Exception ex)
        {
            // Connect pressed by the user reports this in a dialog; the automatic one must not interrupt startup.
            LauncherLogService.Operation($"Assistant startup connection failed: {ex.GetType().Name}");
            if (!_isShutDown) await EndSessionAsync(AssistantState.Error, "The assistant could not connect: " + Shorten(ex.Message, 200));
        }
    }

    internal async Task ConnectAsync()
    {
        if (_isShutDown) return;
        if (IsMaintenanceHeld)
        {
            SetState(AssistantState.Disconnected, "Distro maintenance is running. Try again when it finishes.");
            return;
        }
        _connectionIntent = true;
        if (!_ownsDistroActivity)
        {
            if (!_host.TryBeginSessionActivity())
            {
                SetState(AssistantState.Error, "Distro maintenance is running. Try again when it finishes.");
                return;
            }
            _ownsDistroActivity = true;
        }

        try
        {
            SetState(AssistantState.Checking, "Reading DwemerDistro capabilities…");
            var capabilities = await RunCliAsync(AssistantCliCommands.Capabilities(), QueryTimeout, _lifetime.Token);
            if (!capabilities.Ok)
            {
                FailCli(capabilities);
                return;
            }
            if (!AssistantCliService.GetBool(Prop(capabilities.Data, "features"), "agent"))
            {
                SetState(AssistantState.RuntimeAbsent, "This DwemerDistro Core does not provide the assistant yet. Run Update Distro.");
                return;
            }

            var agent = await RunCliAsync(AssistantCliCommands.AgentStatus(), QueryTimeout, _lifetime.Token);
            if (!agent.Ok)
            {
                FailCli(agent);
                return;
            }
            var provider = AssistantCliService.GetString(agent.Data, "provider");
            var version = AssistantCliService.GetString(agent.Data, "version");
            var reason = DescribeAgentReason(AssistantCliService.GetString(agent.Data, "reason"));
            _provider = provider.Length > 0 ? provider : "codex";
            ProviderText = (provider.Length > 0 ? provider : "Codex") + (version.Length > 0 ? " " + version : string.Empty);
            if (!AssistantCliService.GetBool(agent.Data, "installed"))
            {
                SetState(AssistantState.RuntimeAbsent, reason.Length > 0 ? reason : "The Codex runtime is not installed in DwemerDistro.");
                return;
            }
            if (!AssistantCliService.GetBool(agent.Data, "probe_passed"))
            {
                SetState(AssistantState.IsolationFailed, (reason.Length > 0 ? reason + " " : string.Empty) +
                    "The assistant stays off until its isolation check passes.");
                return;
            }

            if (_taskId is null && !await OpenTaskAsync()) return;
            if (_isShutDown) return;
            await StartSessionAsync();
        }
        catch (OperationCanceledException) when (_isShutDown)
        {
            // The launcher is closing; nothing more is started and the state no longer matters.
        }
        finally
        {
            if (_client is null) ReleaseDistroActivity();
        }
    }

    /// <summary>Core's <c>agent status</c> reason is a token; unknown tokens are shown, bounded, rather than hidden.</summary>
    internal static string DescribeAgentReason(string reason) => reason switch
    {
        "" or "ready" => string.Empty,
        "probe_required" => "The isolation check has not run on this DwemerDistro yet.",
        "probe_stale" => "The isolation check is out of date: the runtime, its policy or the kernel changed since it passed.",
        "not_installed" => "The Codex runtime is not installed in DwemerDistro.",
        // Core tests bubblewrap for the features the sandbox uses, not for a version: distro builds backport them.
        "bwrap_missing" => "DwemerDistro has no bubblewrap, which the assistant sandbox requires. " +
                           "The assistant runtime installer adds it when run with --install-prerequisites.",
        "bwrap_unsupported" => "DwemerDistro's bubblewrap lacks a feature the assistant sandbox requires " +
                               "(--bind-fd, --ro-bind-data, --clearenv, --die-with-parent). Install a supported bubblewrap that provides them.",
        "unsupported_platform" => "The assistant runtime needs a 64-bit x86 DwemerDistro.",
        _ => $"The assistant runtime is not usable ({Shorten(reason, 64)})."
    };

    /// <summary>
    /// Reopens the saved task only when it belongs to this installation and provider and Core still
    /// knows it. A new task is prepared only when nothing is saved; anything else waits for New task.
    /// </summary>
    private async Task<bool> OpenTaskAsync()
    {
        _installationId = _host.GetDistroInstallationId();
        var saved = _store.Load();
        if (saved is null) return await PrepareTaskAsync();
        if (saved.Distro != _cli.DistroName || !saved.BelongsTo(_installationId, _provider))
        {
            SetState(AssistantState.Error, $"The saved task {saved.TaskId} belongs to a different DwemerDistro installation or assistant provider, " +
                "so it was not reused. Nothing was applied. Press New task to start a fresh task.");
            return false;
        }

        SetState(AssistantState.Checking, $"Checking saved task {saved.TaskId}…");
        var status = await RunCliAsync(AssistantCliCommands.ChangeStatus(saved.TaskId), QueryTimeout, _lifetime.Token);
        if (!status.Ok && (!status.HasDocument || status.ErrorCode == "busy"))
        {
            // Unknown rather than gone: keep the reference so Connect can try again.
            FailCli(status);
            return false;
        }
        var product = AssistantCliService.GetString(status.Data, "product");
        if (!status.Ok || AssistantCliService.GetString(status.Data, "task_id") != saved.TaskId || product.Length > 0 && product != saved.Product)
        {
            SetState(AssistantState.Error, $"The saved task {saved.TaskId} is no longer available for CHIM in DwemerDistro" +
                (status.Ok ? string.Empty : $" ({Shorten(status.ErrorCode, 40)})") + ". Nothing was applied. Press New task to start a fresh task.");
            return false;
        }

        _taskId = saved.TaskId;
        _threadId = saved.ThreadId;
        _earlierTasks = saved.EarlierTasks?.ToList() ?? [];
        SetLiveChangeFlags(AssistantCliService.GetString(status.Data, "state"));
        TaskText = $"Task {saved.TaskId} · CHIM · reopened";
        return true;
    }

    private async Task<bool> PrepareTaskAsync()
    {
        var paths = RelativePathsText.Split('\n').Select(line => line.Trim()).Where(line => line.Length > 0).ToArray();
        var invalid = paths.FirstOrDefault(path => !AssistantCliCommands.IsSafeRelativePath(path));
        if (invalid is not null || paths.Length > AssistantCliCommands.MaxRelativePaths)
        {
            SetState(AssistantState.Error, invalid is not null
                ? $"\"{Shorten(invalid, 80)}\" is not a plain path relative to the product folder."
                : $"Choose at most {AssistantCliCommands.MaxRelativePaths} files.");
            return false;
        }

        if (_isShutDown) return false;
        SetState(AssistantState.Checking, "Preparing an isolated working copy…");
        // Core's prepare is not cut short, but a task it finishes after the launcher began closing is neither used nor saved.
        var prepared = await RunCliAsync(AssistantCliCommands.Prepare("herika", paths), PrepareTimeout, CancellationToken.None);
        if (_isShutDown) return false;
        if (!prepared.Ok)
        {
            FailCli(prepared);
            return false;
        }
        var taskId = AssistantCliService.GetString(prepared.Data, "task_id");
        if (!AssistantCliCommands.IsValidTaskId(taskId))
        {
            SetState(AssistantState.Error, "DwemerDistro returned an invalid task id.");
            return false;
        }
        UseTask(taskId, paths.Length);
        return true;
    }

    /// <summary>
    /// Keeps the current task's ids, newest last, before another task replaces it, so its conversation and any receipt
    /// recorded by Core stay findable. Nothing is copied from it.
    /// </summary>
    private void RememberCurrentTask()
    {
        if (_taskId is null) return;
        _earlierTasks.RemoveAll(task => task.TaskId == _taskId);
        _earlierTasks.Add(new AssistantPriorTask(_taskId, _threadId, _canRollback || _canCheckOutcome));
        if (_earlierTasks.Count > AssistantTaskReference.MaxEarlierTasks)
            _earlierTasks.RemoveRange(0, _earlierTasks.Count - AssistantTaskReference.MaxEarlierTasks);
    }

    /// <summary>Makes a freshly prepared task current: no thread yet, nothing to review or roll back, saved for this installation.</summary>
    private void UseTask(string taskId, int fileCount)
    {
        _taskId = taskId;
        _threadId = null;
        _canRollback = false;
        _canCheckOutcome = false;
        ClearFileRequest();
        HasChanges = false;
        SaveReference();
        TaskText = $"Task {taskId} · CHIM · " + (fileCount == 0 ? "diagnostic workspace (no files)" : $"{fileCount} file(s)");
    }

    private async Task StartSessionAsync()
    {
        SetState(AssistantState.Checking, "Starting the assistant session…");
        var generation = ++_sessionGeneration;
        IAssistantTransport transport;
        try
        {
            transport = await _cli.StartServeAsync(_taskId!, _lifetime.Token);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or ArgumentException)
        {
            SetState(AssistantState.Error, "The assistant session could not start: " + Shorten(ex.Message, 200));
            return;
        }
        var client = new CodexAppServerClient(transport);
        var turns = new AssistantTurnRunner(client, RpcTimeout);
        turns.StopFailed += message => Post(generation, () => AddSystem("Could not stop the response: " + Shorten(message, 200)));
        client.NotificationReceived += (method, parameters) => Post(generation, () => OnNotification(method, parameters));
        client.ServerRequestDeclined += method => Post(generation, () => AddSystem(DescribeDeclined(method)));
        client.Disconnected += reason => Post(generation, () => OnDisconnected(reason, transport.DiagnosticTail));
        _client = client;
        _turns = turns;
        client.Start();
        RaiseCommands();

        try
        {
            await client.RequestAsync("initialize", new JsonObject
            {
                ["clientInfo"] = new JsonObject
                {
                    ["name"] = "dwemerdistro_launcher",
                    ["title"] = "DwemerDistro Launcher",
                    ["version"] = LauncherConstants.LauncherVersion
                }
            }, RpcTimeout);
            await client.NotifyAsync("initialized", null);
            await RefreshAccountAsync(client);
        }
        catch (AppServerException ex)
        {
            if (ReferenceEquals(_client, client)) await EndSessionAsync(AssistantState.Error, ex.Message);
        }
    }

    private async Task RefreshAccountAsync(CodexAppServerClient client)
    {
        var account = await client.RequestAsync("account/read", new JsonObject { ["refreshToken"] = false }, RpcTimeout);
        var signedIn = account.ValueKind == JsonValueKind.Object && account.TryGetProperty("account", out var details) && details.ValueKind == JsonValueKind.Object;
        if (!signedIn && AssistantCliService.GetBool(account, "requiresOpenaiAuth"))
        {
            AccountText = "Signed out";
            SetState(AssistantState.SignedOut, "Sign in with ChatGPT to use Codex. The launcher shows the sign-in page and code; nothing opens until you click.");
            return;
        }

        AccountText = signedIn ? DescribeAccount(account.GetProperty("account")) : "No sign-in required";
        await LoadModelsAsync(client);
        await EnsureThreadAsync(client);
        SignInUrl = string.Empty;
        SignInCode = string.Empty;
        SetState(AssistantState.Ready, "Ask about your DwemerDistro setup, or describe a CHIM fix. Edits stay in an isolated copy until you review and approve them.");
        if (_historyLoadPending)
        {
            _historyLoadPending = false;
            await LoadEarlierAsync(client, initial: true);
            if (!ReferenceEquals(_client, client)) return;
        }
        // A request the user allowed files for continues in the new workspace's first turn.
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

    private static string DescribeAccount(JsonElement account)
    {
        var email = AssistantCliService.GetString(account, "email");
        var plan = AssistantCliService.GetString(account, "planType");
        var type = AssistantCliService.GetString(account, "type");
        var who = email.Length > 0 ? email : type == "apiKey" ? "API key" : "Signed in";
        return plan.Length > 0 ? $"{who} ({plan})" : who;
    }

    private async Task LoadModelsAsync(CodexAppServerClient client)
    {
        var previous = SelectedModel?.Id;
        var options = new List<AssistantModelOption>();
        string? defaultId = null;
        string? cursor = null;
        for (var page = 0; page < 5; page++)
        {
            var request = new JsonObject { ["limit"] = 50 };
            if (cursor is not null) request["cursor"] = cursor;
            var result = await client.RequestAsync("model/list", request, RpcTimeout);
            if (Prop(result, "data") is { ValueKind: JsonValueKind.Array } data)
            {
                foreach (var model in data.EnumerateArray())
                {
                    var id = AssistantCliService.GetString(model, "id");
                    if (id.Length == 0 || AssistantCliService.GetBool(model, "hidden")) continue;
                    var name = AssistantCliService.GetString(model, "displayName");
                    options.Add(new AssistantModelOption(id, name.Length > 0 ? name : id));
                    if (AssistantCliService.GetBool(model, "isDefault")) defaultId = id;
                }
            }
            cursor = AssistantCliService.GetString(result, "nextCursor");
            if (cursor.Length == 0) break;
        }

        Models.Clear();
        foreach (var option in options) Models.Add(option);
        SelectedModel = options.FirstOrDefault(o => o.Id == previous) ?? options.FirstOrDefault(o => o.Id == defaultId) ?? options.FirstOrDefault();
        if (SelectedModel is null) throw new AppServerException(0, "No models are available for this account.");
    }

    private async Task EnsureThreadAsync(CodexAppServerClient client)
    {
        JsonElement result = default;
        var resumed = false;
        if (_threadId is { } existing)
        {
            try
            {
                // A long conversation can exceed the launcher's 4 MiB line limit, so history is read in pages afterwards.
                var resume = new JsonObject { ["threadId"] = existing, ["developerInstructions"] = DeveloperInstructions, ["excludeTurns"] = true };
                CodexAppServerClient.AddConfinement(resume);
                result = await client.RequestAsync("thread/resume", resume, RpcTimeout);
                resumed = true;
            }
            catch (AppServerException) when (client.IsConnected)
            {
                AddSystem("The earlier conversation for this task could not be resumed, so a new one was started.");
            }
        }
        if (!resumed)
        {
            var start = new JsonObject { ["developerInstructions"] = DeveloperInstructions };
            if (SelectedModel is not null) start["model"] = SelectedModel.Id;
            CodexAppServerClient.AddConfinement(start);
            result = await client.RequestAsync("thread/start", start, RpcTimeout);
        }
        var threadId = AssistantCliService.GetString(Prop(result, "thread"), "id");
        if (threadId.Length == 0) throw new AppServerException(0, "The assistant did not open a conversation thread.");
        if (!CodexAppServerClient.IsConfined(result))
            throw new AppServerException(0, "The assistant did not confirm it is confined to the task's isolated workspace, so the session was closed.");
        if (resumed && threadId != _threadId) throw new AppServerException(0, "The assistant resumed a different conversation than this task's.");
        _threadId = threadId;
        SaveReference();
        if (_shownThreadId != threadId)
        {
            // Read once per thread shown: a reconnect, including one after a page that closed the session, never repeats it.
            ResetHistory();
            _historyThreadId = threadId;
            if (resumed) _historyPages.Add((null, 0));
            _historyLoadPending = resumed;
            RaiseHistory();
        }
        _shownThreadId = threadId;
    }

    /// <summary>
    /// Reads page <paramref name="page"/> of the shown thread through rust-v0.163.0 <c>thread/turns/list</c>: newest turns
    /// first, full items. A turn whose items did not come with it is read through <c>thread/items/list</c>, following its
    /// cursor for up to <see cref="MaxTurnItemPages"/> pages; a turn with more is counted in the omitted messages, never
    /// dropped silently. Only the user's own text and the assistant's messages are returned, oldest first.
    /// </summary>
    private async Task<(List<AssistantTranscriptItem> Items, int Omitted)> ReadHistoryPageAsync(CodexAppServerClient client, string threadId, int page)
    {
        var (cursor, limit) = _historyPages[page];
        if (limit == 0) limit = _historyPageTurns;
        var request = new JsonObject { ["threadId"] = threadId, ["limit"] = limit, ["sortDirection"] = "desc", ["itemsView"] = "full" };
        if (cursor is not null) request["cursor"] = cursor;
        var result = await client.RequestAsync("thread/turns/list", request, RpcTimeout);
        var turns = Prop(result, "data") is { ValueKind: JsonValueKind.Array } data ? data.EnumerateArray().Reverse().ToList() : [];
        var items = new List<JsonElement>();
        var omitted = 0;
        foreach (var turn in turns)
        {
            if (AssistantCliService.GetString(turn, "itemsView") == "full")
            {
                if (Prop(turn, "items") is { ValueKind: JsonValueKind.Array } turnItems) items.AddRange(turnItems.EnumerateArray());
                continue;
            }
            // Summary or unloaded items may leave text out, so the turn's own items are read in full.
            string? itemCursor = null;
            for (var itemPage = 0; ; itemPage++)
            {
                if (itemPage == MaxTurnItemPages)
                {
                    omitted++;
                    break;
                }
                var itemsRequest = new JsonObject
                {
                    ["threadId"] = threadId, ["turnId"] = AssistantCliService.GetString(turn, "id"), ["limit"] = HistoryTurnItems
                };
                if (itemCursor is not null) itemsRequest["cursor"] = itemCursor;
                var listed = await client.RequestAsync("thread/items/list", itemsRequest, RpcTimeout);
                if (Prop(listed, "data") is { ValueKind: JsonValueKind.Array } entries)
                    items.AddRange(entries.EnumerateArray().Select(entry => Prop(entry, "item")));
                itemCursor = AssistantCliService.GetString(listed, "nextCursor") is { Length: > 0 } nextItems ? nextItems : null;
                if (itemCursor is null) break;
            }
        }

        // The page's bounds are fixed by its first successful read; the next page becomes known through its cursor.
        _historyPages[page] = (cursor, limit);
        while (_historyPageItems.Count <= page) _historyPageItems.Add(0);
        var restored = ReadItems(items, ref omitted);
        _historyPageItems[page] = restored.Count;
        if (AssistantCliService.GetString(result, "nextCursor") is { Length: > 0 } next && _historyPages.Count == page + 1)
            _historyPages.Add((next, 0));
        return (restored, omitted);
    }

    /// <summary>
    /// Shows the page older than the transcript's top. The latest view takes it while it fits whole under the cap; past
    /// that the transcript becomes an older window, which slides: the page goes on top and newer pages leave the bottom.
    /// Nothing is sent, offered or applied. A page that fails is not retried on its own; Load earlier tries again.
    /// </summary>
    private async Task LoadEarlierAsync(CodexAppServerClient client, bool initial)
    {
        if (_isLoadingHistory || _historyThreadId is not { } threadId || threadId != _threadId || !HasEarlierHistory) return;
        var page = _latestView is null ? _latestPages : _windowOldest + 1;
        var generation = _historyViewGeneration;
        var read = await ReadHistoryAsync(client, threadId, page, initial);
        if (read is not var (items, omitted) || generation != _historyViewGeneration) return;
        if (_latestView is null && (initial || _latestPartialItems == 0 && Transcript.Count + items.Count <= MaxTranscriptItems))
        {
            for (var index = 0; index < items.Count; index++) Transcript.Insert(index, items[index]);
            _latestPages = page + 1;
            TrimTranscript();
        }
        else
        {
            if (_latestView is null) EnterWindow(page);
            _windowOldest = page;
            _windowPageItems.Insert(0, items.Count);
            for (var index = 0; index < items.Count; index++) Transcript.Insert(index, items[index]);
            while (Transcript.Count > MaxTranscriptItems && _windowNewest < _windowOldest)
            {
                for (var removed = _windowPageItems[^1]; removed > 0; removed--) Transcript.RemoveAt(Transcript.Count - 1);
                _windowPageItems.RemoveAt(_windowPageItems.Count - 1);
                _windowNewest++;
            }
        }
        ReportOmitted(omitted, initial);
        RaiseHistory();
    }

    /// <summary>
    /// Moves an older window one page toward the present: the page goes on the bottom and older pages leave the top. Once
    /// the next newer page is already whole in the latest view, the latest view comes back instead.
    /// </summary>
    private async Task LoadNewerAsync()
    {
        if (_latestView is null || _isLoadingHistory) return;
        var page = _windowNewest - 1;
        if (page < _latestPages || _client is not { } client || _historyThreadId is not { } threadId || threadId != _threadId)
        {
            ShowLatest();
            return;
        }
        var generation = _historyViewGeneration;
        var read = await ReadHistoryAsync(client, threadId, page, initial: false);
        if (read is not var (items, omitted) || generation != _historyViewGeneration || _latestView is null) return;
        _windowNewest = page;
        _windowPageItems.Add(items.Count);
        foreach (var item in items) Transcript.Add(item);
        while (Transcript.Count > MaxTranscriptItems && _windowOldest > _windowNewest)
        {
            for (var removed = _windowPageItems[0]; removed > 0; removed--) Transcript.RemoveAt(0);
            _windowPageItems.RemoveAt(0);
            _windowOldest--;
        }
        ReportOmitted(omitted, initial: false);
        RaiseHistory();
    }

    private async Task<(List<AssistantTranscriptItem> Items, int Omitted)?> ReadHistoryAsync(
        CodexAppServerClient client, string threadId, int page, bool initial)
    {
        _isLoadingHistory = true;
        RaiseHistory();
        try
        {
            var read = await ReadHistoryPageAsync(client, threadId, page);
            return ReferenceEquals(_client, client) && threadId == _historyThreadId ? read : null;
        }
        catch (AppServerException ex)
        {
            // A newer session owns the transcript now; a page that closed this one is still reported.
            if (_client is not null && !ReferenceEquals(_client, client) || threadId != _historyThreadId) return null;
            string note;
            if (ex.Code is -32602 or CodexAppServerClient.MethodNotFound)
            {
                _historyUnavailable = true;
                note = "This DwemerDistro Core cannot show this conversation's earlier messages yet. Run Update Distro to see them; they were not deleted.";
            }
            else
            {
                // Only a page never read can shrink; one already read keeps its bounds so it holds the same turns.
                var unread = _historyPages[page].Limit == 0;
                if (unread) _historyPageTurns = Math.Max(1, _historyPageTurns / 2);
                note = "Earlier messages could not be loaded: " + Shorten(ex.Message, 160) +
                       " They are kept in the conversation. Trying again reads them" + (unread ? " in a smaller page." : " again.");
            }
            if (initial) AddSystem(note);
            else StatusDetail = note;
            return null;
        }
        finally
        {
            _isLoadingHistory = false;
            RaiseHistory();
        }
    }

    private void ReportOmitted(int omitted, bool initial)
    {
        if (omitted == 0) return;
        if (initial) AddSystem($"{omitted} earlier message(s) in this conversation could not be shown here.");
        else StatusDetail = $"{omitted} earlier message(s) could not be shown here.";
    }

    private async Task LoadEarlierAsync()
    {
        if (_client is { } client) await LoadEarlierAsync(client, initial: false);
    }

    /// <summary>Keeps the latest view aside and starts an empty window just below <paramref name="page"/>.</summary>
    private void EnterWindow(int page)
    {
        _latestView = Transcript.ToList();
        _historyViewGeneration++;
        Transcript.Clear();
        _windowPageItems.Clear();
        _windowNewest = page;
        _windowOldest = page - 1;
        StatusDetail = "Showing earlier messages. Newer messages or Back to latest returns, and so does sending a message.";
    }

    /// <summary>Shows the latest view again, with everything added to it while a window was shown.</summary>
    private void ShowLatest()
    {
        if (_latestView is not { } latest) return;
        _latestView = null;
        _historyViewGeneration++;
        _windowPageItems.Clear();
        Transcript.Clear();
        foreach (var item in latest) Transcript.Add(item);
        RaiseHistory();
    }

    /// <summary>Forgets the shown thread's pages and returns to the latest view; its messages stay shown.</summary>
    private void ResetHistory()
    {
        ShowLatest();
        _historyThreadId = null;
        _historyPages.Clear();
        _historyPageItems.Clear();
        _historyUnavailable = false;
        _historyPageTurns = HistoryPageTurns;
        _latestPages = 0;
        _latestPartialItems = 0;
        RaiseHistory();
    }

    /// <summary>
    /// The latest view keeps its newest messages. A history message that leaves its top makes its page partial, so Load
    /// earlier reads that page whole into a window and nothing between the window and the latest view is skipped.
    /// </summary>
    private void TrimTranscript()
    {
        var latest = LatestItems;
        if (latest.Count <= MaxTranscriptItems) return;
        while (latest.Count > MaxTranscriptItems)
        {
            latest.RemoveAt(0);
            if (_latestPartialItems > 0)
            {
                _latestPartialItems--;
                continue;
            }
            while (_latestPages > 0)
            {
                _latestPages--;
                if (_historyPageItems[_latestPages] > 0)
                {
                    _latestPartialItems = _historyPageItems[_latestPages] - 1;
                    break;
                }
            }
        }
        RaiseHistory();
    }

    private void RaiseHistory()
    {
        OnPropertyChanged(nameof(HasEarlierHistory));
        OnPropertyChanged(nameof(IsBrowsingHistory));
        LoadEarlierCommand.RaiseCanExecuteChanged();
        LoadNewerCommand.RaiseCanExecuteChanged();
        BackToLatestCommand.RaiseCanExecuteChanged();
    }

    /// <summary>
    /// A thread's conversation from rust-v0.163.0 thread.turns[].items[], in order. A userMessage keeps each text input the
    /// launcher sent as its own content entry, so the labeled per-message context is dropped as a whole entry, never cut out
    /// of the user's text. Reasoning, commands, tool calls and file changes stay hidden.
    /// </summary>
    internal static List<AssistantTranscriptItem> ReadConversation(JsonElement thread, out int omitted)
    {
        omitted = 0;
        if (Prop(thread, "turns") is not { ValueKind: JsonValueKind.Array } turns) return [];
        var items = turns.EnumerateArray().Where(turn => Prop(turn, "items").ValueKind == JsonValueKind.Array)
            .SelectMany(turn => Prop(turn, "items").EnumerateArray());
        return ReadItems(items, ref omitted);
    }

    private static List<AssistantTranscriptItem> ReadItems(IEnumerable<JsonElement> items, ref int omitted)
    {
        var restored = new List<AssistantTranscriptItem>();
        foreach (var item in items)
        {
            var id = AssistantCliService.GetString(item, "id");
            switch (AssistantCliService.GetString(item, "type"))
            {
                case "userMessage":
                    var text = ReadUserText(Prop(item, "content"));
                    if (text is null) omitted++;
                    // The first message of a workspace the user allowed files for is the launcher's, not the user's.
                    else if (text.StartsWith(HandoffLabel, StringComparison.Ordinal) || text.StartsWith(ContinuationPrefix, StringComparison.Ordinal))
                        restored.Add(new AssistantTranscriptItem("DwemerDistro",
                            "Continued your request in a new working copy with the files you allowed.", id));
                    else if (text.Length > 0) restored.Add(new AssistantTranscriptItem("You", text, id));
                    break;
                case "agentMessage":
                    var message = AssistantCliService.GetString(item, "text");
                    // Shown as it was after its turn; an old file request is never offered again.
                    if (ParseFileRequest(message, out _, out var visible, out _) == FileRequestParse.Valid) message = visible;
                    if (message.Length > 0) restored.Add(new AssistantTranscriptItem("Codex", message, id));
                    break;
            }
        }
        return restored;
    }

    /// <summary>
    /// The user's own text from one userMessage's content entries, exactly as sent, or null when it cannot be shown
    /// safely: the launcher's context label anywhere but the start of its own entry hides the whole message.
    /// </summary>
    private static string? ReadUserText(JsonElement content)
    {
        if (content.ValueKind != JsonValueKind.Array) return null;
        var parts = new List<string>();
        var other = false;
        foreach (var entry in content.EnumerateArray())
        {
            if (AssistantCliService.GetString(entry, "type") != "text" || Prop(entry, "text").ValueKind != JsonValueKind.String)
            {
                other = true;
                continue;
            }
            var text = Prop(entry, "text").GetString()!;
            if (text.StartsWith(ContextLabel, StringComparison.Ordinal)) continue;
            if (text.Contains(ContextLabel, StringComparison.Ordinal)) return null;
            parts.Add(text);
        }
        return parts.Count == 0 && other ? null : string.Join("\n\n", parts);
    }

    /// <summary>Identifiers only; the transcript and the account stay out of launcher storage. Never rewritten once closing.</summary>
    private void SaveReference()
    {
        if (_isShutDown || _taskId is null || _installationId is null) return;
        _store.Save(new AssistantTaskReference(AssistantTaskReference.CurrentVersion, _installationId, _cli.DistroName,
            _provider, "herika", _taskId, _threadId) { EarlierTasks = _earlierTasks.Count > 0 ? _earlierTasks.ToArray() : null });
    }

    private async Task SignInAsync()
    {
        var client = _client!;
        SetState(AssistantState.SigningIn, "Requesting a sign-in code…");
        try
        {
            JsonElement login;
            try
            {
                login = await client.RequestAsync("account/login/start", new JsonObject { ["type"] = "chatgptDeviceCode" }, RpcTimeout);
            }
            catch (AppServerException ex) when (ex.Code is -32600 or -32602 or CodexAppServerClient.MethodNotFound)
            {
                login = await client.RequestAsync("account/login/start", new JsonObject { ["type"] = "chatgpt" }, RpcTimeout);
            }
            _loginId = AssistantCliService.GetString(login, "loginId");
            var verification = AssistantCliService.GetString(login, "verificationUrl");
            SignInUrl = verification.Length > 0 ? verification : AssistantCliService.GetString(login, "authUrl");
            SignInCode = AssistantCliService.GetString(login, "userCode");
            SetState(AssistantState.SigningIn, SignInCode.Length > 0
                ? "Open the sign-in page and enter the code shown here."
                : "Open the sign-in page to finish signing in.");
        }
        catch (AppServerException ex)
        {
            SetState(AssistantState.SignedOut, "Sign-in could not start: " + Shorten(ex.Message, 200));
        }
    }

    // --- Conversation -------------------------------------------------------------------------

    private async Task SendAsync()
    {
        var text = InputText.Trim();
        if (_client is null || _turns is null || _threadId is null || text.Length == 0) return;
        // A message belongs below the newest ones, so an older window gives way to the latest view first.
        ShowLatest();
        InputText = string.Empty;
        AddItem(new AssistantTranscriptItem("You", text));
        _lastUserRequest = text;
        await StartTurnAsync(text);
    }

    /// <summary>Starts one turn. Any earlier file request belongs to an earlier turn and can no longer be allowed.</summary>
    private async Task StartTurnAsync(string text)
    {
        var client = _client;
        var turns = _turns;
        if (client is null || turns is null || _threadId is null) return;
        ClearFileRequest();
        _turnSerial++;
        _lastAgentItem = null;
        _lastAgentText = null;
        SetState(AssistantState.Streaming, "The assistant is responding. Press Esc or Stop to interrupt.");

        // Fresh reads for every message, continuations included; they are sent with the turn and never stored or logged.
        var lookup = new CancellationTokenSource();
        _contextLookup = lookup;
        string context;
        try
        {
            var inventory = LookupAsync(AssistantCliCommands.Inventory(), InventoryTimeout, lookup.Token);
            var models = LookupAsync(AssistantCliCommands.Models(), ModelsTimeout, lookup.Token);
            context = BuildTurnContext(await inventory, await models);
        }
        finally
        {
            if (ReferenceEquals(_contextLookup, lookup)) _contextLookup = null;
        }
        var stopped = lookup.IsCancellationRequested;
        lookup.Dispose();
        if (!ReferenceEquals(_client, client) || State != AssistantState.Streaming || _threadId is null) return;
        if (stopped)
        {
            AddSystem("Stopped before the message was sent.");
            SetState(AssistantState.Ready, "Response stopped.");
            return;
        }

        try
        {
            var turn = new JsonObject
            {
                ["threadId"] = _threadId,
                ["input"] = new JsonArray(
                    new JsonObject { ["type"] = "text", ["text"] = context },
                    new JsonObject { ["type"] = "text", ["text"] = text })
            };
            if (SelectedModel is not null) turn["model"] = SelectedModel.Id;
            CodexAppServerClient.AddConfinement(turn);
            await turns.StartAsync(_threadId, turn);
        }
        catch (AppServerException ex)
        {
            if (State == AssistantState.Streaming && ReferenceEquals(_client, client))
            {
                AddSystem("The message was not sent: " + Shorten(ex.Message, 200));
                SetState(AssistantState.Ready);
            }
        }
    }

    /// <summary>
    /// Esc and Stop: interrupt only this tab's own turn. Pressed before the turn has an id, the stop
    /// is held by the runner and sent the moment the id arrives.
    /// </summary>
    public async Task InterruptAsync()
    {
        if (State != AssistantState.Streaming) return;
        // Still reading the inventory and model settings: nothing has been sent, so the turn is simply not started.
        if (_contextLookup is { } lookup)
        {
            StatusDetail = "Stopping the response…";
            lookup.Cancel();
            return;
        }
        if (_turns is not { } turns) return;
        StatusDetail = "Stopping the response…";
        await turns.StopAsync();
    }

    /// <summary>A lookup that ends early (Stop or the session closing) reads as unanswered; the caller checks which.</summary>
    private async Task<AssistantCliResult> LookupAsync(string[] arguments, TimeSpan timeout, CancellationToken cancellationToken)
    {
        try
        {
            return await RunCliAsync(arguments, timeout, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return AssistantCliResult.Failure(AssistantCliResult.Timeout, string.Empty);
        }
    }

    /// <summary>
    /// The labeled context sent ahead of each message: the distro-wide scope, then the inventory and the CHIM model
    /// snapshot, each as Core's allowlisted projection or a plain statement that it is unavailable and why.
    /// </summary>
    internal static string BuildTurnContext(AssistantCliResult inventory, AssistantCliResult models) =>
        ContextLabel + "\n" + ScopeReminder + "\n\n" + BuildInventoryContext(inventory) + "\n\n" + BuildModelsContext(models);

    private static string DescribeLookupFailure(AssistantCliResult result, string problem) =>
        result.Ok ? problem : result.ErrorCode switch
        {
            AssistantCliResult.Timeout => "DwemerDistro did not answer the lookup in time.",
            AssistantCliResult.CliMissing or AssistantCliResult.DistroMissing => "the DwemerDistro command is not available.",
            AssistantCliResult.Protocol or "invalid_argument" or "unsupported" or "usage" or "not_found" =>
                "this DwemerDistro Core does not provide the lookup (Update Distro adds it).",
            _ => $"DwemerDistro reported {Shorten(result.ErrorCode, 40)}."
        };

    /// <summary>
    /// Core's presence-only inventory of the whole distro. Only the allowlisted projection is serialized, never the raw
    /// document, and the text says plainly what presence does not show.
    /// </summary>
    internal static string BuildInventoryContext(AssistantCliResult result)
    {
        var snapshot = AssistantCliService.ProjectInventory(result, out var problem);
        if (snapshot is null)
            return "DwemerDistro inventory is unavailable for this message: " + DescribeLookupFailure(result, problem) +
                   " If asked what is installed, say it could not be read just now and point to the launcher's server and component pages. " +
                   "Do not guess, and do not read configuration, database or credential files to find it.";

        var complete = snapshot["complete"]!.GetValue<bool>();
        return $"DwemerDistro inventory, read by DwemerDistro at {snapshot["captured_at"]!.GetValue<string>()} (UTC), just before this message. " +
               "Scope presence_only. servers: each managed game server's install state, repository state, branch, version, and whether its " +
               "database exists (null means unknown). extensions: the names of entries directly inside each known ext/ folder (CHIM and " +
               "Dialectic); no_known_root lists servers without a known extension folder, so nothing was listed for them. components: whether " +
               "each component's startup marker files are present (present means startup would try it, not that it is running). custom_mods: " +
               "registered custom mod servers and their recorded state. None of this shows whether an extension is enabled, loaded, " +
               "configured or working, whether a component is running or healthy, or anything about code quality; say so when it matters. " +
               "Answer what-is-installed questions directly from it, with the capture time, instead of asking the user for a list. " +
               (complete ? "Every section was read in full."
                   : "Some sections are INCOMPLETE (see available, reason, state, truncated and skipped); say which when it matters.") +
               "\n" + snapshot.ToJsonString();
    }

    /// <summary>Core's projected CHIM model snapshot, or a plain statement that it is unavailable and why.</summary>
    internal static string BuildModelsContext(AssistantCliResult result)
    {
        var snapshot = AssistantCliService.ProjectModelsSnapshot(result, out var problem);
        if (snapshot is null)
            return Unavailable(DescribeLookupFailure(result, problem));

        if (!snapshot["available"]!.GetValue<bool>())
        {
            var reason = snapshot["reason"]!.GetValue<string>();
            return Unavailable("CHIM's model settings could not be read" + (reason.Length > 0 ? $" ({Shorten(reason, 120)})." : "."));
        }

        var count = snapshot["assignments"]!.AsArray().Count;
        var truncated = snapshot["truncated"]!.GetValue<bool>();
        return $"CHIM selected models, read by DwemerDistro from CHIM's configured profiles at {snapshot["captured_at"]!.GetValue<string>()} (UTC), " +
               "just before this message. Scope configured_profiles: each assignment is the connector and model configured for one CHIM " +
               "profile and role. It does not show which NPC or profile is active in game right now. " +
               (truncated ? $"The list is TRUNCATED: CHIM has more assignments than the {count} shown; say so if it matters. "
                   : count == 0 ? "No assignments are configured. " : $"The list is complete ({count} assignment(s)). ") +
               "Use it directly for questions about selected models; do not read configuration, database or credential files for this.\n" +
               snapshot.ToJsonString();

        static string Unavailable(string why) =>
            "CHIM selected models are unavailable for this message: " + why +
            " If asked, say they could not be read. Do not guess them, and do not read configuration, database or credential files to find them.";
    }

    /// <summary>The user closes the session; it stays closed, even after distro maintenance, until Connect. A draft is kept.</summary>
    private async Task DisconnectAsync()
    {
        if (State is AssistantState.Streaming or AssistantState.Applying) return;
        _connectionIntent = false;
        _reconnectAfterMaintenance = false;
        await EndSessionAsync(AssistantState.Disconnected, "Session closed. Connect to continue this task.");
    }

    // --- Distro maintenance -------------------------------------------------------------------

    /// <summary>
    /// Called by the launcher before Compact, Export, Import or DNS repair claim the distro. An idle session (ready or
    /// signed out) is closed and its lease released before this returns; anything in flight refuses, so a response,
    /// sign-in, review, live change or check is never cut off. Until <see cref="ResumeAfterMaintenance"/>, nothing connects.
    /// </summary>
    /// <remarks>
    /// Each true answer is one hold, ended by exactly one <see cref="ResumeAfterMaintenance"/>, which the launcher calls
    /// whether or not its exclusive claim then succeeds. A command still running after <see cref="MaintenanceDrainTimeout"/>
    /// ends the hold here and refuses, so a stuck command never leaves the assistant held.
    /// </remarks>
    internal async Task<bool> SuspendForMaintenanceAsync()
    {
        if (_isShutDown) return false;
        if (IsMaintenanceHeld)
        {
            // Another operation already closed the session; this one holds the distro too until it ends.
            _maintenanceHolds++;
            return true;
        }
        if (!_ownsDistroActivity)
        {
            _maintenanceHolds = 1;
            _reconnectAfterMaintenance = false;
            _maintenanceEndedUnsafely = false;
            RaiseCommands();
            return true;
        }
        if (!CanYieldForMaintenance) return false;
        _maintenanceHolds = 1;
        _reconnectAfterMaintenance = _connectionIntent;
        _maintenanceEndedUnsafely = false;
        await EndSessionAsync(AssistantState.Disconnected, "Paused for distro maintenance. " + (_reconnectAfterMaintenance
            ? "The assistant reconnects when it finishes safely." : "Connect when it finishes."));
        // A change check the last response started may still be reading; the launcher's claim counts it until it ends.
        var deadline = DateTime.UtcNow + MaintenanceDrainTimeout;
        while (Volatile.Read(ref _commandsInFlight) > 0 && DateTime.UtcNow < deadline) await Task.Delay(50);
        if (Volatile.Read(ref _commandsInFlight) == 0) return true;
        ResumeAfterMaintenance(reconnect: true);
        return false;
    }

    /// <summary>How long maintenance waits for running Core commands after closing the session.</summary>
    internal TimeSpan MaintenanceDrainTimeout { get; set; } = QueryTimeout;

    /// <summary>
    /// Ends the maintenance hold. Reconnects only a session maintenance itself closed, that the user still wants, after
    /// an operation that ended safely; otherwise Connect stays available and says why.
    /// </summary>
    internal void ResumeAfterMaintenance(bool reconnect)
    {
        if (!IsMaintenanceHeld) return;
        if (!reconnect) _maintenanceEndedUnsafely = true;
        if (--_maintenanceHolds > 0) return;
        var paused = _reconnectAfterMaintenance;
        reconnect = !_maintenanceEndedUnsafely;
        _reconnectAfterMaintenance = false;
        _maintenanceEndedUnsafely = false;
        RaiseCommands();
        if (!paused || _isShutDown || _client is not null || State != AssistantState.Disconnected) return;
        if (!reconnect || !_connectionIntent)
        {
            SetState(AssistantState.Disconnected, "Distro maintenance ended. Press Connect to continue this task.");
            return;
        }
        _ = ReconnectAfterMaintenanceAsync();
    }

    private async Task ReconnectAfterMaintenanceAsync()
    {
        try
        {
            await ConnectAsync();
        }
        catch (Exception ex)
        {
            LauncherLogService.Operation($"Assistant reconnection after maintenance failed: {ex.GetType().Name}");
            if (!_isShutDown) await EndSessionAsync(AssistantState.Error, "The assistant could not reconnect: " + Shorten(ex.Message, 200));
        }
    }

    private async Task NewTaskAsync()
    {
        await EndSessionAsync(AssistantState.NotChecked, "Starting a new task…");
        RememberCurrentTask();
        _store.Clear();
        _taskId = null;
        _threadId = null;
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
        await ConnectAsync();
    }

    internal void OnNotification(string method, JsonElement parameters)
    {
        switch (method)
        {
            case "item/started" when ItemOf(parameters, out var type, out var id) && type == "agentMessage":
                if (FindItem(id) is null) AddItem(new AssistantTranscriptItem("Codex", string.Empty, id));
                break;
            case "item/agentMessage/delta":
                var itemId = AssistantCliService.GetString(parameters, "itemId");
                var d = Prop(parameters, "delta");
                var delta = d.ValueKind == JsonValueKind.String ? d.GetString() ?? string.Empty : AssistantCliService.GetString(d, "text");
                var target = FindItem(itemId) ?? AddItem(new AssistantTranscriptItem("Codex", string.Empty, itemId));
                target.Append(delta);
                break;
            case "item/completed" when ItemOf(parameters, out var type, out var id):
                var item = Prop(parameters, "item");
                if (type == "agentMessage")
                {
                    var final = AssistantCliService.GetString(item, "text");
                    var existing = FindItem(id);
                    if (existing is null) existing = AddItem(new AssistantTranscriptItem("Codex", final, id));
                    else if (final.Length > 0) existing.Replace(final);
                    // Only the turn's last message may carry a file request, and only once the turn completes.
                    _lastAgentItem = existing;
                    _lastAgentText = final.Length > 0 ? final : existing.Text;
                }
                else if (type == "commandExecution")
                {
                    AddSystem("Ran in the workspace: " + Shorten(AssistantCliService.GetString(item, "command"), 160));
                }
                else if (type == "fileChange")
                {
                    AddSystem("Edited files in the isolated workspace. Nothing is live yet.");
                }
                break;
            case "turn/completed":
                var turn = Prop(parameters, "turn");
                var status = AssistantCliService.GetString(turn, "status");
                if (status == "interrupted") AddSystem("Stopped.");
                else if (status == "failed")
                    AddSystem("The response failed: " + Shorten(AssistantCliService.GetString(Prop(turn, "error"), "message"), 200));
                if (State != AssistantState.Streaming) break;
                SetState(AssistantState.Ready, "Response complete.");
                if (status == "completed")
                {
                    OfferFileRequest();
                    _ = RefreshChangesAsync();
                }
                else if (_lastAgentText?.Contains(FileRequestTag, StringComparison.OrdinalIgnoreCase) == true)
                {
                    AddSystem("The response did not finish, so its file request was ignored. Nothing was shared.");
                }
                break;
            case "error":
                if (!AssistantCliService.GetBool(parameters, "willRetry") && Prop(parameters, "error") is { ValueKind: JsonValueKind.Object } error)
                    AddSystem("Assistant error: " + Shorten(AssistantCliService.GetString(error, "message"), 200));
                break;
            case "account/login/completed":
                if (_loginId is not null && AssistantCliService.GetString(parameters, "loginId") is var completed && completed.Length > 0 && completed != _loginId) break;
                _loginId = null;
                if (AssistantCliService.GetBool(parameters, "success") && _client is { } client)
                    _ = RefreshAccountSafelyAsync(client);
                else
                    SetState(AssistantState.SignedOut, "Sign-in did not complete: " + Shorten(AssistantCliService.GetString(parameters, "error"), 200));
                break;
        }
    }

    private async Task RefreshAccountSafelyAsync(CodexAppServerClient client)
    {
        SetState(AssistantState.Busy, "Signed in. Loading models…");
        try
        {
            await RefreshAccountAsync(client);
        }
        catch (AppServerException ex)
        {
            if (ReferenceEquals(_client, client)) await EndSessionAsync(AssistantState.Error, ex.Message);
        }
    }

    private void OnDisconnected(string reason, string tail)
    {
        if (_client is null) return;
        _ = DisposeClientAsync();
        var detail = reason + (tail.Trim().Length > 0 ? " Last output: " + Shorten(tail.Trim(), 240) : string.Empty);
        if (State is AssistantState.Applying or AssistantState.Review)
        {
            // The live change runs through the CLI, not the session, so its own result stands.
            AddSystem("Assistant session disconnected. " + reason);
            return;
        }
        SetState(AssistantState.Disconnected, detail);
    }

    private async Task EndSessionAsync(AssistantState state, string detail)
    {
        await DisposeClientAsync();
        SignInUrl = string.Empty;
        SignInCode = string.Empty;
        SetState(state, detail);
    }

    private async Task DisposeClientAsync()
    {
        var client = _client;
        _client = null;
        _turns = null;
        _sessionGeneration++;
        _contextLookup?.Cancel();
        _historyLoadPending = false;
        ClearFileRequest();
        if (client is not null)
        {
            try { await client.DisposeAsync(); }
            catch (Exception ex) { LauncherLogService.Operation($"Assistant session cleanup failed: {ex.GetType().Name}"); }
        }
        ReleaseDistroActivity();
        RaiseCommands();
    }

    /// <summary>Ends the session and stops a Connect still in progress, so nothing starts after the launcher closes.</summary>
    public Task ShutdownAsync()
    {
        _isShutDown = true;
        _lifetime.Cancel();
        return DisposeClientAsync();
    }

    // --- File access requests -----------------------------------------------------------------

    /// <summary>
    /// Reads the one fenced file-request block at the end of a completed message. Valid means a closed block of 1 to 20
    /// distinct safe relative paths and nothing ambiguous; anything else that mentions the block is Invalid, never partly used.
    /// </summary>
    internal static FileRequestParse ParseFileRequest(string message, out IReadOnlyList<string> paths, out string visibleText, out string problem)
    {
        paths = [];
        visibleText = message;
        problem = string.Empty;
        if (!message.Contains(FileRequestTag, StringComparison.OrdinalIgnoreCase)) return FileRequestParse.None;

        var lines = message.Replace("\r\n", "\n").Split('\n');
        var open = Array.FindIndex(lines, line => line.Trim() == "```" + FileRequestTag);
        var close = open < 0 ? -1 : Array.FindIndex(lines, open + 1, line => line.Trim() == "```");
        var mentions = lines.Count(line => line.Contains(FileRequestTag, StringComparison.OrdinalIgnoreCase));
        var requested = close < 0 ? [] : lines[(open + 1)..close].Select(line => line.Trim()).Where(line => line.Length > 0).ToArray();
        var duplicate = requested.GroupBy(path => path, StringComparer.Ordinal).FirstOrDefault(group => group.Count() > 1)?.Key;
        var invalid = requested.FirstOrDefault(path => !AssistantCliCommands.IsSafeRelativePath(path) || path.Split('/')[0] == ".ddistro-context");
        if (message.Length > AssistantTranscriptItem.MaxTextChars) problem = "the message was too long to read in full.";
        else if (open < 0 || close < 0) problem = "the file list was incomplete.";
        else if (mentions != 1) problem = "the message held more than one file list.";
        else if (requested.Length == 0) problem = "the file list was empty.";
        else if (requested.Length > AssistantCliCommands.MaxRelativePaths) problem = $"it listed more than {AssistantCliCommands.MaxRelativePaths} files.";
        else if (invalid is not null) problem = $"\"{Shorten(invalid, 80)}\" is not a plain path inside the CHIM folder.";
        else if (duplicate is not null) problem = $"it listed \"{Shorten(duplicate, 80)}\" more than once.";
        if (problem.Length > 0) return FileRequestParse.Invalid;

        paths = requested;
        visibleText = string.Join("\n", lines[..open].Concat(lines[(close + 1)..])).Trim();
        if (visibleText.Length == 0) visibleText = "I need access to some CHIM files to continue.";
        return FileRequestParse.Valid;
    }

    /// <summary>After a completed turn: turns the last message's file request into the access card, or says why it cannot.</summary>
    private void OfferFileRequest()
    {
        if (_lastAgentText is not { } text || _lastAgentItem is not { } item || _taskId is null || _threadId is null) return;
        switch (ParseFileRequest(text, out var paths, out var visible, out var problem))
        {
            case FileRequestParse.Valid:
                item.Replace(visible);
                _fileRequest = new AssistantFileRequest(_taskId, _threadId, _turnSerial, paths, _lastUserRequest,
                    Shorten(visible, MaxContinuationContextChars));
                RaiseFileRequest();
                StatusDetail = "The assistant asks for access to CHIM files. Nothing is shared unless you allow it.";
                break;
            case FileRequestParse.Invalid:
                AddSystem("The assistant asked for files, but the request could not be used: " + problem +
                          " Nothing was shared. Ask it to list the files again, or choose files under Advanced.");
                break;
        }
    }

    private bool IsCurrent(AssistantFileRequest request) =>
        ReferenceEquals(request, _fileRequest) && request.TaskId == _taskId && request.ThreadId == _threadId && request.Turn == _turnSerial;

    private void ClearFileRequest()
    {
        if (_fileRequest is null) return;
        _fileRequest = null;
        RaiseFileRequest();
    }

    private void DeclineFileAccess()
    {
        ClearFileRequest();
        AddSystem("File access not allowed. Nothing was shared.");
    }

    /// <summary>
    /// The user's Allow click: refuses while the current copy has unreviewed edits or an unknown live change, prepares a
    /// fresh task with exactly the requested paths through Core (which refuses secrets and unsafe paths), then reconnects
    /// and continues the original request there. A stale request, or any failure, leaves the current task as it was.
    /// </summary>
    private async Task AllowFileAccessAsync()
    {
        var request = _fileRequest;
        if (request is null || !IsCurrent(request))
        {
            ClearFileRequest();
            StatusDetail = "That file request is out of date. Nothing was shared.";
            return;
        }
        if (_canCheckOutcome)
        {
            StatusDetail = "Check the last live change's status before starting a new working copy. Nothing was shared.";
            return;
        }

        SetState(AssistantState.Busy, "Checking the current working copy for unreviewed edits…");
        var diff = await RunCliAsync(AssistantCliCommands.Diff(request.TaskId), QueryTimeout, CancellationToken.None);
        var pending = ReadReviewableChanges(diff, request.TaskId);
        if (!IsCurrent(request) || pending != 0)
        {
            SetState(SessionState(), !IsCurrent(request) ? "That file request is out of date. Nothing was shared."
                : pending < 0 ? "Could not confirm the current working copy has no unreviewed edits, so nothing was switched or shared."
                : $"The current working copy has {pending} edited file(s) you have not reviewed. Review and apply them first, " +
                  "or press New task to discard them. Nothing was shared.");
            if (pending > 0) HasChanges = true;
            return;
        }

        SetState(AssistantState.Checking, $"Preparing a working copy with the {request.Paths.Count} file(s) you allowed…");
        var prepared = await RunCliAsync(AssistantCliCommands.Prepare("herika", request.Paths), PrepareTimeout, CancellationToken.None);
        var taskId = AssistantCliService.GetString(prepared.Data, "task_id");
        if (!IsCurrent(request) || !prepared.Ok || !AssistantCliCommands.IsValidTaskId(taskId))
        {
            ClearFileRequest();
            SetState(SessionState(), "Those files could not be prepared: " + Shorten(prepared.Ok
                ? "DwemerDistro returned an invalid task id." : prepared.ErrorMessage.Length > 0 ? prepared.ErrorMessage : prepared.ErrorCode, 240) +
                " Nothing was shared; the current task continues.");
            return;
        }

        var hadRollback = _canRollback;
        var earlierTask = request.TaskId;
        var carried = CarryForward(LatestItems);
        await DisposeClientAsync();
        RememberCurrentTask();
        UseTask(taskId, request.Paths.Count);
        TaskText += $" · continued from task {ShortTaskId(earlierTask)}";
        OutcomeText = string.Empty;
        AddSystem($"Access allowed. This conversation continues in a new isolated working copy (task {ShortTaskId(taskId)}) holding only " +
                  $"these {request.Paths.Count} file(s):\n" + string.Join("\n", request.Paths) +
                  $"\nThe assistant gets a summary of your request and the recent messages above, not the earlier session itself. " +
                  $"The earlier task {ShortTaskId(earlierTask)} is kept" +
                  (hadRollback ? ", with the receipt for its applied change; roll back is no longer offered on this page." : ".") +
                  "\nContinuing your request…");
        _pendingContinuation = BuildContinuation(request, carried, taskId);
        await ConnectAsync();
    }

    /// <summary>Old launchers began the first message of an allowed-files workspace with this; restored threads still show it as the launcher's.</summary>
    private const string ContinuationPrefix = "The user allowed access to these CHIM files.";

    /// <summary>First line of the message that hands a conversation to a new working copy.</summary>
    internal const string HandoffLabel = "[DwemerDistro workspace handoff - added by the launcher, not typed by the user]";

    /// <summary>
    /// The first message in the new workspace, clearly the launcher's: which task holds the allowed files now, a bounded
    /// carry-forward of the recent conversation, the user's own request and the assistant's reason. The earlier session
    /// is not shared with the new one; this summary is all it gets.
    /// </summary>
    internal static string BuildContinuation(AssistantFileRequest request, IReadOnlyList<string> carried, string newTaskId) =>
        HandoffLabel + "\n"
        + $"Workspace handoff: this conversation continues in a new isolated working copy, task {newTaskId}, replacing task {request.TaskId}. "
        + "Edits made in the earlier working copy are not here. The user allowed access to these CHIM files; they are now in /work at the "
        + "same relative paths (a file that does not exist yet is absent):\n"
        + string.Join("\n", request.Paths)
        + (carried.Count > 0
            ? "\n\nCarried forward from the earlier conversation, oldest first (messages may be shortened):\n" + string.Join("\n\n", carried)
            : string.Empty)
        + "\n\nThe user's request was:\n" + Shorten(request.OriginalRequest, MaxInputChars)
        + "\n\nYour earlier reply, before the files were available:\n" + Shorten(request.Context, MaxContinuationContextChars)
        + "\n\nContinue the request using these files. Edit only what is needed; the user reviews every change before it goes live.";

    /// <summary>The newest user and assistant messages shown, oldest first and bounded; launcher notes stay behind.</summary>
    internal static IReadOnlyList<string> CarryForward(IEnumerable<AssistantTranscriptItem> transcript) =>
        transcript.Where(item => item.Role is "You" or "Codex" && item.Text.Trim().Length > 0)
            .TakeLast(MaxCarriedMessages)
            .Select(item => (item.Role == "You" ? "User: " : "Assistant: ") + Shorten(item.Text.Trim(), MaxCarriedMessageChars))
            .ToList();

    private static string ShortTaskId(string taskId) => taskId.Length > 8 ? taskId[..8] : taskId;

    /// <summary>
    /// Asks Core's <c>changes diff</c> whether this task has edits that can still be reviewed. Review changes appears only
    /// for real edits; an unreadable answer hides it and says so, and the next completed response or reconnect checks again.
    /// </summary>
    private async Task RefreshChangesAsync()
    {
        if (_taskId is not { } taskId) return;
        var check = ++_changeCheck;
        var diff = await RunCliAsync(AssistantCliCommands.Diff(taskId), QueryTimeout, CancellationToken.None);
        if (check != _changeCheck || taskId != _taskId) return;
        var pending = ReadReviewableChanges(diff, taskId);
        HasChanges = pending > 0;
        if (pending < 0)
            StatusDetail = "Could not check this task's working copy for edits. It is checked again after the next response or when you reconnect.";
    }

    /// <summary>Edited files Core can still stage for this task: 0 when none or already applied, -1 when the answer is unusable.</summary>
    internal static int ReadReviewableChanges(AssistantCliResult diff, string taskId)
    {
        if (!diff.Ok || AssistantCliService.GetString(diff.Data, "task_id") != taskId || Prop(diff.Data, "files") is not { ValueKind: JsonValueKind.Array } files)
            return -1;
        var state = AssistantCliService.GetString(diff.Data, "state");
        // An apply or rollback that did not finish leaves the outcome unknown, so nothing is assumed about the edits.
        if (state is "" or "interrupted" or "in_progress" or "rollback_interrupted") return -1;
        if (state is not ("prepared" or "staged")) return 0;
        return files.GetArrayLength();
    }

    private void RaiseFileRequest()
    {
        OnPropertyChanged(nameof(HasFileRequest));
        OnPropertyChanged(nameof(FileRequestPaths));
        AllowFileAccessCommand.RaiseCanExecuteChanged();
        DeclineFileAccessCommand.RaiseCanExecuteChanged();
    }

    // --- Review and live changes --------------------------------------------------------------

    private async Task ReviewAsync()
    {
        // A new review always replaces the old one: whatever happens below, the earlier batch can no longer be approved.
        _review = null;
        RaiseReview();
        SetState(AssistantState.Busy, "Freezing the current changes for review…");
        var staged = await RunCliAsync(AssistantCliCommands.Stage(_taskId!), PrepareTimeout, CancellationToken.None);
        if (!staged.Ok)
        {
            SetState(SessionState(), "Could not prepare the review: " + DescribeStageFailure(staged) + " Nothing can be approved.");
            return;
        }
        var batch = ReadStage(staged, _taskId!, out var problem);
        if (batch is null)
        {
            SetState(SessionState(), "Could not prepare the review: " + problem + " Nothing can be approved.");
            return;
        }
        if (batch.Files.Count == 0)
        {
            HasChanges = false;
            SetState(SessionState(), "There are no changes to review yet.");
            return;
        }
        _review = batch;
        RaiseReview();
        SetState(AssistantState.Review, batch.WarningTotal > 0
            ? "Review the exact batch and its validation warnings below. Approve applies only this batch, once."
            : "Review the exact batch below. Approve applies only this batch, once.");
    }

    /// <summary>
    /// Accepts only a complete stage of this task: state staged, a manifest hash, diff_truncated exactly false, and
    /// a diff the launcher can show in full. Anything else returns null and the reason; nothing becomes approvable.
    /// </summary>
    internal static AssistantReviewBatch? ReadStage(AssistantCliResult staged, string taskId, out string problem)
    {
        var data = staged.Data;
        var hash = AssistantCliService.GetString(data, "manifest_sha256");
        problem = string.Empty;
        if (!staged.Ok || data.ValueKind != JsonValueKind.Object)
            problem = "DwemerDistro did not return a staged batch.";
        else if (AssistantCliService.GetString(data, "task_id") != taskId)
            problem = "DwemerDistro returned a review for a different task.";
        else if (AssistantCliService.GetString(data, "state") != "staged")
            problem = "DwemerDistro did not confirm the batch was staged.";
        else if (!AssistantCliCommands.IsValidManifestHash(hash))
            problem = "DwemerDistro returned no valid manifest hash for the batch.";
        else if (Prop(data, "diff_truncated").ValueKind != JsonValueKind.False)
            problem = "The diff is incomplete, so the batch cannot be reviewed in full. Split the change into smaller tasks.";
        else if (Prop(data, "diff").ValueKind != JsonValueKind.String || Prop(data, "files").ValueKind != JsonValueKind.Array)
            problem = "DwemerDistro returned a batch without its diff or file list.";
        else if (Prop(data, "diff").GetString()!.Length > MaxDiffChars)
            problem = "The diff is too large to show in full here. Split the change into smaller tasks.";
        if (problem.Length > 0) return null;
        return new AssistantReviewBatch(taskId, hash, Prop(data, "diff").GetString()!, AssistantCliService.GetFiles(data, "files"),
            staged.Warnings, Math.Max(staged.WarningTotal, staged.Warnings.Count));
    }

    private static string DescribeStageFailure(AssistantCliResult staged)
    {
        var message = Shorten(staged.ErrorMessage.Length > 0 ? staged.ErrorMessage : staged.ErrorCode, 200);
        // Validation failures name each file; show the first few so the assistant can be asked to fix them.
        if (Prop(staged.ErrorDetails, "errors") is not { ValueKind: JsonValueKind.Array } errors) return message;
        var lines = errors.EnumerateArray().Take(5)
            .Select(error => $"{AssistantCliService.GetString(error, "path")}: {Shorten(AssistantCliService.GetString(error, "message"), 160)}");
        return message + "\n" + string.Join("\n", lines);
    }

    private void Reject()
    {
        _review = null;
        RaiseReview();
        SetState(SessionState(), "Review cleared. Nothing was applied.");
    }

    private async Task ApproveAsync()
    {
        var batch = _review;
        if (batch is null || batch.TaskId != _taskId) return;
        if (!_host.TryBeginLiveChange())
        {
            StatusDetail = "Another server, component or system operation is running. Nothing was applied; try again when it finishes.";
            return;
        }

        // The batch is consumed the moment its apply is dispatched, so it can never be sent twice.
        _review = null;
        SetState(AssistantState.Applying, "Applying the reviewed batch to the live CHIM files…");
        AssistantCliResult result;
        try
        {
            result = await RunCliAsync(AssistantCliCommands.Apply(batch.TaskId, batch.ManifestSha256), LiveChangeTimeout, CancellationToken.None);
        }
        finally
        {
            _host.EndLiveChange();
        }

        RaiseReview();
        OutcomeText = DescribeApply(result, batch.Files.Count, out _canRollback, out _canCheckOutcome);
        SetState(SessionState(), OutcomeText);
        await RefreshChangesAsync();
    }

    /// <summary>
    /// Classifies an apply by Core's own state and per-file outcomes, never by ok alone: a replayed apply answers
    /// ok with the earlier receipt, which may be partial or failed, and a failed apply carries its result in
    /// error.details.
    /// </summary>
    internal static string DescribeApply(AssistantCliResult result, int fileCount, out bool canRollback, out bool canCheck)
    {
        canRollback = false;
        canCheck = false;
        var outcome = result.Ok ? result.Data : result.ErrorDetails;
        var state = AssistantCliService.GetString(outcome, "state");
        var counts = AssistantCliService.CountFileStates(outcome);
        var replayed = AssistantCliService.GetBool(outcome, "replayed") ? " This is the result recorded earlier; nothing was written again." : string.Empty;
        var fileLines = FileLines(outcome);
        switch (state)
        {
            case "applied" when result.Ok && counts.Keys.All(key => key == "written"):
                var receipt = DescribeReceipt(outcome);
                canRollback = receipt.Length > 0;
                return $"Applied {counts.GetValueOrDefault("written", fileCount)} file(s)." +
                       (receipt.Length > 0 ? $" Receipt {receipt}." : " No receipt was returned, so rollback is unavailable.") + replayed;
            case "no_changes" when result.Ok:
                return "Nothing was applied: the reviewed batch had no changes.";
            case "partial":
                canRollback = true;
                canCheck = true;
                return $"Partially applied: {DescribeCounts(counts)}. Some live files changed. Check status, then roll back if needed.{replayed}{fileLines}";
            case "failed":
                return $"Not applied: no live file was written ({DescribeCounts(counts)}).{replayed} Prepare a new task to try again.{fileLines}";
            case "interrupted" or "in_progress":
                canCheck = true;
                return "Outcome unknown: an earlier apply of this task did not finish, and nothing was repeated. " +
                       $"Check status and inspect the listed files.{fileLines}";
            case not "" when result.Ok:
                canCheck = true;
                return $"Outcome unknown: DwemerDistro reported state \"{Shorten(state, 40)}\" ({DescribeCounts(counts)}). " +
                       $"The live files may be partly changed; check status before anything else.{fileLines}";
        }

        if (result.Ok)
        {
            canCheck = true;
            return "Outcome unknown: DwemerDistro did not report the apply state. Check status before anything else.";
        }
        switch (result.ErrorCode)
        {
            case "conflict" when AssistantCliService.GetString(result.ErrorDetails, "applied_manifest_sha256").Length > 0:
                canCheck = true;
                return "Not applied: this task already applied a different batch. Check status before anything else.";
            case "conflict":
            case "stale":
                return "Not applied: the live files changed after this review (stale). Review the changes again.";
            case "busy":
                return "Not applied: DwemerDistro is busy with another operation. Review the changes again when it finishes.";
            case "partial":
                canRollback = true;
                canCheck = true;
                return "Partially applied: some live files changed. Check status, then roll back if needed.";
            case AssistantCliResult.Timeout:
            case AssistantCliResult.Protocol:
                canCheck = true;
                return "Outcome unknown: DwemerDistro did not confirm the result. Check status before doing anything else.";
            default:
                canCheck = true;
                return $"Apply failed ({Shorten(result.ErrorCode, 40)}): {Shorten(result.ErrorMessage, 200)} Check status to confirm the live files.";
        }
    }

    /// <summary>Rollback, classified the same way: a replay answers ok with the earlier rollback receipt.</summary>
    internal static string DescribeRollback(AssistantCliResult result, out bool canRollback, out bool canCheck)
    {
        canRollback = false;
        canCheck = false;
        var outcome = result.Ok ? result.Data : result.ErrorDetails;
        var state = AssistantCliService.GetString(outcome, "state");
        var counts = AssistantCliService.CountFileStates(outcome);
        var fileLines = FileLines(outcome);
        switch (state)
        {
            case "rolled_back" when result.Ok && counts.Keys.All(key => key == "written"):
                return "Rolled back. The live files match the state before this task's apply.";
            case "not_applied" when result.Ok:
                return "Nothing to roll back: this task's batch was never applied.";
            case "nothing_written" when result.Ok:
                return "Nothing to roll back: the apply wrote no file.";
            case "rollback_partial":
                canCheck = true;
                return $"Rollback incomplete: {DescribeCounts(counts)}. Some live files are restored and some are not. Check status.{fileLines}";
            case "rollback_failed":
                canCheck = true;
                return $"Rollback failed: no live file was restored ({DescribeCounts(counts)}). Check status.{fileLines}";
            case "rollback_interrupted" or "interrupted" or "in_progress":
                canCheck = true;
                return $"Rollback outcome unknown: an earlier apply or rollback did not finish, and nothing was repeated. Check status.{fileLines}";
            case not "" when result.Ok:
                canCheck = true;
                return $"Rollback outcome unknown: DwemerDistro reported state \"{Shorten(state, 40)}\". Check status before anything else.";
        }
        if (result.Ok || result.ErrorCode is AssistantCliResult.Timeout or AssistantCliResult.Protocol)
        {
            canCheck = true;
            return "Rollback outcome unknown. Check status before doing anything else.";
        }
        if (Prop(result.ErrorDetails, "stale").ValueKind == JsonValueKind.Array)
        {
            canRollback = true;
            return "Rollback refused: live files changed after the apply, and nothing was written. " + Shorten(result.ErrorMessage, 200);
        }
        canCheck = true;
        return $"Rollback failed ({Shorten(result.ErrorCode, 40)}): {Shorten(result.ErrorMessage, 200)}";
    }

    private static string DescribeCounts(Dictionary<string, int> counts) =>
        counts.Count == 0 ? "no per-file results"
            : string.Join(", ", counts.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => $"{pair.Value} {pair.Key}"));

    private static string FileLines(JsonElement outcome)
    {
        var files = AssistantCliService.GetFiles(outcome, "files", 11);
        if (files.Length == 0) return string.Empty;
        return "\n" + string.Join("\n", files.Take(10)) + (files.Length > 10 ? "\n…" : string.Empty);
    }

    private static string DescribeReceipt(JsonElement data)
    {
        if (data.ValueKind != JsonValueKind.Object || !data.TryGetProperty("receipt", out var receipt)) return string.Empty;
        return receipt.ValueKind switch
        {
            JsonValueKind.String => Shorten(receipt.GetString() ?? string.Empty, 80),
            JsonValueKind.Object => Shorten(AssistantCliService.GetString(receipt, "id") is { Length: > 0 } id ? id : "recorded", 80),
            _ => string.Empty
        };
    }

    /// <summary>What a task's change state allows: rollback after a write, and a forced status check while unknown.</summary>
    private void SetLiveChangeFlags(string state)
    {
        _canRollback = state is "applied" or "partial";
        _canCheckOutcome = state is "interrupted" or "in_progress" or "rollback_interrupted";
    }

    private async Task RollbackAsync()
    {
        if (!_host.TryBeginLiveChange())
        {
            StatusDetail = "Another server, component or system operation is running. Nothing was rolled back.";
            return;
        }
        var previous = SessionState();
        SetState(AssistantState.Applying, "Rolling back the applied batch…");
        AssistantCliResult result;
        try
        {
            result = await RunCliAsync(AssistantCliCommands.Rollback(_taskId!), LiveChangeTimeout, CancellationToken.None);
        }
        finally
        {
            _host.EndLiveChange();
        }
        OutcomeText = DescribeRollback(result, out _canRollback, out _canCheckOutcome);
        SetState(previous, OutcomeText);
    }

    private async Task CheckOutcomeAsync()
    {
        var previous = SessionState();
        SetState(AssistantState.Busy, "Reading the task's change status…");
        var status = await RunCliAsync(AssistantCliCommands.ChangeStatus(_taskId!), QueryTimeout, CancellationToken.None);
        if (status.Ok && AssistantCliService.GetString(status.Data, "task_id") == _taskId)
        {
            var state = AssistantCliService.GetString(status.Data, "state");
            SetLiveChangeFlags(state);
            var counts = AssistantCliService.CountFileStates(status.Data);
            OutcomeText = $"Status: {Shorten(state, 40)}" + (counts.Count > 0 ? $" ({DescribeCounts(counts)})" : string.Empty) +
                          (_canCheckOutcome ? ". The outcome is still unknown; inspect the listed files." : string.Empty) + FileLines(status.Data);
        }
        else
        {
            OutcomeText = "Status unavailable: " + Shorten(status.Ok ? "DwemerDistro answered for a different task." : status.ErrorMessage, 200);
        }
        SetState(previous, OutcomeText);
    }

    private async Task RunProbeAsync()
    {
        if (!_host.TryBeginDistroActivity())
        {
            StatusDetail = "Distro maintenance is running. Try again when it finishes.";
            return;
        }
        try
        {
            SetState(AssistantState.Checking, "Running the isolation check…");
            var probe = await RunCliAsync(AssistantCliCommands.AgentProbe(), PrepareTimeout, CancellationToken.None);
            if (!probe.Ok)
            {
                FailCli(probe);
                return;
            }
            if (ReadProbe(probe.Data, out var detail))
                SetState(AssistantState.NotChecked, "Isolation check passed. Connect to start.\n" + detail);
            else
                SetState(AssistantState.IsolationFailed, "Isolation check failed. The assistant stays off." + (detail.Length > 0 ? "\n" + detail : string.Empty));
        }
        finally
        {
            _host.EndDistroActivity();
        }
    }

    /// <summary>
    /// <c>ddistro agent probe</c> lists checks as [{name, passed}], failed first; the bare helper's {name: bool} map
    /// is read too. A pass needs a boolean passed, at least one check, every check passed, and no contradicting
    /// probe_passed. Anything malformed is a failure, never an empty pass. Failures add Core's redacted diagnostics.
    /// </summary>
    internal static bool ReadProbe(JsonElement data, out string detail)
    {
        var checks = new List<(string Name, bool Passed)>();
        var passed = Prop(data, "passed");
        var wellFormed = passed.ValueKind is JsonValueKind.True or JsonValueKind.False;
        switch (Prop(data, "checks"))
        {
            case { ValueKind: JsonValueKind.Array } list:
                foreach (var check in list.EnumerateArray())
                {
                    var name = AssistantCliService.GetString(check, "name");
                    var value = Prop(check, "passed");
                    wellFormed &= name.Length > 0 && value.ValueKind is JsonValueKind.True or JsonValueKind.False;
                    checks.Add((name, value.ValueKind == JsonValueKind.True));
                }
                break;
            case { ValueKind: JsonValueKind.Object } map:
                foreach (var check in map.EnumerateObject())
                {
                    wellFormed &= check.Value.ValueKind is JsonValueKind.True or JsonValueKind.False;
                    checks.Add((check.Name, check.Value.ValueKind == JsonValueKind.True));
                }
                break;
            default:
                wellFormed = false;
                break;
        }

        var lines = checks.OrderBy(check => check.Passed).Take(30)
            .Select(check => Shorten($"{(check.Passed ? "Passed" : "Failed")}: {check.Name}", 120)).ToList();
        wellFormed &= checks.Count > 0;
        if (!wellFormed) lines.Insert(0, "DwemerDistro returned an incomplete isolation check result, so it does not count as passed.");
        var allPassed = wellFormed && passed.ValueKind == JsonValueKind.True && checks.All(check => check.Passed);
        // The wrapper adds agent status after a probe; a pass it does not confirm is not a pass.
        var statusAgrees = Prop(data, "probe_passed").ValueKind is JsonValueKind.Undefined or JsonValueKind.True;
        if (allPassed && !statusAgrees) lines.Insert(0, "DwemerDistro's status does not confirm the isolation check, so it does not count as passed.");
        var ok = allPassed && statusAgrees;
        if (!ok)
        {
            if (Prop(data, "diagnostics") is { ValueKind: JsonValueKind.Object } diagnostics)
                lines.AddRange(diagnostics.EnumerateObject().Take(8).Select(DescribeDiagnostic).Where(line => line.Length > 0));
            var reason = DescribeAgentReason(AssistantCliService.GetString(data, "reason"));
            if (reason.Length > 0) lines.Add(reason);
        }
        detail = string.Join("\n", lines);
        return ok;
    }

    /// <summary>One probe step: exit status, failure token and up to three of Core's redacted stderr lines.</summary>
    private static string DescribeDiagnostic(JsonProperty step)
    {
        if (step.Value.ValueKind != JsonValueKind.Object) return string.Empty;
        var parts = new List<string>();
        if (Prop(step.Value, "exit") is { ValueKind: JsonValueKind.Number } exit) parts.Add("exit " + exit.GetRawText());
        parts.AddRange(new[] { "failure", "error" }.Select(key => AssistantCliService.GetString(step.Value, key)).Where(value => value.Length > 0));
        if (Prop(step.Value, "stderr") is { ValueKind: JsonValueKind.Array } stderr)
            parts.AddRange(stderr.EnumerateArray().Where(line => line.ValueKind == JsonValueKind.String).Take(3).Select(line => line.GetString()!));
        return parts.Count == 0 ? string.Empty : Shorten($"Diagnostics {step.Name}: {string.Join("; ", parts)}", 400);
    }

    // --- Helpers ------------------------------------------------------------------------------

    private void FailCli(AssistantCliResult result)
    {
        var state = result.ErrorCode switch
        {
            AssistantCliResult.CliMissing or AssistantCliResult.DistroMissing => AssistantState.CliAbsent,
            "invalid_argument" or "unsupported" => AssistantState.RuntimeAbsent,
            _ => AssistantState.Error
        };
        var detail = state == AssistantState.RuntimeAbsent
            ? "This DwemerDistro Core does not support the assistant yet. Run Update Distro."
            : Shorten(result.ErrorMessage.Length > 0 ? result.ErrorMessage : result.ErrorCode, 240);
        SetState(state, detail);
    }

    private AssistantState SessionState() => _client is not null ? AssistantState.Ready : AssistantState.Disconnected;

    /// <summary>
    /// Every Core command holds the launcher's passive distro lease while it runs, so distro maintenance waits for it and
    /// never starts underneath it; while maintenance holds the distro, the command is not run.
    /// </summary>
    private async Task<AssistantCliResult> RunCliAsync(IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (!_host.TryBeginDistroActivity())
            return AssistantCliResult.Failure("busy", "Distro maintenance is running. Try again when it finishes.");
        Interlocked.Increment(ref _commandsInFlight);
        try
        {
            return await _cli.RunAsync(arguments, timeout, cancellationToken);
        }
        finally
        {
            Interlocked.Decrement(ref _commandsInFlight);
            _host.EndDistroActivity();
        }
    }

    private void ReleaseDistroActivity()
    {
        if (!_ownsDistroActivity) return;
        _ownsDistroActivity = false;
        _host.EndSessionActivity();
    }

    private AssistantTranscriptItem AddItem(AssistantTranscriptItem item)
    {
        LatestItems.Add(item);
        TrimTranscript();
        return item;
    }

    private void AddSystem(string text) => AddItem(new AssistantTranscriptItem("DwemerDistro", text));

    private AssistantTranscriptItem? FindItem(string? itemId) =>
        string.IsNullOrEmpty(itemId) ? null : LatestItems.LastOrDefault(item => item.ItemId == itemId);

    private static bool ItemOf(JsonElement parameters, out string type, out string id)
    {
        var item = Prop(parameters, "item");
        type = AssistantCliService.GetString(item, "type");
        id = AssistantCliService.GetString(item, "id");
        return type.Length > 0;
    }

    internal static string DescribeDeclined(string method) => method switch
    {
        "item/commandExecution/requestApproval" or "execCommandApproval" =>
            "Declined a request to run a command outside the assistant's isolation.",
        "item/fileChange/requestApproval" or "applyPatchApproval" =>
            "Declined a request to write outside the isolated workspace. Use Review changes for live files.",
        "item/permissions/requestApproval" => "Declined a request for extra permissions.",
        _ => "Declined an unsupported assistant request."
    };

    private void Post(int generation, Action action) =>
        _dispatcher.BeginInvoke(() =>
        {
            if (generation == _sessionGeneration) action();
        });

    private void RaiseReview()
    {
        OnPropertyChanged(nameof(ReviewDiff));
        OnPropertyChanged(nameof(ReviewSummary));
        OnPropertyChanged(nameof(ReviewValidationText));
        OnPropertyChanged(nameof(HasReviewWarnings));
        OnPropertyChanged(nameof(IsReviewVisible));
        OnPropertyChanged(nameof(IsChangesVisible));
        OnPropertyChanged(nameof(IsSidePanelVisible));
    }

    private void RaiseCommands()
    {
        OnPropertyChanged(nameof(IsApplying));
        foreach (var command in new System.Windows.Input.ICommand[]
                 {
                     ConnectCommand, RunProbeCommand, SignInCommand, OpenSignInUrlCommand, StopCommand, DisconnectCommand, LoadEarlierCommand, LoadNewerCommand, BackToLatestCommand,
                     SendCommand, NewTaskCommand, ReviewCommand, ApproveCommand, RejectCommand, RollbackCommand, CheckOutcomeCommand,
                     AllowFileAccessCommand, DeclineFileAccessCommand
                 })
        {
            switch (command)
            {
                case AsyncRelayCommand asyncCommand: asyncCommand.RaiseCanExecuteChanged(); break;
                case RelayCommand relayCommand: relayCommand.RaiseCanExecuteChanged(); break;
            }
        }
    }

    /// <summary>A child value, or an undefined element when the parent is not an object or lacks it.</summary>
    private static JsonElement Prop(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) ? value : default;

    private static string Shorten(string value, int max) =>
        string.IsNullOrEmpty(value) ? string.Empty : value.Length <= max ? value : value[..max] + "…";
}
