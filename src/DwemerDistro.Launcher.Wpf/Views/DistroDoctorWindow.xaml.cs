using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using DwemerDistro.Launcher.Wpf.ViewModels;

namespace DwemerDistro.Launcher.Wpf.Views;

public partial class DistroDoctorWindow : Window
{
    private readonly DistroDoctorViewModel _viewModel;

    internal DistroDoctorWindow(DistroDoctorViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
    }

    private void LogTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        LogTextBox.ScrollToEnd();
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_viewModel.CanClose)
        {
            return;
        }

        e.Cancel = true;
        _viewModel.NotifyCloseBlocked();
    }
}
