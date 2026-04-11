using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using DwemerDistro.Launcher.Wpf.ViewModels;

namespace DwemerDistro.Launcher.Wpf;

public partial class MainWindow : Window
{
    private readonly MainWindowViewModel _viewModel = new();

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _viewModel;
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        await _viewModel.InitializeAsync();
    }

    private async void Window_Closing(object? sender, CancelEventArgs e)
    {
        await _viewModel.ShutdownAsync();
    }

    private void OutputTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        OutputTextBox.ScrollToEnd();
    }
}

