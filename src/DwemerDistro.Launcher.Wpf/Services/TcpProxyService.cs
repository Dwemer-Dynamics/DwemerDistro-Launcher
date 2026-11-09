using System.Net;
using System.Net.Sockets;
using System.IO;

namespace DwemerDistro.Launcher.Wpf.Services;

public sealed class TcpProxyService
{
    private readonly Func<CancellationToken, Task<IPEndPoint?>> _targetResolver;
    private readonly Action<string> _log;
    private readonly int _listenPort;
    private readonly string _name;
    private CancellationTokenSource? _cts;
    private TcpListener? _listener;
    private Task? _acceptLoopTask;
    private readonly object _failureLock = new();
    private string? _lastFailure;
    private DateTime _lastFailureShownUtc;
    private int _suppressedFailures;

    // Repeated identical connection failures are counted, not reprinted, for this window.
    internal static readonly TimeSpan FailureRepeatWindow = TimeSpan.FromSeconds(60);

    public TcpProxyService(int listenPort, string name,
        Func<CancellationToken, Task<IPEndPoint?>> targetResolver, Action<string> log)
    {
        _listenPort = listenPort;
        _name = name;
        _targetResolver = targetResolver;
        _log = log;
    }

    public void Start()
    {
        if (_acceptLoopTask is not null)
        {
            return;
        }

        _cts = new CancellationTokenSource();
        _listener = new TcpListener(IPAddress.Loopback, _listenPort);
        _listener.Start(16);
        LauncherLogService.Startup($"{_name} proxy listening on 127.0.0.1:{_listenPort}");
        _acceptLoopTask = Task.Run(() => AcceptLoopAsync(_cts.Token));
    }

    public async Task StopAsync()
    {
        if (_cts is null)
        {
            return;
        }

        _cts.Cancel();
        _listener?.Stop();

        if (_acceptLoopTask is not null)
        {
            try
            {
                await _acceptLoopTask.ConfigureAwait(false);
            }
            catch
            {
                // Stop can interrupt AcceptTcpClientAsync.
            }
        }

        _acceptLoopTask = null;
        _listener = null;
        _cts.Dispose();
        _cts = null;
        LauncherLogService.Startup($"{_name} proxy stopped.");
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        if (_listener is null)
        {
            return;
        }

        while (!cancellationToken.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
                if (!cancellationToken.IsCancellationRequested)
                {
                    _log($"{_name} proxy accept loop stopped unexpectedly.{Environment.NewLine}");
                }
                break;
            }

            _ = Task.Run(() => HandleClientAsync(client, cancellationToken), cancellationToken);
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken cancellationToken)
    {
        using var clientScope = client;
        var target = await _targetResolver(cancellationToken).ConfigureAwait(false);
        if (target is null)
        {
            LogFailure("could not get the DwemerDistro address");
            return;
        }

        using var server = new TcpClient();
        try
        {
            await server.ConnectAsync(target, cancellationToken).ConfigureAwait(false);
            ResetFailures();
            await using var clientStream = client.GetStream();
            await using var serverStream = server.GetStream();

            var clientToServer = clientStream.CopyToAsync(serverStream, cancellationToken);
            var serverToClient = serverStream.CopyToAsync(clientStream, cancellationToken);
            await Task.WhenAny(clientToServer, serverToClient).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is SocketException or IOException or OperationCanceledException)
        {
            if (!cancellationToken.IsCancellationRequested)
            {
                LogFailure($"connection to DwemerDistro failed: {ex.Message}");
            }
        }
    }

    private void LogFailure(string reason)
    {
        var message = FormatFailure(reason, DateTime.UtcNow);
        if (message is not null)
        {
            _log(message + Environment.NewLine);
        }
    }

    // Returns the console line for a failure, or null while an identical failure is being throttled.
    internal string? FormatFailure(string reason, DateTime nowUtc)
    {
        lock (_failureLock)
        {
            if (string.Equals(reason, _lastFailure, StringComparison.Ordinal)
                && nowUtc - _lastFailureShownUtc < FailureRepeatWindow)
            {
                _suppressedFailures++;
                return null;
            }

            var repeated = string.Equals(reason, _lastFailure, StringComparison.Ordinal) && _suppressedFailures > 0
                ? $" (repeated {_suppressedFailures + 1} times)"
                : "";
            _lastFailure = reason;
            _lastFailureShownUtc = nowUtc;
            _suppressedFailures = 0;
            return $"Warning: {_name} proxy {reason}{repeated}";
        }
    }

    private void ResetFailures()
    {
        lock (_failureLock)
        {
            _lastFailure = null;
            _suppressedFailures = 0;
        }
    }
}
