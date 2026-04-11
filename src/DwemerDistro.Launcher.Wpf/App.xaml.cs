using System.Windows;
using Velopack;

namespace DwemerDistro.Launcher.Wpf;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        VelopackApp
            .Build()
            .SetAutoApplyOnStartup(true)
            .Run();

        base.OnStartup(e);

        var window = new MainWindow();
        MainWindow = window;
        window.Show();
    }
}
