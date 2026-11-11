using System.Diagnostics;

namespace DwemerDistro.Launcher.Wpf.Services;

/// <summary>
/// A cancellation deadline whose clock can be stopped, so a budget for doing work is not spent while that
/// work idles on the user, such as a game left open. Resuming restarts the clock with what was left; a
/// deadline that already expired stays cancelled. Pause and Resume are idempotent and safe from any thread.
/// </summary>
internal sealed class PausableDeadline : IDisposable
{
    private readonly object _gate = new();
    private readonly CancellationTokenSource _cts;
    private TimeSpan _remaining;
    private long _startedAt;
    private bool _paused;
    private bool _disposed;

    public PausableDeadline(TimeSpan timeout)
    {
        _remaining = timeout;
        _startedAt = Stopwatch.GetTimestamp();
        _cts = new CancellationTokenSource(timeout);
    }

    public CancellationToken Token => _cts.Token;

    public void Pause()
    {
        lock (_gate)
        {
            if (_paused || _disposed || _cts.IsCancellationRequested) return;
            _paused = true;
            _remaining -= Stopwatch.GetElapsedTime(_startedAt);
            _cts.CancelAfter(Timeout.InfiniteTimeSpan);
        }
    }

    public void Resume()
    {
        lock (_gate)
        {
            if (!_paused || _disposed) return;
            _paused = false;
            _startedAt = Stopwatch.GetTimestamp();
            _cts.CancelAfter(_remaining > TimeSpan.Zero ? _remaining : TimeSpan.Zero);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _cts.Dispose();
        }
    }
}
