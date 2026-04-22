using System.Windows;
using Application = System.Windows.Application;

namespace DwemerDistro.Launcher.Wpf;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var window = new MainWindow();
        MainWindow = window;
        window.Show();
    }
}
