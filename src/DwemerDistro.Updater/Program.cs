using System.Diagnostics;
using System.IO.Compression;
using System.Windows.Forms;

namespace DwemerDistro.Updater;

internal static class Program
{
    [STAThread]
    public static async Task<int> Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        return await RunAsync(args).ConfigureAwait(false);
    }

    private static async Task<int> RunAsync(string[] args)
    {
        var options = ParseArgs(args);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(options.LogPath)!);
            Log(options.LogPath, "Updater starting.");

            await WaitForProcessExitAsync(options.ProcessId, options.LogPath).ConfigureAwait(false);

            var stagingDirectory = Path.Combine(Path.GetTempPath(), "DwemerDistro", "UpdateStaging", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(stagingDirectory);

            Log(options.LogPath, $"Extracting package: {options.PackagePath}");
            ZipFile.ExtractToDirectory(options.PackagePath, stagingDirectory, overwriteFiles: true);

            Log(options.LogPath, $"Applying files into: {options.InstallDirectory}");
            CopyDirectory(stagingDirectory, options.InstallDirectory, options.LogPath);

            TryDeleteFile(options.PackagePath, options.LogPath);
            TryDeleteDirectory(stagingDirectory, options.LogPath);

            Log(options.LogPath, $"Restarting launcher: {options.EntryExePath}");
            Process.Start(new ProcessStartInfo(options.EntryExePath)
            {
                UseShellExecute = true,
                WorkingDirectory = options.InstallDirectory
            });

            return 0;
        }
        catch (Exception ex)
        {
            Log(options.LogPath, $"Updater failed: {ex}");
            MessageBox.Show(
                "DwemerDistro launcher update failed.\n\n" + ex.Message,
                "DwemerDistro Update Error",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
            return 1;
        }
    }

    private static async Task WaitForProcessExitAsync(int processId, string logPath)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            Log(logPath, $"Waiting for process {processId} to exit.");
            await process.WaitForExitAsync().ConfigureAwait(false);
        }
        catch (ArgumentException)
        {
            Log(logPath, $"Process {processId} is already closed.");
        }
    }

    private static void CopyDirectory(string sourceDirectory, string destinationDirectory, string logPath)
    {
        foreach (var directory in Directory.GetDirectories(sourceDirectory, "*", SearchOption.AllDirectories))
        {
            var relativePath = Path.GetRelativePath(sourceDirectory, directory);
            Directory.CreateDirectory(Path.Combine(destinationDirectory, relativePath));
        }

        foreach (var sourceFile in Directory.GetFiles(sourceDirectory, "*", SearchOption.AllDirectories))
        {
            var relativePath = Path.GetRelativePath(sourceDirectory, sourceFile);
            var destinationFile = Path.Combine(destinationDirectory, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(destinationFile)!);

            const int maxAttempts = 10;
            for (var attempt = 1; attempt <= maxAttempts; attempt++)
            {
                try
                {
                    File.Copy(sourceFile, destinationFile, overwrite: true);
                    break;
                }
                catch (IOException) when (attempt < maxAttempts)
                {
                    Log(logPath, $"Retrying file copy for {relativePath} (attempt {attempt}/{maxAttempts}).");
                    Thread.Sleep(500);
                }
            }
        }
    }

    private static void TryDeleteFile(string path, string logPath)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex)
        {
            Log(logPath, $"Failed to delete file '{path}': {ex.Message}");
        }
    }

    private static void TryDeleteDirectory(string path, string logPath)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception ex)
        {
            Log(logPath, $"Failed to delete directory '{path}': {ex.Message}");
        }
    }

    private static void Log(string logPath, string message)
    {
        File.AppendAllText(logPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}{Environment.NewLine}");
    }

    private static UpdaterOptions ParseArgs(IReadOnlyList<string> args)
    {
        string? GetValue(string key)
        {
            for (var i = 0; i < args.Count - 1; i++)
            {
                if (string.Equals(args[i], key, StringComparison.OrdinalIgnoreCase))
                {
                    return args[i + 1];
                }
            }

            return null;
        }

        var pidText = GetValue("--pid");
        var installDir = GetValue("--install-dir");
        var packagePath = GetValue("--package");
        var entryExe = GetValue("--entry-exe");
        var logPath = GetValue("--log");

        if (!int.TryParse(pidText, out var processId) ||
            string.IsNullOrWhiteSpace(installDir) ||
            string.IsNullOrWhiteSpace(packagePath) ||
            string.IsNullOrWhiteSpace(entryExe) ||
            string.IsNullOrWhiteSpace(logPath))
        {
            throw new InvalidOperationException("Updater received invalid arguments.");
        }

        return new UpdaterOptions(processId, installDir, packagePath, entryExe, logPath);
    }

    private sealed record UpdaterOptions(
        int ProcessId,
        string InstallDirectory,
        string PackagePath,
        string EntryExePath,
        string LogPath);
}
