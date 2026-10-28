using System.Collections.ObjectModel;
using System.Windows.Input;

namespace DwemerDistro.Launcher.Wpf.ViewModels;

public sealed class InstallComponentItemViewModel : ObservableObject
{
    private bool _isInstalled;
    private string _statusText = "Checking...";
    private string _statusDetailText = string.Empty;
    private string _connectorStatusText = string.Empty;
    private string _statusBackground = "#555555";
    private string _statusForeground = "White";

    public InstallComponentItemViewModel(
        string key,
        string title,
        string description,
        string installCheckExpression,
        ICommand primaryCommand,
        bool supportsNvidiaCuda = false,
        bool supportsAmdCpu = false,
        string? secondaryActionText = null,
        ICommand? secondaryActionCommand = null,
        string? tertiaryActionText = null,
        ICommand? tertiaryActionCommand = null,
        string? artworkSource = null,
        IReadOnlyList<string>? details = null,
        int? port = null)
    {
        Key = key;
        Title = title;
        Description = description;
        InstallCheckExpression = installCheckExpression;
        PrimaryCommand = primaryCommand;
        SupportsNvidiaCuda = supportsNvidiaCuda;
        SupportsAmdCpu = supportsAmdCpu;
        SecondaryActionText = secondaryActionText;
        SecondaryActionCommand = secondaryActionCommand;
        TertiaryActionText = tertiaryActionText;
        TertiaryActionCommand = tertiaryActionCommand;
        ArtworkSource = artworkSource;
        Details = details ?? [];
        PortText = port is { } value ? $"Port {value}" : string.Empty;
    }

    public string Key { get; }

    public string Title { get; }

    public string Description { get; }

    /// <summary>Pack URI of the small project or creator mark shown in the card corner.</summary>
    public string? ArtworkSource { get; }

    /// <summary>Install caveats that must stay visible on the card, one short line each.</summary>
    public IReadOnlyList<string> Details { get; }

    public bool HasDetails => Details.Count > 0;

    public string PortText { get; }

    public bool HasPort => !string.IsNullOrWhiteSpace(PortText);

    /// <summary>
    /// Current configuration rows from the live probe. Usually the card's own conf.sh; a companion service
    /// without a card of its own (Text2Vec, llama.cpp) is listed on the card it ships beside.
    /// </summary>
    public ObservableCollection<ConfigurableComponentItemViewModel> Configurations { get; } = [];

    public bool HasConfiguration => Configurations.Count > 0;

    public void SetConfigurations(IEnumerable<ConfigurableComponentItemViewModel> configurations)
    {
        Configurations.Clear();
        foreach (var configuration in configurations)
        {
            Configurations.Add(configuration);
        }

        OnPropertyChanged(nameof(HasConfiguration));
    }

    public string InstallCheckExpression { get; }

    public ICommand PrimaryCommand { get; }

    public ICommand? SecondaryActionCommand { get; }

    public ICommand? TertiaryActionCommand { get; }

    public string? SecondaryActionText { get; }

    public string? TertiaryActionText { get; }

    public bool SupportsNvidiaCuda { get; }

    public bool SupportsAmdCpu { get; }

    public bool HasSecondaryAction => SecondaryActionCommand is not null && !string.IsNullOrWhiteSpace(SecondaryActionText);

    public bool HasTertiaryAction => TertiaryActionCommand is not null && !string.IsNullOrWhiteSpace(TertiaryActionText);

    public bool IsInstalled
    {
        get => _isInstalled;
        private set
        {
            if (SetProperty(ref _isInstalled, value))
            {
                OnPropertyChanged(nameof(PrimaryButtonText));
            }
        }
    }

    public string PrimaryButtonText => IsInstalled ? "Reinstall" : "Install";

    public string StatusText
    {
        get => _statusText;
        private set
        {
            if (SetProperty(ref _statusText, value))
            {
                OnPropertyChanged(nameof(ShowsStatusDetail));
            }
        }
    }

    public string StatusDetailText
    {
        get => _statusDetailText;
        private set
        {
            if (SetProperty(ref _statusDetailText, value))
            {
                OnPropertyChanged(nameof(HasStatusDetail));
                OnPropertyChanged(nameof(ShowsStatusDetail));
            }
        }
    }

    public bool HasStatusDetail => !string.IsNullOrWhiteSpace(StatusDetailText);

    // A not-installed probe only restates the dedicated port the card already shows.
    public bool ShowsStatusDetail => HasStatusDetail && StatusText != "Not installed";

    /// <summary>Result of the last game connector sync; kept across install-state refreshes.</summary>
    public string ConnectorStatusText
    {
        get => _connectorStatusText;
        private set
        {
            if (SetProperty(ref _connectorStatusText, value))
            {
                OnPropertyChanged(nameof(HasConnectorStatus));
            }
        }
    }

    public bool HasConnectorStatus => !string.IsNullOrWhiteSpace(ConnectorStatusText);

    public void SetConnectorStatus(string text)
    {
        ConnectorStatusText = text;
    }

    public string StatusBackground
    {
        get => _statusBackground;
        private set => SetProperty(ref _statusBackground, value);
    }

    public string StatusForeground
    {
        get => _statusForeground;
        private set => SetProperty(ref _statusForeground, value);
    }

    public void SetCheckingState()
    {
        StatusText = "Checking";
        StatusDetailText = string.Empty;
        StatusBackground = "#555555";
        StatusForeground = "White";
    }

    public void SetInstalledState(bool installed)
    {
        IsInstalled = installed;
        StatusText = installed ? "Installed" : "Not installed";
        StatusDetailText = string.Empty;
        StatusBackground = installed ? "#285A2D" : "#6A3A12";
        StatusForeground = "White";
    }

    public void SetProbeState(
        bool installed,
        string statusText,
        string statusBackground,
        string detailText)
    {
        IsInstalled = installed;
        StatusText = string.IsNullOrWhiteSpace(statusText)
            ? installed ? "Installed" : "Not installed"
            : statusText;
        StatusDetailText = detailText;
        StatusBackground = string.IsNullOrWhiteSpace(statusBackground)
            ? installed ? "#285A2D" : "#6A3A12"
            : statusBackground;
        StatusForeground = "White";
    }

    public void SetUnknownState()
    {
        IsInstalled = false;
        StatusText = "Unknown";
        StatusDetailText = string.Empty;
        StatusBackground = "#4F3C7A";
        StatusForeground = "White";
    }
}

public sealed class InstallComponentSectionViewModel : ObservableObject
{
    private bool _isCurrent;

    public InstallComponentSectionViewModel(string title, IEnumerable<InstallComponentItemViewModel> items)
    {
        Title = title;
        Items = new ObservableCollection<InstallComponentItemViewModel>(items);
    }

    public string Title { get; }

    public ObservableCollection<InstallComponentItemViewModel> Items { get; }

    /// <summary>The section last jumped to from the section links; it only highlights, never filters.</summary>
    public bool IsCurrent
    {
        get => _isCurrent;
        set => SetProperty(ref _isCurrent, value);
    }
}
