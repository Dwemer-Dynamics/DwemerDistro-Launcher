using System.Net;
using System.Net.Sockets;
using System.IO;

namespace DwemerDistro.Launcher.Wpf.Services;

public sealed class TcpProxyService
{
    private readonly Func<CancellationToken, Task<IPEndPoint?>> _targetResolver;
    private readonly Action<string> _log;
    private CancellationTokenSource? _cts;
    private TcpListener? _listener;
    private Task? _acceptLoopTask;

    public TcpProxyService(Func<CancellationToken, Task<IPEndPoint?>> targetResolver, Action<string> log)
    {
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
        _listener = new TcpListener(IPAddress.Loopback, LauncherConstants.SkyrimProxyPort);
        _listener.Start(16);
        _log($"TCP Proxy listening on 127.0.0.1:{LauncherConstants.SkyrimProxyPort}{Environment.NewLine}");
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
        _log($"TCP Proxy stopped.{Environment.NewLine}");
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
                    _log($"Proxy accept loop stopped unexpectedly.{Environment.NewLine}");
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
            _log($"Proxy: Could not get WSL IP.{Environment.NewLine}");
            return;
        }

        using var server = new TcpClient();
        try
        {
            await server.ConnectAsync(target, cancellationToken).ConfigureAwait(false);
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
                _log($"Proxy: Error connecting to target: {ex.Message}{Environment.NewLine}");
            }
        }
    }
}
