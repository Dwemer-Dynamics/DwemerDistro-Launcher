using System.ComponentModel;
using System.Windows;
using DwemerDistro.Launcher.Wpf.ViewModels;

namespace DwemerDistro.Launcher.Wpf.Views;

public partial class FirstRunSetupWindow : Window
{
    private readonly FirstRunSetupViewModel _viewModel;

    public FirstRunSetupWindow(MainWindowViewModel mainWindowViewModel)
    {
        InitializeComponent();
        _viewModel = new FirstRunSetupViewModel(mainWindowViewModel);
        DataContext = _viewModel;
        _viewModel.RequestClose += ViewModel_RequestClose;
        _viewModel.PropertyChanged += ViewModel_PropertyChanged;
        Loaded += FirstRunSetupWindow_Loaded;
        Closed += FirstRunSetupWindow_Closed;
    }

    private async void FirstRunSetupWindow_Loaded(object sender, RoutedEventArgs e)
    {
        Loaded -= FirstRunSetupWindow_Loaded;
        await _viewModel.InitializeAsync().ConfigureAwait(true);
    }

    private void FirstRunSetupWindow_Closed(object? sender, EventArgs e)
    {
        _viewModel.RequestClose -= ViewModel_RequestClose;
        _viewModel.PropertyChanged -= ViewModel_PropertyChanged;
    }

    private void ViewModel_RequestClose()
    {
        Close();
    }

    private void OpenRouterPasswordBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        _viewModel.OpenRouterKey = OpenRouterPasswordBox.Password;
    }

    private void HuggingFacePasswordBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        _viewModel.HuggingFaceTokenValue = HuggingFacePasswordBox.Password;
    }

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(FirstRunSetupViewModel.OpenRouterKey) &&
            string.IsNullOrEmpty(_viewModel.OpenRouterKey) &&
            !string.IsNullOrEmpty(OpenRouterPasswordBox.Password))
        {
            OpenRouterPasswordBox.Clear();
        }

        if (e.PropertyName == nameof(FirstRunSetupViewModel.HuggingFaceTokenValue) &&
            string.IsNullOrEmpty(_viewModel.HuggingFaceTokenValue) &&
            !string.IsNullOrEmpty(HuggingFacePasswordBox.Password))
        {
            HuggingFacePasswordBox.Clear();
        }
    }
}

