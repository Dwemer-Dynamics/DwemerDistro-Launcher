using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace DwemerDistro.Launcher.Wpf.Services;

internal static class BrowserDownloadService
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromMinutes(2);

    // Exposes one completed report through an unguessable, loopback-only URL, then shuts down.
    public static async Task DownloadOnceAsync(string filePath)
    {
        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException("The diagnostic report could not be found.", filePath);
        }

        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start(1);

        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var url = $"http://127.0.0.1:{port}/{token}";
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });

            using var timeout = new CancellationTokenSource(RequestTimeout);
            using var client = await listener.AcceptTcpClientAsync(timeout.Token).ConfigureAwait(false);
            await SendFileResponseAsync(client, token, filePath, timeout.Token).ConfigureAwait(false);
        }
        finally
        {
            listener.Stop();
        }
    }

    private static async Task SendFileResponseAsync(
        TcpClient client,
        string token,
        string filePath,
        CancellationToken cancellationToken)
    {
        var stream = client.GetStream();
        var requestBuffer = new byte[8192];
        var bytesRead = await stream.ReadAsync(requestBuffer, cancellationToken).ConfigureAwait(false);
        var request = Encoding.ASCII.GetString(requestBuffer, 0, bytesRead);
        var expectedRequestTarget = $"/{token}";
        var validRequest = request.StartsWith($"GET {expectedRequestTarget} HTTP/1.", StringComparison.Ordinal);

        if (!validRequest)
        {
            await WriteTextResponseAsync(stream, "404 Not Found", "Not Found", cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        await using var file = new FileStream(
            filePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 81920,
            useAsync: true);
        var fileName = Path.GetFileName(filePath).Replace("\"", string.Empty, StringComparison.Ordinal);
        var headers =
            "HTTP/1.1 200 OK\r\n" +
            "Content-Type: text/plain; charset=utf-8\r\n" +
            $"Content-Disposition: attachment; filename=\"{fileName}\"\r\n" +
            $"Content-Length: {file.Length}\r\n" +
            "Cache-Control: no-store\r\n" +
            "X-Content-Type-Options: nosniff\r\n" +
            "Connection: close\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(headers), cancellationToken).ConfigureAwait(false);
        await file.CopyToAsync(stream, cancellationToken).ConfigureAwait(false);
    }

    private static async Task WriteTextResponseAsync(
        NetworkStream stream,
        string status,
        string body,
        CancellationToken cancellationToken)
    {
        var bodyBytes = Encoding.UTF8.GetBytes(body);
        var headers =
            $"HTTP/1.1 {status}\r\n" +
            "Content-Type: text/plain; charset=utf-8\r\n" +
            $"Content-Length: {bodyBytes.Length}\r\n" +
            "Cache-Control: no-store\r\n" +
            "Connection: close\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(headers), cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(bodyBytes, cancellationToken).ConfigureAwait(false);
    }
}
