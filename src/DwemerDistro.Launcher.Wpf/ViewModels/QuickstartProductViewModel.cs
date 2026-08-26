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
/// Mods page behind the typed confirmation.
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
    private bool _isInstalled;
    private bool _isStatusKnown;
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

    public bool IsInstalled => _isInstalled;

    /// <summary>An already-installed product, or one being installed now, cannot be toggled.</summary>
    public bool IsSelectable =>
        !_isInstalled &&
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
        _ when _isInstalled => "Installed",
        _ when !_isStatusKnown => "Checking",
        _ => "Not installed"
    };

    public string StatusBackground => _installState switch
    {
        QuickstartProductInstallState.Installing => StatusBusy,
        QuickstartProductInstallState.Installed => StatusGood,
        QuickstartProductInstallState.Failed => StatusBad,
        QuickstartProductInstallState.Skipped => StatusWarn,
        _ when _isInstalled => StatusGood,
        _ => StatusNeutral
    };

    public string AccessibleName => $"{Title} for {GameTitle}";

    public string AccessibleHelpText =>
        _isInstalled
            ? $"{Title} is already installed. Manage or remove it from the Mods page."
            : $"Select {Title} to install it on the Main branch during Quickstart.";

    /// <summary>Applies the manager status probe. An installed product is locked out of selection.</summary>
    public void ApplyInstalledState(bool isInstalled, bool isStatusKnown)
    {
        _isInstalled = isInstalled;
        _isStatusKnown = isStatusKnown;
        if (isInstalled && _isSelected)
        {
            _isSelected = false;
            OnPropertyChanged(nameof(IsSelected));
        }

        RaiseDerivedState();
    }

    public void SetInstallState(QuickstartProductInstallState state, string? detail = null)
    {
        _installState = state;
        _resultDetail = detail;
        if (state == QuickstartProductInstallState.Installed)
        {
            _isInstalled = true;
            _isStatusKnown = true;
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

    private void RaiseDerivedState()
    {
        OnPropertyChanged(nameof(IsInstalled));
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
