using DwemerDistro.Launcher.Wpf.Services;

namespace DwemerDistro.Launcher.Wpf.ViewModels;

/// <summary>The one step the Assistant page offers while the assistant cannot take messages.</summary>
public enum AssistantSetupAction { None, Connect, SignIn, OpenSignInPage, OpenApiKeys, RunProbe, Repair }

/// <summary>
/// How the Assistant page presents the session: a centered setup card with one action while nothing can be sent, a compact
/// header once it can. Read-only views of the existing state and commands; nothing here connects, signs in or reads a key.
/// </summary>
public sealed partial class AssistantViewModel
{
    private static readonly HashSet<string> PresentationSources =
    [
        nameof(State), nameof(StatusText), nameof(StatusDetail), nameof(OpenRouterKeyStatus), nameof(CanSwitchProvider), nameof(IsOpenRouterConnected), nameof(IsOpenRouterKeyMissing),
        nameof(IsHomeConfigRepairOffered), nameof(IsExporting), nameof(SignInUrl), nameof(ProviderHint), nameof(HasRunningJobs)
    ];

    private bool _isConversationShownOffline;
    private bool _wasSessionConnected;

    public RelayCommand ShowConversationCommand { get; private set; } = null!;
    public RelayCommand ShowSetupCommand { get; private set; } = null!;

    private void InitializePresentation()
    {
        ShowConversationCommand = new RelayCommand(() => IsConversationShownOffline = true);
        ShowSetupCommand = new RelayCommand(() => IsConversationShownOffline = false);
        PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is { } name && PresentationSources.Contains(name)) RaisePresentation();
        };
        Transcript.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasConversation));
            OnPropertyChanged(nameof(IsOnboardingShown));
            OnPropertyChanged(nameof(IsOfflineConversationShown));
        };
    }

    /// <summary>
    /// A session that is open and can work: ChatGPT signed in, Claude Code ready, or OpenRouter with a usable key. Responding,
    /// review and applying count; a missing or still-checking OpenRouter key does not.
    /// </summary>
    public bool IsSessionConnected => _client is not null
        && State is AssistantState.Ready or AssistantState.Busy or AssistantState.Streaming or AssistantState.Review or AssistantState.Applying
        && (!IsOpenRouter || IsOpenRouterConnected);

    /// <summary>Messages can be sent now (Send still needs a draft or attachment).</summary>
    public bool IsSessionUsable => IsSessionConnected && State == AssistantState.Ready && !IsExporting;

    public bool HasConversation => Transcript.Count > 0;

    /// <summary>
    /// The header's state. The session can be Ready while an OpenRouter key is missing or still being read; the header never says
    /// Ready then. Otherwise exactly <see cref="StatusText"/>.
    /// </summary>
    public string HeaderStatusText =>
        _isStartupHeld ? "Preparing…"
        : IsOpenRouter && _client is not null && !IsOpenRouterConnected && State is AssistantState.Ready or AssistantState.Busy
            ? IsOpenRouterKeyMissing ? "OpenRouter key needed" : "Checking OpenRouter key…"
            : StatusText;

    /// <summary>The user chose to read the kept conversation instead of the setup card while not connected.</summary>
    public bool IsConversationShownOffline
    {
        get => _isConversationShownOffline;
        private set
        {
            if (!SetProperty(ref _isConversationShownOffline, value)) return;
            OnPropertyChanged(nameof(IsOnboardingShown));
            OnPropertyChanged(nameof(IsOfflineConversationShown));
        }
    }

    /// <summary>The centered setup card covers the conversation.</summary>
    public bool IsOnboardingShown => !IsSessionConnected && !(IsConversationShownOffline && HasConversation);

    /// <summary>Not connected, reading the kept conversation; a strip above it leads back to setup.</summary>
    public bool IsOfflineConversationShown => !IsSessionConnected && IsConversationShownOffline && HasConversation;

    /// <summary>Claude Code itself was found missing, too old or signed out: the fix happens outside DwemerDistro, then Check again.</summary>
    private bool IsClaudeCodeNotReady => IsClaude && _client is null && State == AssistantState.Error
        && _claudeCode is { State: not ClaudeCodeState.Ready };

    public AssistantSetupAction SetupAction =>
        // Startup's wait offers no step: Connect would only be refused, and nothing else applies yet.
        _isStartupHeld ? AssistantSetupAction.None
        : IsHomeConfigRepairOffered ? AssistantSetupAction.Repair
        : State == AssistantState.IsolationFailed ? AssistantSetupAction.RunProbe
        : State == AssistantState.SigningIn ? (SignInUrl.Length > 0 ? AssistantSetupAction.OpenSignInPage : AssistantSetupAction.None)
        : State == AssistantState.SignedOut && _client is not null && IsChatGptSignInShown ? AssistantSetupAction.SignIn
        : IsOpenRouter && _client is not null && !IsOpenRouterConnected ? (IsOpenRouterKeyMissing ? AssistantSetupAction.OpenApiKeys : AssistantSetupAction.None)
        : _client is null ? AssistantSetupAction.Connect
        : AssistantSetupAction.None;

    public bool IsConnectPrimary => SetupAction == AssistantSetupAction.Connect;
    public bool IsSignInPrimary => SetupAction == AssistantSetupAction.SignIn;
    public bool IsApiKeysPrimary => SetupAction == AssistantSetupAction.OpenApiKeys;
    public bool IsProbePrimary => SetupAction == AssistantSetupAction.RunProbe;

    /// <summary>The supported sign-in page and code, once Core has issued them.</summary>
    public bool IsSignInPanelShown => IsSignInVisible && SignInUrl.Length > 0;

    /// <summary>Startup is still settling, or a connection is being made: the card shows indeterminate progress, never a percentage.</summary>
    public bool IsSetupProgressShown => _isStartupHeld || State == AssistantState.Checking;

    // Always Connect (the state's own detail says "press Connect"), except while it is already connecting.
    public string ConnectActionLabel => State == AssistantState.Checking ? "Connecting…" : "Connect";

    /// <summary>The card's explanation: the OpenRouter key's own status while that is what blocks, otherwise the state's detail.</summary>
    public string OnboardingDetail =>
        _isStartupHeld ? _startupPhase
        : IsOpenRouter && _client is not null && !IsOpenRouterConnected && State != AssistantState.Checking ? OpenRouterKeyStatus : StatusDetail;

    /// <summary>Why the one action cannot run now; empty when it can, or while Connecting… says it.</summary>
    public string SetupBlockedReason =>
        SetupAction != AssistantSetupAction.Connect || ConnectCommand.CanExecute(null) || State == AssistantState.Checking ? string.Empty
        : IsMaintenanceHeld ? "DwemerDistro maintenance is using the distro. Connect becomes available when it finishes."
        : "Wait for the current step to finish.";

    public bool HasSetupBlockedReason => SetupBlockedReason.Length > 0;

    public string OnboardingTitle =>
        _isStartupHeld ? "Preparing your assistant"
        : SetupAction == AssistantSetupAction.Repair ? "Repair the assistant's settings file?"
        : IsClaudeCodeNotReady ? "Claude Code is not ready"
        : SetupAction == AssistantSetupAction.SignIn ? "Sign in to continue"
        : SetupAction == AssistantSetupAction.OpenSignInPage ? "Finish signing in"
        : SetupAction == AssistantSetupAction.OpenApiKeys ? "An OpenRouter key is needed"
        : IsOpenRouter && _client is not null && !IsOpenRouterConnected && State != AssistantState.Checking ? "Checking your OpenRouter key…"
        : State switch
        {
            AssistantState.NotChecked => "Assistant is not connected",
            AssistantState.Disconnected => "Assistant is disconnected",
            AssistantState.Checking => "Connecting…",
            AssistantState.Error => "The assistant could not connect",
            _ => StatusText
        };

    public string OnboardingNextStep => _isStartupHeld
        ? _startupWillConnect ? "We'll connect automatically when setup is ready." : "You can connect once the startup checks finish."
        : SetupAction switch
    {
        AssistantSetupAction.Connect when IsClaudeCodeNotReady =>
            "Fix it in a terminal as described above, then press Connect to check again. DwemerDistro never signs in to Claude for you.",
        AssistantSetupAction.Connect when State == AssistantState.Checking => string.Empty,
        AssistantSetupAction.Connect when IsClaude =>
            "DwemerDistro checks the distro and Claude Code on this PC. Claude sign-in happens in Claude Code itself: run claude in a terminal once.",
        AssistantSetupAction.Connect when IsOpenRouter =>
            "DwemerDistro checks the distro, then uses the OpenRouter key saved in API Keys.",
        AssistantSetupAction.Connect =>
            "DwemerDistro checks the distro, then asks you to sign in with ChatGPT if you are not signed in yet.",
        AssistantSetupAction.SignIn =>
            "You get a sign-in page and a one-time code here. Nothing opens until you click Open sign-in page.",
        AssistantSetupAction.OpenSignInPage =>
            "Open the sign-in page, enter the code and approve. The assistant continues once ChatGPT confirms it.",
        AssistantSetupAction.OpenApiKeys =>
            "Add or restore your OpenRouter key in API Keys, then come back to this tab. The key is read again when you return.",
        AssistantSetupAction.RunProbe => "Run the check again after fixing what it reported.",
        AssistantSetupAction.Repair => "Repair moves the file into quarantine and reconnects. Cancel changes nothing.",
        _ => string.Empty
    };

    public bool HasOnboardingNextStep => OnboardingNextStep.Length > 0;

    /// <summary>Why Send is not available, under the composer; empty when messages can be sent.</summary>
    public string SendBlockedReason =>
        IsSessionUsable ? string.Empty
        : _isStartupHeld ? "The assistant is getting ready while the launcher finishes its startup checks."
        : IsHomeConfigRepairOffered ? "Repair or cancel the settings-file prompt before connecting."
        : IsExporting ? "Wait for the chat export to finish, or cancel it."
        : State switch
        {
            AssistantState.Streaming => "The assistant is responding. Esc or Stop interrupts it.",
            AssistantState.Busy => "Wait for the current step to finish.",
            AssistantState.Review => "Approve or reject the reviewed changes first.",
            AssistantState.Applying => "Applying the reviewed changes…",
            AssistantState.Checking => "Connecting…",
            AssistantState.SignedOut => "Sign in to send messages.",
            AssistantState.SigningIn => "Finish signing in to send messages.",
            _ when IsOpenRouterKeyMissing => "Add an OpenRouter key in API Keys to send messages.",
            _ when IsOpenRouter && _client is not null && !IsOpenRouterConnected => "Checking your OpenRouter key…",
            _ when _client is null => "Not connected. Connect to send messages.",
            _ => "Messages can be sent once the assistant is ready."
        };

    public bool HasSendBlockedReason => SendBlockedReason.Length > 0;

    private void RaisePresentation()
    {
        var connected = IsSessionConnected;
        // Each time a session opens, the next loss of it shows the setup card again rather than an unexplained read-only chat.
        if (connected && !_wasSessionConnected) IsConversationShownOffline = false;
        _wasSessionConnected = connected;
        foreach (var name in new[]
                 {
                     nameof(HeaderStatusText), nameof(IsSessionConnected), nameof(IsSessionUsable), nameof(IsOnboardingShown), nameof(IsOfflineConversationShown),
                     nameof(SetupAction), nameof(IsConnectPrimary), nameof(IsSignInPrimary), nameof(IsApiKeysPrimary), nameof(IsProbePrimary),
                     nameof(IsSignInPanelShown), nameof(IsSetupProgressShown), nameof(ConnectActionLabel), nameof(SetupBlockedReason), nameof(HasSetupBlockedReason),
                     nameof(OnboardingTitle), nameof(OnboardingDetail), nameof(OnboardingNextStep), nameof(HasOnboardingNextStep),
                     nameof(SendBlockedReason), nameof(HasSendBlockedReason)
                 })
            OnPropertyChanged(name);
    }
}
