using System.ComponentModel;
using System.Windows;
using DwemerDistro.Launcher.Wpf.Services;
using DwemerDistro.Launcher.Wpf.ViewModels;
using MessageBox = System.Windows.MessageBox;

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
        try
        {
            LauncherLogService.Startup("First-time setup initialization started.");
            using var initializationTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            await _viewModel.InitializeAsync(initializationTimeout.Token).ConfigureAwait(true);
            LauncherLogService.Startup("First-time setup initialization completed.");
        }
        catch (OperationCanceledException)
        {
            LauncherLogService.Startup("First-time setup initialization timed out.");
            MessageBox.Show(
                "First-time setup checks timed out. The launcher is still usable; retry setup after WSL finishes starting or run Distro Doctor from Debugging.",
                "First-Time Setup",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
        catch (Exception ex)
        {
            LauncherLogService.Startup("First-time setup initialization failed.", ex);
            MessageBox.Show(
                $"First-time setup failed to initialize.\n\n{ex.Message}\n\nDetails were written to:\n{LauncherLogService.StartupLogPath}",
                "First-Time Setup",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
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

