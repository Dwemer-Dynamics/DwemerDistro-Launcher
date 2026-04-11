using System.Net;
using System.Net.Sockets;
using System.Text;

namespace DwemerDistro.Launcher.Wpf.Services;

public sealed class DiscoveryService
{
    private readonly Func<CancellationToken, Task<string?>> _wslIpResolver;
    private readonly Action<string> _log;
    private CancellationTokenSource? _cts;
    private TcpListener? _listener;
    private Task? _acceptLoopTask;

    public DiscoveryService(Func<CancellationToken, Task<string?>> wslIpResolver, Action<string> log)
    {
        _wslIpResolver = wslIpResolver;
        _log = log;
    }

    public void Start()
    {
        if (_acceptLoopTask is not null)
        {
            return;
        }

        _cts = new CancellationTokenSource();
        _listener = new TcpListener(IPAddress.Loopback, LauncherConstants.DiscoveryPort);
        _listener.Start(5);
        _log($"Discovery service listening on localhost:{LauncherConstants.DiscoveryPort}{Environment.NewLine}");
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
        _log($"Discovery service stopped.{Environment.NewLine}");
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
                    _log($"Discovery service accept loop stopped unexpectedly.{Environment.NewLine}");
                }
                break;
            }

            _ = Task.Run(() => HandleClientAsync(client, cancellationToken), cancellationToken);
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken cancellationToken)
    {
        using var clientScope = client;
        try
        {
            var buffer = new byte[1024];
            var stream = client.GetStream();
            var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            var request = Encoding.UTF8.GetString(buffer, 0, read);

            var response = request.Contains("GET /discover", StringComparison.OrdinalIgnoreCase)
                ? await BuildDiscoveryResponseAsync(request, cancellationToken).ConfigureAwait(false)
                : BuildHttpResponse("404 Not Found", "Not Found");

            var bytes = Encoding.UTF8.GetBytes(response);
            await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // Match Python behavior: discovery request failures are non-fatal.
        }
    }

    private async Task<string> BuildDiscoveryResponseAsync(string request, CancellationToken cancellationToken)
    {
        var targetPort = GetDiscoveryTargetPort(request);
        var wslIp = await _wslIpResolver(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(wslIp))
        {
            return BuildHttpResponse("503 Service Unavailable", "WSL IP not available");
        }

        return BuildHttpResponse("200 OK", $"{wslIp}:{targetPort}");
    }

    private static int GetDiscoveryTargetPort(string request)
    {
        var requestLine = request.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? string.Empty;
        var requestParts = requestLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (requestParts.Length < 2 || !Uri.TryCreate("http://localhost" + requestParts[1], UriKind.Absolute, out var uri))
        {
            return LauncherConstants.SkyrimServerPort;
        }

        var query = uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries);
        foreach (var part in query)
        {
            var pieces = part.Split('=', 2);
            if (pieces.Length == 2 && pieces[0].Equals("game", StringComparison.OrdinalIgnoreCase))
            {
                var value = Uri.UnescapeDataString(pieces[1]).Trim().ToLowerInvariant();
                if (value is "kenshi" or "stobe")
                {
                    return LauncherConstants.StobeServerPort;
                }
            }
        }

        return LauncherConstants.SkyrimServerPort;
    }

    private static string BuildHttpResponse(string status, string body)
    {
        return
            $"HTTP/1.1 {status}\r\n" +
            "Content-Type: text/plain\r\n" +
            $"Content-Length: {Encoding.UTF8.GetByteCount(body)}\r\n" +
            "Connection: close\r\n" +
            "\r\n" +
            body;
    }
}
