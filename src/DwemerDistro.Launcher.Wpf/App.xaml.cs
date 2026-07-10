using System.Windows;
using DwemerDistro.Launcher.Wpf.Services;
using Application = System.Windows.Application;
using MessageBox = System.Windows.MessageBox;
using DispatcherUnhandledExceptionEventArgs = System.Windows.Threading.DispatcherUnhandledExceptionEventArgs;

namespace DwemerDistro.Launcher.Wpf;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        LauncherLogService.Startup($"Launcher startup {LauncherConstants.LauncherVersion}.");
        DispatcherUnhandledException += App_DispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;
        TaskScheduler.UnobservedTaskException += TaskScheduler_UnobservedTaskException;

        var window = new MainWindow();
        MainWindow = window;
        window.Show();
    }

    private static void App_DispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        LauncherLogService.Startup("Unhandled dispatcher exception.", e.Exception);
        e.Handled = true;
        MessageBox.Show(
            "The launcher hit an unexpected error but stayed open.\n\n" +
            $"Details were written to:\n{LauncherLogService.StartupLogPath}",
            "DwemerDistro Launcher",
            MessageBoxButton.OK,
            MessageBoxImage.Warning);
    }

    private static void CurrentDomain_UnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        LauncherLogService.Startup("Unhandled app-domain exception.", e.ExceptionObject as Exception);
    }

    private static void TaskScheduler_UnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        LauncherLogService.Startup("Unobserved task exception.", e.Exception);
        e.SetObserved();
    }
}
