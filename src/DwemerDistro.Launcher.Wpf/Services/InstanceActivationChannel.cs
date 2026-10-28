using System.Diagnostics;
using System.Runtime.InteropServices;

namespace DwemerDistro.Launcher.Wpf.Services;

/// <summary>
/// Lets a second start of the launcher from the same installation hand over to the running one instead of showing an
/// "already running" message. The channel is a named auto-reset event in the user's session namespace, scoped by the
/// same install hash as the instance mutex. It carries no data: all another process can do with it is ask the running
/// launcher to show its window.
/// </summary>
public sealed class InstanceActivationChannel : IDisposable
{
    private static readonly TimeSpan SignalPollInterval = TimeSpan.FromMilliseconds(100);
    private readonly EventWaitHandle _event;
    private readonly RegisteredWaitHandle _registration;

    private InstanceActivationChannel(EventWaitHandle activationEvent, Action onActivate)
    {
        _event = activationEvent;
        _registration = ThreadPool.RegisterWaitForSingleObject(activationEvent, (_, timedOut) =>
        {
            if (!timedOut) onActivate();
        }, null, Timeout.Infinite, executeOnlyOnce: false);
    }

    /// <summary>The activation event for one instance mutex name, in the same namespace.</summary>
    public static string BuildName(string instanceMutexName) => instanceMutexName + ".Activate";

    /// <summary>Called by the instance that owns the mutex. <paramref name="onActivate"/> runs on a thread-pool thread.</summary>
    public static InstanceActivationChannel Listen(string name, Action onActivate) =>
        new(new EventWaitHandle(false, EventResetMode.AutoReset, name), onActivate);

    /// <summary>
    /// Asks the running instance to show itself, waiting up to <paramref name="timeout"/> for one that is still starting.
    /// False when nothing listens, for example an older launcher, so the caller can say it is already running instead.
    /// <paramref name="beforeSignal"/> runs once, only when a listener was found, just before it is signalled.
    /// </summary>
    public static async Task<bool> TrySignalAsync(string name, TimeSpan timeout, Action? beforeSignal = null)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            if (EventWaitHandle.TryOpenExisting(name, out var activationEvent))
            {
                using (activationEvent)
                {
                    beforeSignal?.Invoke();
                    return activationEvent.Set();
                }
            }

            if (DateTime.UtcNow >= deadline) return false;
            await Task.Delay(SignalPollInterval).ConfigureAwait(false);
        }
    }

    public void Dispose()
    {
        _registration.Unregister(null);
        _event.Dispose();
    }

    /// <summary>
    /// Lets the running launcher take the foreground, which only a process the user just started may grant. The grant goes
    /// to exactly one process: another launcher in this session started from this same executable. When none, or more
    /// than one, matches, nothing is granted and Windows flashes the running launcher's taskbar button instead.
    /// </summary>
    public static void AllowForegroundToRunningInstance()
    {
        try
        {
            using var current = Process.GetCurrentProcess();
            var executable = Environment.ProcessPath;
            if (string.IsNullOrEmpty(executable)) return;
            var matches = new List<int>();
            foreach (var process in Process.GetProcessesByName(current.ProcessName))
            {
                using (process)
                {
                    try
                    {
                        if (process.Id != current.Id && process.SessionId == current.SessionId
                            && string.Equals(process.MainModule?.FileName, executable, StringComparison.OrdinalIgnoreCase))
                        {
                            matches.Add(process.Id);
                        }
                    }
                    catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
                    {
                        // Exited, or not ours to inspect: never granted.
                    }
                }
            }

            if (matches.Count == 1) AllowSetForegroundWindow(matches[0]);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            LauncherLogService.Startup("The running launcher could not be allowed to come to the front.");
        }
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AllowSetForegroundWindow(int processId);
}
