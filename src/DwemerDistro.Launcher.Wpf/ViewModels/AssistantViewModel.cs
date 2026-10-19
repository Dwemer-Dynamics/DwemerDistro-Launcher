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
    /// <summary>Holds off Compact, Export, Import and DNS repair while the assistant uses the distro.</summary>
    bool TryBeginDistroActivity();
    void EndDistroActivity();

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

    private readonly IAssistantCli _cli;
    private readonly IAssistantHost _host;
    private readonly IAssistantStateStore _store;
    private readonly Dispatcher _dispatcher;
    private CodexAppServerClient? _client;
    private AssistantTurnRunner? _turns;
    private int _sessionGeneration;
    private bool _ownsDistroActivity;
    private AssistantState _state = AssistantState.NotChecked;
    private string _statusText = string.Empty;
    private string _statusDetail = "Connect starts the assistant. Nothing runs until you ask.";
    private string _providerText = "Codex";
    private string _accountText = "Not connected";
    private AssistantModelOption? _selectedModel;
    private string _relativePathsText = string.Empty;
    private string _taskText = "No task yet";
    private string? _taskId;
    private string? _threadId;
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

    /// <summary>Runs nothing and reads nothing: WSL, the saved task and the model are all touched on Connect.</summary>
    public AssistantViewModel(IAssistantCli cli, IAssistantHost host, IAssistantStateStore store)
    {
        _cli = cli;
        _host = host;
        _store = store;
        _dispatcher = Dispatcher.CurrentDispatcher;
        _statusText = Describe(AssistantState.NotChecked);
        ConnectCommand = new AsyncRelayCommand(ConnectAsync, () => _client is null && State is not (AssistantState.Checking or AssistantState.Busy or AssistantState.Applying));
        RunProbeCommand = new AsyncRelayCommand(RunProbeAsync, () => State == AssistantState.IsolationFailed);
        SignInCommand = new AsyncRelayCommand(SignInAsync, () => State == AssistantState.SignedOut && _client is not null);
        OpenSignInUrlCommand = new RelayCommand(() => _host.OpenUrl(SignInUrl), () => Uri.TryCreate(SignInUrl, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps);
        StopCommand = new AsyncRelayCommand(StopAsync, () => _client is not null && State != AssistantState.Applying);
        SendCommand = new AsyncRelayCommand(SendAsync, () => State == AssistantState.Ready && InputText.Trim().Length > 0);
        NewTaskCommand = new AsyncRelayCommand(NewTaskAsync, () => State is AssistantState.Ready or AssistantState.Disconnected or AssistantState.Error or AssistantState.SignedOut);
        // An unconfirmed live change must be checked before another batch can be reviewed.
        ReviewCommand = new AsyncRelayCommand(ReviewAsync, () => State is AssistantState.Ready or AssistantState.Disconnected && _taskId is not null && !_canCheckOutcome);
        ApproveCommand = new AsyncRelayCommand(ApproveAsync, () => State == AssistantState.Review && _review is not null);
        RejectCommand = new RelayCommand(Reject, () => State == AssistantState.Review);
        RollbackCommand = new AsyncRelayCommand(RollbackAsync, () => _canRollback && _taskId is not null && State is AssistantState.Ready or AssistantState.Disconnected or AssistantState.Error);
        CheckOutcomeCommand = new AsyncRelayCommand(CheckOutcomeAsync, () => _canCheckOutcome && _taskId is not null && State is not (AssistantState.Applying or AssistantState.Busy));
    }

    public AsyncRelayCommand ConnectCommand { get; }
    public AsyncRelayCommand RunProbeCommand { get; }
    public AsyncRelayCommand SignInCommand { get; }
    public RelayCommand OpenSignInUrlCommand { get; }
    public AsyncRelayCommand StopCommand { get; }
    public AsyncRelayCommand SendCommand { get; }
    public AsyncRelayCommand NewTaskCommand { get; }
    public AsyncRelayCommand ReviewCommand { get; }
    public AsyncRelayCommand ApproveCommand { get; }
    public RelayCommand RejectCommand { get; }
    public AsyncRelayCommand RollbackCommand { get; }
    public AsyncRelayCommand CheckOutcomeCommand { get; }

    public ObservableCollection<AssistantTranscriptItem> Transcript { get; } = new();
    public ObservableCollection<AssistantModelOption> Models { get; } = new();
    public IReadOnlyList<string> Products { get; } = ["CHIM (HerikaServer)"];
    public string SelectedProduct { get; set; } = "CHIM (HerikaServer)";

    public AssistantState State
    {
        get => _state;
        private set
        {
            if (!SetProperty(ref _state, value)) return;
            OnPropertyChanged(nameof(IsSignInVisible));
            OnPropertyChanged(nameof(IsReviewVisible));
            OnPropertyChanged(nameof(IsProbeVisible));
            OnPropertyChanged(nameof(IsInputEnabled));
            OnPropertyChanged(nameof(StopButtonText));
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
    public string OutcomeText { get => _outcomeText; private set { if (SetProperty(ref _outcomeText, value)) OnPropertyChanged(nameof(HasOutcome)); } }
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
    public bool IsInputEnabled => State is AssistantState.Ready or AssistantState.Streaming;
    public bool IsApplying => State == AssistantState.Applying;
    public string StopButtonText => State == AssistantState.Streaming ? "Stop" : "Disconnect";

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

    // --- Connection ---------------------------------------------------------------------------

    internal async Task ConnectAsync()
    {
        if (!_ownsDistroActivity)
        {
            if (!_host.TryBeginDistroActivity())
            {
                SetState(AssistantState.Error, "Distro maintenance is running. Try again when it finishes.");
                return;
            }
            _ownsDistroActivity = true;
        }

        try
        {
            SetState(AssistantState.Checking, "Reading DwemerDistro capabilities…");
            var capabilities = await _cli.RunAsync(AssistantCliCommands.Capabilities(), QueryTimeout, CancellationToken.None);
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

            var agent = await _cli.RunAsync(AssistantCliCommands.AgentStatus(), QueryTimeout, CancellationToken.None);
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
            await StartSessionAsync();
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
        var status = await _cli.RunAsync(AssistantCliCommands.ChangeStatus(saved.TaskId), QueryTimeout, CancellationToken.None);
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

        SetState(AssistantState.Checking, "Preparing an isolated working copy…");
        var prepared = await _cli.RunAsync(AssistantCliCommands.Prepare("herika", paths), PrepareTimeout, CancellationToken.None);
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
        _taskId = taskId;
        _threadId = null;
        SaveReference();
        TaskText = $"Task {taskId} · CHIM · " + (paths.Length == 0 ? "diagnostic workspace (no files)" : $"{paths.Length} file(s)");
        return true;
    }

    private async Task StartSessionAsync()
    {
        SetState(AssistantState.Checking, "Starting the assistant session…");
        var generation = ++_sessionGeneration;
        IAssistantTransport transport;
        try
        {
            transport = await _cli.StartServeAsync(_taskId!, CancellationToken.None);
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
        SetState(AssistantState.Ready, "Ask about this task's working copy. Changes stay in the isolated workspace until you review and approve them.");
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
                var resume = new JsonObject { ["threadId"] = existing };
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
            var start = new JsonObject();
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
    }

    /// <summary>Identifiers only; the transcript and the account stay out of launcher storage.</summary>
    private void SaveReference()
    {
        if (_taskId is null || _installationId is null) return;
        _store.Save(new AssistantTaskReference(AssistantTaskReference.CurrentVersion, _installationId, _cli.DistroName,
            _provider, "herika", _taskId, _threadId));
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
        var client = _client;
        var turns = _turns;
        var text = InputText.Trim();
        if (client is null || turns is null || _threadId is null || text.Length == 0) return;
        InputText = string.Empty;
        AddItem(new AssistantTranscriptItem("You", text));
        SetState(AssistantState.Streaming, "The assistant is responding. Press Esc or Stop to interrupt.");
        try
        {
            var turn = new JsonObject
            {
                ["threadId"] = _threadId,
                ["input"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text })
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
        if (State != AssistantState.Streaming || _turns is not { } turns) return;
        StatusDetail = "Stopping the response…";
        await turns.StopAsync();
    }

    private async Task StopAsync()
    {
        if (State == AssistantState.Streaming)
        {
            await InterruptAsync();
            return;
        }
        await EndSessionAsync(AssistantState.Disconnected, "Session closed. Connect to continue this task.");
    }

    private async Task NewTaskAsync()
    {
        await EndSessionAsync(AssistantState.NotChecked, "Starting a new task…");
        _store.Clear();
        _taskId = null;
        _threadId = null;
        _canRollback = false;
        _canCheckOutcome = false;
        OutcomeText = string.Empty;
        Transcript.Clear();
        TaskText = "No task yet";
        await ConnectAsync();
    }

    private void OnNotification(string method, JsonElement parameters)
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
                    if (existing is null) AddItem(new AssistantTranscriptItem("Codex", final, id));
                    else if (final.Length > 0) existing.Replace(final);
                }
                else if (type == "commandExecution")
                {
                    AddSystem("Ran in the workspace: " + Shorten(AssistantCliService.GetString(item, "command"), 160));
                }
                else if (type == "fileChange")
                {
                    AddSystem("Edited files in the isolated workspace. Use Review changes to see them.");
                }
                break;
            case "turn/completed":
                var turn = Prop(parameters, "turn");
                var status = AssistantCliService.GetString(turn, "status");
                if (status == "interrupted") AddSystem("Stopped.");
                else if (status == "failed")
                    AddSystem("The response failed: " + Shorten(AssistantCliService.GetString(Prop(turn, "error"), "message"), 200));
                if (State == AssistantState.Streaming) SetState(AssistantState.Ready, "Response complete.");
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
        if (client is not null)
        {
            try { await client.DisposeAsync(); }
            catch (Exception ex) { LauncherLogService.Operation($"Assistant session cleanup failed: {ex.GetType().Name}"); }
        }
        ReleaseDistroActivity();
        RaiseCommands();
    }

    public Task ShutdownAsync() => DisposeClientAsync();

    // --- Review and live changes --------------------------------------------------------------

    private async Task ReviewAsync()
    {
        // A new review always replaces the old one: whatever happens below, the earlier batch can no longer be approved.
        _review = null;
        RaiseReview();
        SetState(AssistantState.Busy, "Freezing the current changes for review…");
        var staged = await _cli.RunAsync(AssistantCliCommands.Stage(_taskId!), PrepareTimeout, CancellationToken.None);
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
            result = await _cli.RunAsync(AssistantCliCommands.Apply(batch.TaskId, batch.ManifestSha256), LiveChangeTimeout, CancellationToken.None);
        }
        finally
        {
            _host.EndLiveChange();
        }

        RaiseReview();
        OutcomeText = DescribeApply(result, batch.Files.Count, out _canRollback, out _canCheckOutcome);
        SetState(SessionState(), OutcomeText);
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
            result = await _cli.RunAsync(AssistantCliCommands.Rollback(_taskId!), LiveChangeTimeout, CancellationToken.None);
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
        var status = await _cli.RunAsync(AssistantCliCommands.ChangeStatus(_taskId!), QueryTimeout, CancellationToken.None);
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
            var probe = await _cli.RunAsync(AssistantCliCommands.AgentProbe(), PrepareTimeout, CancellationToken.None);
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

    private void ReleaseDistroActivity()
    {
        if (!_ownsDistroActivity) return;
        _ownsDistroActivity = false;
        _host.EndDistroActivity();
    }

    private AssistantTranscriptItem AddItem(AssistantTranscriptItem item)
    {
        Transcript.Add(item);
        while (Transcript.Count > MaxTranscriptItems) Transcript.RemoveAt(0);
        return item;
    }

    private void AddSystem(string text) => AddItem(new AssistantTranscriptItem("DwemerDistro", text));

    private AssistantTranscriptItem? FindItem(string? itemId) =>
        string.IsNullOrEmpty(itemId) ? null : Transcript.LastOrDefault(item => item.ItemId == itemId);

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
    }

    private void RaiseCommands()
    {
        OnPropertyChanged(nameof(IsApplying));
        foreach (var command in new System.Windows.Input.ICommand[]
                 {
                     ConnectCommand, RunProbeCommand, SignInCommand, OpenSignInUrlCommand, StopCommand, SendCommand, NewTaskCommand,
                     ReviewCommand, ApproveCommand, RejectCommand, RollbackCommand, CheckOutcomeCommand
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
