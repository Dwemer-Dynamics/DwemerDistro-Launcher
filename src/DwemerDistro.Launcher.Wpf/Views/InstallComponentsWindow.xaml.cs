using System.Windows;
using DwemerDistro.Launcher.Wpf.ViewModels;

namespace DwemerDistro.Launcher.Wpf.Views;

public partial class InstallComponentsWindow : Window
{
    private readonly InstallComponentsWindowViewModel _viewModel;

    public InstallComponentsWindow(MainWindowViewModel mainWindowViewModel)
    {
        InitializeComponent();
        _viewModel = new InstallComponentsWindowViewModel(mainWindowViewModel);
        DataContext = _viewModel;
        Loaded += InstallComponentsWindow_Loaded;
    }

    private async void InstallComponentsWindow_Loaded(object sender, RoutedEventArgs e)
    {
        Loaded -= InstallComponentsWindow_Loaded;
        await _viewModel.InitializeAsync().ConfigureAwait(true);
    }
}
