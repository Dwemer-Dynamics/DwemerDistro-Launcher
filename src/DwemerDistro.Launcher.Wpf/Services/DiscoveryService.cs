using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace DwemerDistro.Launcher.Wpf.Services;

public sealed class DiscoveryService
{
    private readonly Func<CancellationToken, Task<string?>> _wslIpResolver;
    private readonly Func<string, CancellationToken, Task<string?>>? _diagnosticReportGenerator;
    private readonly Action<string> _log;
    private readonly SemaphoreSlim _diagnosticDownloadGate = new(1, 1);
    private CancellationTokenSource? _cts;
    private TcpListener? _listener;
    private Task? _acceptLoopTask;

    public DiscoveryService(
        Func<CancellationToken, Task<string?>> wslIpResolver,
        Action<string> log,
        Func<string, CancellationToken, Task<string?>>? diagnosticReportGenerator = null)
    {
        _wslIpResolver = wslIpResolver;
        _log = log;
        _diagnosticReportGenerator = diagnosticReportGenerator;
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

            if (IsDiagnosticDownloadRequest(request))
            {
                await SendDiagnosticDownloadAsync(stream, cancellationToken).ConfigureAwait(false);
                return;
            }

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

    internal static bool IsDiagnosticDownloadRequest(string request)
    {
        return request.StartsWith("GET /download-diagnostics HTTP/1.", StringComparison.OrdinalIgnoreCase);
    }

    private async Task SendDiagnosticDownloadAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        if (_diagnosticReportGenerator is null)
        {
            await WriteHttpResponseAsync(
                stream,
                BuildHttpResponse("503 Service Unavailable", "Diagnostic download is unavailable."),
                cancellationToken).ConfigureAwait(false);
            return;
        }

        if (!await _diagnosticDownloadGate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            await WriteHttpResponseAsync(
                stream,
                BuildHttpResponse("409 Conflict", "A diagnostic report is already being generated."),
                cancellationToken).ConfigureAwait(false);
            return;
        }

        var temporaryPath = DiagnosticReportPaths.CreateTemporaryPath("diagnostics");
        try
        {
            var outputPath = await _diagnosticReportGenerator(temporaryPath, cancellationToken)
                .ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(outputPath) || !File.Exists(outputPath))
            {
                await WriteHttpResponseAsync(
                    stream,
                    BuildHttpResponse("500 Internal Server Error", "Diagnostic generation failed."),
                    cancellationToken).ConfigureAwait(false);
                return;
            }

            await BrowserDownloadService.WriteAttachmentAsync(stream, outputPath, cancellationToken)
                .ConfigureAwait(false);
            _log($"Diagnostic file sent to the browser download manager.{Environment.NewLine}");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LauncherLogService.Startup("Browser diagnostic download failed.", ex);
            _log($"Browser diagnostic download failed: {ex.Message}{Environment.NewLine}");
            try
            {
                await WriteHttpResponseAsync(
                    stream,
                    BuildHttpResponse("500 Internal Server Error", "Diagnostic generation failed."),
                    cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                // The connection may already be closed if transfer failed after response headers.
            }
        }
        finally
        {
            try
            {
                File.Delete(temporaryPath);
            }
            catch (Exception ex)
            {
                LauncherLogService.Startup("Temporary browser diagnostic cleanup failed.", ex);
            }
            finally
            {
                _diagnosticDownloadGate.Release();
            }
        }
    }

    private async Task<string> BuildDiscoveryResponseAsync(string request, CancellationToken cancellationToken)
    {
        var loopbackTarget = GetLoopbackDiscoveryTarget(request);
        if (loopbackTarget is not null)
        {
            return BuildHttpResponse("200 OK", loopbackTarget);
        }

        var targetPort = GetDiscoveryTargetPort(request);
        var wslIp = await _wslIpResolver(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(wslIp))
        {
            return BuildHttpResponse("503 Service Unavailable", "WSL IP not available");
        }

        return BuildHttpResponse("200 OK", $"{wslIp}:{targetPort}");
    }

    internal static string? GetLoopbackDiscoveryTarget(string request)
    {
        return GetGame(request) is "lorkhan" or "morrowind" or "openmw"
            ? $"127.0.0.1:{LauncherConstants.LorkhanProxyPort}"
            : null;
    }

    private static int GetDiscoveryTargetPort(string request)
    {
        var game = GetGame(request);
        if (game is "kenshi" or "stobe")
        {
            return LauncherConstants.StobeServerPort;
        }

        if (game is "starfield")
        {
            return LauncherConstants.StarfieldServerPort;
        }

        if (game is "dialectic" or "fallout" or "fnv" or "newvegas")
        {
            return LauncherConstants.DialecticServerPort;
        }

        if (game is "reign")
        {
            return LauncherConstants.ReignServerPort;
        }

        return LauncherConstants.SkyrimServerPort;
    }

    private static string? GetGame(string request)
    {
        var requestLine = request.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? string.Empty;
        var requestParts = requestLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (requestParts.Length < 2 || !Uri.TryCreate("http://localhost" + requestParts[1], UriKind.Absolute, out var uri))
        {
            return null;
        }

        var query = uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries);
        foreach (var part in query)
        {
            var pieces = part.Split('=', 2);
            if (pieces.Length == 2 && pieces[0].Equals("game", StringComparison.OrdinalIgnoreCase))
            {
                return Uri.UnescapeDataString(pieces[1]).Trim().ToLowerInvariant();
            }
        }

        return null;
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

    private static async Task WriteHttpResponseAsync(
        NetworkStream stream,
        string response,
        CancellationToken cancellationToken)
    {
        var bytes = Encoding.UTF8.GetBytes(response);
        await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
    }
}
