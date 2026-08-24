using System.Windows;
using DwemerDistro.Launcher.Wpf.ViewModels;

namespace DwemerDistro.Launcher.Wpf.Views;

public partial class InstallComponentsWindow : Window
{
    public InstallComponentsWindow(MainWindowViewModel mainWindowViewModel)
    {
        InitializeComponent();
        DataContext = new InstallComponentsWindowViewModel(mainWindowViewModel);
    }
}
