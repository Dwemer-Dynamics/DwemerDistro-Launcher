using System.Windows;
using DwemerDistro.Launcher.Wpf.ViewModels;
using MessageBox = System.Windows.MessageBox;

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

    private async void EditHuggingFaceTokenButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var currentToken = await _viewModel.ReadHuggingFaceTokenAsync().ConfigureAwait(true);
            var window = new HuggingFaceTokenWindow(currentToken)
            {
                Owner = this
            };

            if (window.ShowDialog() != true)
            {
                return;
            }

            var error = window.ShouldClearToken
                ? await _viewModel.ClearHuggingFaceTokenAsync().ConfigureAwait(true)
                : await _viewModel.SaveHuggingFaceTokenAsync(window.TokenValue).ConfigureAwait(true);

            if (!string.IsNullOrWhiteSpace(error))
            {
                MessageBox.Show(
                    $"Failed to update Hugging Face token.\n\n{error}",
                    "Hugging Face Token",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                return;
            }

            await _viewModel.RefreshHuggingFaceTokenStatusAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"Failed to update Hugging Face token.\n\n{ex.Message}",
                "Hugging Face Token",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }
}
