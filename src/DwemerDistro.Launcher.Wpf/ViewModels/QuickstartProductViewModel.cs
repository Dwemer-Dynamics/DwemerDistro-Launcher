using DwemerDistro.Launcher.Wpf.Models;

namespace DwemerDistro.Launcher.Wpf.ViewModels;

/// <summary>Per-product outcome of the Choose Your Mods install run.</summary>
public enum QuickstartProductInstallState
{
    /// <summary>Not attempted yet.</summary>
    Pending,
    Installing,
    Installed,
    Failed,
    /// <summary>The user chose to move past a failure without retrying.</summary>
    Skipped
}

/// <summary>
/// One product row in the Quickstart "Choose Your Mods" step. Products already present are shown as
/// Installed and cannot be ticked, unticked, or removed from here - uninstalling only lives on the
/// Mods page behind the typed confirmation. Only a product the manager explicitly reports as
/// not-installed can be ticked: an unread, failed, or unrecognised status stays locked so Quickstart
/// never installs over something it could not see.
/// </summary>
public sealed class QuickstartProductViewModel : ObservableObject
{
    private const string StatusNeutral = "#4B4B4B";
    private const string StatusGood = "#285A2D";
    private const string StatusWarn = "#6A3A12";
    private const string StatusBad = "#7A2828";
    private const string StatusBusy = "#3A3224";

    private readonly Func<QuickstartProductViewModel, Task> _retry;
    private readonly Action<QuickstartProductViewModel> _skip;

    private bool _isSelected;
    private ServerInstallState _reportedState = ServerInstallState.Unknown;
    private bool _hasStatusAnswer;
    private QuickstartProductInstallState _installState = QuickstartProductInstallState.Pending;
    private string? _resultDetail;

    public QuickstartProductViewModel(
        GameProfile profile,
        ServerProduct product,
        Func<QuickstartProductViewModel, Task> retry,
        Action<QuickstartProductViewModel> skip)
    {
        Key = profile.Key;
        Product = product;
        Title = profile.Name;
        GameTitle = profile.GameTitle;
        Description = profile.Description;
        ArtworkSource = profile.RailImageSource;
        _retry = retry;
        _skip = skip;

        RetryCommand = new AsyncRelayCommand(() => _retry(this), () => ShowRetry);
        SkipCommand = new RelayCommand(() => _skip(this), () => ShowRetry);
    }

    public string Key { get; }

    public ServerProduct Product { get; }

    public string Title { get; }

    public string GameTitle { get; }

    public string Description { get; }

    /// <summary>Local rail artwork from the launcher assets; nothing is fetched over the network.</summary>
    public string ArtworkSource { get; }

    public AsyncRelayCommand RetryCommand { get; }

    public RelayCommand SkipCommand { get; }

    /// <summary>Nothing is selected by default; the user opts in per product.</summary>
    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (!IsSelectable && value)
            {
                return;
            }

            SetProperty(ref _isSelected, value);
        }
    }

    public bool IsInstalled => _reportedState == ServerInstallState.Installed;

    /// <summary>The last install state the manager reported, or Unknown when it could not be read.</summary>
    public ServerInstallState ReportedState => _reportedState;

    /// <summary>
    /// Only an explicit not-installed answer makes a product installable. An unread status, a failed
    /// probe, a missing entry, a value this build does not know, and needs-repair all fail closed.
    /// </summary>
    public bool IsEligibleForInstall => _reportedState == ServerInstallState.NotInstalled;

    /// <summary>An ineligible product, or one being installed now, cannot be toggled.</summary>
    public bool IsSelectable =>
        IsEligibleForInstall &&
        _installState is not (QuickstartProductInstallState.Installing or QuickstartProductInstallState.Installed);

    public QuickstartProductInstallState InstallState => _installState;

    public bool ShowRetry => _installState == QuickstartProductInstallState.Failed;

    public bool HasResultDetail => !string.IsNullOrWhiteSpace(_resultDetail);

    public string ResultDetail => _resultDetail ?? string.Empty;

    public string StatusText => _installState switch
    {
        QuickstartProductInstallState.Installing => "Installing",
        QuickstartProductInstallState.Installed => "Installed",
        QuickstartProductInstallState.Failed => "Failed",
        QuickstartProductInstallState.Skipped => "Skipped",
        _ => _reportedState switch
        {
            ServerInstallState.Installed => "Installed",
            ServerInstallState.NotInstalled => "Not installed",
            ServerInstallState.NeedsRepair => "Needs repair",
            _ => _hasStatusAnswer ? "Status unknown" : "Checking"
        }
    };

    public string StatusBackground => _installState switch
    {
        QuickstartProductInstallState.Installing => StatusBusy,
        QuickstartProductInstallState.Installed => StatusGood,
        QuickstartProductInstallState.Failed => StatusBad,
        QuickstartProductInstallState.Skipped => StatusWarn,
        _ => _reportedState switch
        {
            ServerInstallState.Installed => StatusGood,
            ServerInstallState.NeedsRepair => StatusWarn,
            _ => StatusNeutral
        }
    };

    public string AccessibleName => $"{Title} for {GameTitle}";

    public string AccessibleHelpText => _reportedState switch
    {
        ServerInstallState.Installed => $"{Title} is already installed. Manage or remove it from the Mods page.",
        ServerInstallState.NotInstalled => $"Select {Title} to install it on the Main branch during Quickstart.",
        ServerInstallState.NeedsRepair => $"{Title} needs repair. Repair or reinstall it from the Mods page.",
        _ => $"{Title} install status is unavailable. Refresh installed mods before installing it."
    };

    /// <summary>
    /// Applies the manager status probe. Anything other than an explicit not-installed answer locks
    /// the row and drops a stale tick, so a refresh cannot leave a selection the guard would reject.
    /// </summary>
    public void ApplyStatus(ServerInstallState state)
    {
        _reportedState = state;
        _hasStatusAnswer = true;
        DropSelectionIfNotEligible();
        RaiseDerivedState();
    }

    public void SetInstallState(QuickstartProductInstallState state, string? detail = null)
    {
        _installState = state;
        _resultDetail = detail;
        if (state == QuickstartProductInstallState.Installed)
        {
            _reportedState = ServerInstallState.Installed;
            _hasStatusAnswer = true;
            DropSelectionIfNotEligible();
        }

        RaiseDerivedState();
    }

    /// <summary>Clears a previous run's outcome so a fresh install attempt starts from Pending.</summary>
    public void ResetInstallState()
    {
        _installState = QuickstartProductInstallState.Pending;
        _resultDetail = null;
        RaiseDerivedState();
    }

    /// <summary>The onboarding record value for this product's outcome.</summary>
    public string ToInstallResultKey()
    {
        return _installState switch
        {
            QuickstartProductInstallState.Installed => "installed",
            QuickstartProductInstallState.Failed => "failed",
            QuickstartProductInstallState.Skipped => "skipped",
            QuickstartProductInstallState.Installing => "interrupted",
            _ => "pending"
        };
    }

    private void DropSelectionIfNotEligible()
    {
        if (!_isSelected || IsEligibleForInstall)
        {
            return;
        }

        _isSelected = false;
        OnPropertyChanged(nameof(IsSelected));
    }

    private void RaiseDerivedState()
    {
        OnPropertyChanged(nameof(IsInstalled));
        OnPropertyChanged(nameof(ReportedState));
        OnPropertyChanged(nameof(IsEligibleForInstall));
        OnPropertyChanged(nameof(IsSelectable));
        OnPropertyChanged(nameof(InstallState));
        OnPropertyChanged(nameof(ShowRetry));
        OnPropertyChanged(nameof(HasResultDetail));
        OnPropertyChanged(nameof(ResultDetail));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(StatusBackground));
        OnPropertyChanged(nameof(AccessibleHelpText));
        RetryCommand.RaiseCanExecuteChanged();
        SkipCommand.RaiseCanExecuteChanged();
    }
}
