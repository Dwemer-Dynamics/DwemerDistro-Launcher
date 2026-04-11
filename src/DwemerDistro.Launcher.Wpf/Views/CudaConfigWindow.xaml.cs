using System.Windows;
using DwemerDistro.Launcher.Wpf.ViewModels;

namespace DwemerDistro.Launcher.Wpf.Views;

public partial class CudaConfigWindow : Window
{
    private readonly MainWindowViewModel _viewModel;

    public CudaConfigWindow(MainWindowViewModel viewModel, string currentGpu)
    {
        InitializeComponent();
        _viewModel = viewModel;

        var normalizedGpu = NormalizeGpuValue(currentGpu);
        CurrentSettingTextBlock.Text = $"Current Setting: {FormatGpuLabel(normalizedGpu)}";
        SetSelectedGpu(normalizedGpu);
    }

    private async void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        await _viewModel.SaveCudaSettingAsync(GetSelectedGpu(), this);
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void GpuOption_Checked(object sender, RoutedEventArgs e)
    {
        SelectedSettingTextBlock.Text = $"Selected: {FormatGpuLabel(GetSelectedGpu())}";
    }

    private void SetSelectedGpu(string gpuValue)
    {
        switch (gpuValue)
        {
            case "0":
                Gpu0RadioButton.IsChecked = true;
                break;
            case "1":
                Gpu1RadioButton.IsChecked = true;
                break;
            case "2":
                Gpu2RadioButton.IsChecked = true;
                break;
            case "3":
                Gpu3RadioButton.IsChecked = true;
                break;
            default:
                AllGpuRadioButton.IsChecked = true;
                break;
        }

        SelectedSettingTextBlock.Text = $"Selected: {FormatGpuLabel(gpuValue)}";
    }

    private string GetSelectedGpu()
    {
        if (Gpu0RadioButton.IsChecked == true)
        {
            return "0";
        }

        if (Gpu1RadioButton.IsChecked == true)
        {
            return "1";
        }

        if (Gpu2RadioButton.IsChecked == true)
        {
            return "2";
        }

        if (Gpu3RadioButton.IsChecked == true)
        {
            return "3";
        }

        return "all";
    }

    private static string NormalizeGpuValue(string? gpuValue)
    {
        return gpuValue is "0" or "1" or "2" or "3" ? gpuValue : "all";
    }

    private static string FormatGpuLabel(string gpuValue)
    {
        return gpuValue == "all" ? "All GPUs" : $"GPU {gpuValue}";
    }
}
