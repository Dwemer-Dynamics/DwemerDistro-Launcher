using System.ComponentModel;
using System.Windows;
using DwemerDistro.Launcher.Wpf.Services;

namespace DwemerDistro.Launcher.Wpf.Views;

public partial class ApiKeysWindow : Window
{
    private readonly ApiKeysView _editor;
    public ApiKeysWindow(ApiKeysService service)
    {
        InitializeComponent();
        _editor = new ApiKeysView(service);
        EditorHost.Content = _editor;
    }
    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_editor.IsBusy) { e.Cancel = true; _editor.ShowCloseBlocked(); }
    }
}
