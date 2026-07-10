using System.Diagnostics;
using System.IO;
using System.Text;
using DwemerDistro.Launcher.Wpf.Models;

namespace DwemerDistro.Launcher.Wpf.Services;

public sealed class ProcessRunner
{
    private static readonly TimeSpan StandardInputCloseTimeout = TimeSpan.FromSeconds(2);

    public async Task<CommandResult> RunHiddenAsync(
        string fileName,
        IEnumerable<string> arguments,
        Action<string>? output = null,
        CancellationToken cancellationToken = default)
    {
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();

        using var process = CreateHiddenProcess(fileName, arguments, redirectInput: false);

        process.Start();

        var stdoutTask = ReadLinesAsync(process.StandardOutput, line =>
        {
            stdout.AppendLine(line);
            output?.Invoke(line + Environment.NewLine);
        }, cancellationToken);

        var stderrTask = ReadLinesAsync(process.StandardError, line =>
        {
            stderr.AppendLine(line);
            output?.Invoke(line + Environment.NewLine);
        }, cancellationToken);

        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            throw;
        }

        return new CommandResult(process.ExitCode, stdout.ToString(), stderr.ToString());
    }

    public async Task<CommandResult> RunHiddenWithEnvironmentAsync(
        string fileName,
        IEnumerable<string> arguments,
        IReadOnlyDictionary<string, string> environment,
        Action<string>? output = null,
        CancellationToken cancellationToken = default)
    {
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();

        using var process = CreateHiddenProcess(fileName, arguments, redirectInput: false, environment);

        process.Start();

        var stdoutTask = ReadLinesAsync(process.StandardOutput, line =>
        {
            stdout.AppendLine(line);
            output?.Invoke(line + Environment.NewLine);
        }, cancellationToken);

        var stderrTask = ReadLinesAsync(process.StandardError, line =>
        {
            stderr.AppendLine(line);
            output?.Invoke(line + Environment.NewLine);
        }, cancellationToken);

        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            throw;
        }

        return new CommandResult(process.ExitCode, stdout.ToString(), stderr.ToString());
    }

    public async Task<CommandResult> RunHiddenWithInputAsync(
        string fileName,
        IEnumerable<string> arguments,
        string input,
        Action<string>? output = null,
        CancellationToken cancellationToken = default)
    {
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();

        using var process = CreateHiddenProcess(fileName, arguments, redirectInput: true);

        process.Start();

        var stdoutTask = ReadLinesAsync(process.StandardOutput, line =>
        {
            stdout.AppendLine(line);
            output?.Invoke(line + Environment.NewLine);
        }, cancellationToken);

        var stderrTask = ReadLinesAsync(process.StandardError, line =>
        {
            stderr.AppendLine(line);
            output?.Invoke(line + Environment.NewLine);
        }, cancellationToken);

        try
        {
            await TryWriteStandardInputAsync(process, input, cancellationToken).ConfigureAwait(false);

            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            throw;
        }

        return new CommandResult(process.ExitCode, stdout.ToString(), stderr.ToString());
    }

    private static async Task TryWriteStandardInputAsync(
        Process process,
        string input,
        CancellationToken cancellationToken)
    {
        try
        {
            await process.StandardInput.WriteAsync(input.AsMemory(), cancellationToken).ConfigureAwait(false);
            await process.StandardInput.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (IOException)
        {
            // Some WSL commands close stdin before the launcher finishes writing preset input.
            // The process exit code is the authoritative success/failure signal.
        }
        catch (ObjectDisposedException)
        {
            // Treat a closed stdin pipe the same way as a broken pipe.
        }
        finally
        {
            try
            {
                await CloseStandardInputAsync(process, cancellationToken).ConfigureAwait(false);
            }
            catch (IOException)
            {
            }
            catch (InvalidOperationException)
            {
            }
        }
    }

    private static async Task CloseStandardInputAsync(Process process, CancellationToken cancellationToken)
    {
        var closeTask = Task.Run(() =>
        {
            try
            {
                process.StandardInput.Close();
            }
            catch (IOException)
            {
            }
            catch (ObjectDisposedException)
            {
            }
            catch (InvalidOperationException)
            {
            }
        }, CancellationToken.None);

        var delayTask = Task.Delay(StandardInputCloseTimeout, cancellationToken);
        var completedTask = await Task.WhenAny(closeTask, delayTask).ConfigureAwait(false);
        if (completedTask == closeTask)
        {
            await closeTask.ConfigureAwait(false);
        }
    }

    public async Task<CommandResult> RunElevatedAsync(
        string fileName,
        IEnumerable<string> arguments,
        CancellationToken cancellationToken = default)
    {
        var startInfo = new ProcessStartInfo(fileName)
        {
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException($"Failed to start {fileName}.");

        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            throw;
        }

        return new CommandResult(process.ExitCode, string.Empty, string.Empty);
    }

    public Process StartHiddenProcess(
        string fileName,
        IEnumerable<string> arguments,
        Action<string>? output = null,
        bool redirectInput = false)
    {
        var process = CreateHiddenProcess(fileName, arguments, redirectInput);
        process.Start();

        _ = Task.Run(() => ReadLinesAsync(process.StandardOutput, line => output?.Invoke(line + Environment.NewLine)));
        _ = Task.Run(() => ReadLinesAsync(process.StandardError, line => output?.Invoke(line + Environment.NewLine)));

        return process;
    }

    public void RunInNewConsole(string command)
    {
        var startInfo = new ProcessStartInfo("cmd.exe")
        {
            UseShellExecute = true,
            WindowStyle = ProcessWindowStyle.Normal
        };
        startInfo.ArgumentList.Add("/c");
        startInfo.ArgumentList.Add(command);
        Process.Start(startInfo);
    }

    public void OpenExternalUrl(string url)
    {
        Process.Start(new ProcessStartInfo(url)
        {
            UseShellExecute = true
        });
    }

    public void OpenFolder(string path)
    {
        var startInfo = new ProcessStartInfo("explorer.exe")
        {
            UseShellExecute = true
        };
        startInfo.ArgumentList.Add(path);
        Process.Start(startInfo);
    }

    public void StartDetached(string fileName, IEnumerable<string> arguments)
    {
        var startInfo = new ProcessStartInfo(fileName)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(fileName) ?? Environment.CurrentDirectory
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        Process.Start(startInfo);
    }

    public void TryKill(Process? process)
    {
        if (process is null)
        {
            return;
        }

        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // Best-effort cleanup. The caller logs the user-facing action.
        }
    }

    private static Process CreateHiddenProcess(
        string fileName,
        IEnumerable<string> arguments,
        bool redirectInput,
        IReadOnlyDictionary<string, string>? environment = null)
    {
        var startInfo = new ProcessStartInfo(fileName)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = redirectInput,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        if (environment is not null)
        {
            foreach (var (key, value) in environment)
            {
                startInfo.Environment[key] = value;
            }
        }

        return new Process
        {
            StartInfo = startInfo,
            EnableRaisingEvents = true
        };
    }

    private static async Task ReadLinesAsync(
        StreamReader reader,
        Action<string> onLine,
        CancellationToken cancellationToken = default)
    {
        while (!reader.EndOfStream && !cancellationToken.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (line is not null)
            {
                onLine(line.Replace("\0", string.Empty));
            }
        }
    }
}
