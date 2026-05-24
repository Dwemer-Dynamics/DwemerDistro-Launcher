using System.Collections.ObjectModel;
using System.Windows.Input;

namespace DwemerDistro.Launcher.Wpf.ViewModels;

public sealed class InstallComponentItemViewModel : ObservableObject
{
    private bool _isInstalled;
    private string _statusText = "Checking...";
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
        ICommand? secondaryActionCommand = null)
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
    }

    public string Key { get; }

    public string Title { get; }

    public string Description { get; }

    public string InstallCheckExpression { get; }

    public ICommand PrimaryCommand { get; }

    public ICommand? SecondaryActionCommand { get; }

    public string? SecondaryActionText { get; }

    public bool SupportsNvidiaCuda { get; }

    public bool SupportsAmdCpu { get; }

    public bool HasSecondaryAction => SecondaryActionCommand is not null && !string.IsNullOrWhiteSpace(SecondaryActionText);

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
        private set => SetProperty(ref _statusText, value);
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
        StatusBackground = "#555555";
        StatusForeground = "White";
    }

    public void SetInstalledState(bool installed)
    {
        IsInstalled = installed;
        StatusText = installed ? "Installed" : "Not installed";
        StatusBackground = installed ? "#285A2D" : "#6A3A12";
        StatusForeground = "White";
    }

    public void SetUnknownState()
    {
        IsInstalled = false;
        StatusText = "Unknown";
        StatusBackground = "#4F3C7A";
        StatusForeground = "White";
    }
}

public sealed class InstallComponentSectionViewModel
{
    public InstallComponentSectionViewModel(string title, IEnumerable<InstallComponentItemViewModel> items)
    {
        Title = title;
        Items = new ObservableCollection<InstallComponentItemViewModel>(items);
    }

    public string Title { get; }

    public ObservableCollection<InstallComponentItemViewModel> Items { get; }
}
