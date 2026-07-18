using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using DwemerDistro.Launcher.Wpf.Services;
using Application = System.Windows.Application;
using MessageBox = System.Windows.MessageBox;
using DispatcherUnhandledExceptionEventArgs = System.Windows.Threading.DispatcherUnhandledExceptionEventArgs;

namespace DwemerDistro.Launcher.Wpf;

public partial class App : Application
{
    private Mutex? _instanceMutex;
    private bool _ownsInstanceMutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        LauncherLogService.Startup($"Launcher startup {LauncherConstants.LauncherVersion}.");

        if (e.Args.Contains("--plugin-broker-once", StringComparer.OrdinalIgnoreCase))
        {
            _ = RunPluginBrokerOnceAsync();
            return;
        }

        var mutexName = BuildInstallScopedMutexName();
        _instanceMutex = new Mutex(initiallyOwned: true, mutexName, out _ownsInstanceMutex);
        if (!_ownsInstanceMutex)
        {
            LauncherLogService.Startup($"Launcher startup stopped because another instance owns '{mutexName}'.");
            MessageBox.Show(
                "DwemerDistro is already running from this installation.",
                "DwemerDistro Launcher",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            Shutdown();
            return;
        }

        DispatcherUnhandledException += App_DispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;
        TaskScheduler.UnobservedTaskException += TaskScheduler_UnobservedTaskException;

        var window = new MainWindow();
        MainWindow = window;
        window.Show();
    }

    private async Task RunPluginBrokerOnceAsync()
    {
        try
        {
            var processRunner = new ProcessRunner();
            var wsl = new WslService(processRunner);
            await using var broker = new PluginPackageBrokerService(wsl, message => LauncherLogService.Startup(message.TrimEnd()));
            var count = await broker.RunOnceAsync().ConfigureAwait(false);
            LauncherLogService.Startup($"Unified plugin broker one-shot completed with {count} job(s).");
            Dispatcher.Invoke(() => Shutdown(0));
        }
        catch (Exception error)
        {
            LauncherLogService.Startup("Unified plugin broker one-shot failed.", error);
            Dispatcher.Invoke(() => Shutdown(1));
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_ownsInstanceMutex)
        {
            _instanceMutex?.ReleaseMutex();
        }

        _instanceMutex?.Dispose();
        base.OnExit(e);
    }

    private static string BuildInstallScopedMutexName()
    {
        var installPath = Path.GetFullPath(AppContext.BaseDirectory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .ToUpperInvariant();
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(installPath)))[..16];
        return $"Local\\DwemerDistro.Launcher.{hash}";
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
