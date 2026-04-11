using System.Net.Http;
using System.Net.Http.Json;
using System.IO;
using System.Reflection;
using System.Text.Json.Serialization;
using DwemerDistro.Launcher.Wpf.Models;

namespace DwemerDistro.Launcher.Wpf.Services;

public sealed class LauncherUpdateService
{
    private readonly HttpClient _httpClient;
    private readonly ProcessRunner _processRunner;

    public LauncherUpdateService(HttpClient httpClient, ProcessRunner processRunner)
    {
        _httpClient = httpClient;
        _processRunner = processRunner;

        if (!_httpClient.DefaultRequestHeaders.UserAgent.Any())
        {
            _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd($"DwemerDistroLauncher/{LauncherConstants.LauncherVersion}");
        }
    }

    public Version GetCurrentVersion()
    {
        return Assembly.GetEntryAssembly()?.GetName().Version
            ?? Assembly.GetExecutingAssembly().GetName().Version
            ?? Version.Parse(LauncherConstants.LauncherVersion);
    }

    public async Task<LauncherReleaseInfo?> CheckForUpdatesAsync(CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.GetAsync(
            LauncherConstants.LauncherLatestReleaseApiUrl,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);

        response.EnsureSuccessStatusCode();

        var release = await response.Content.ReadFromJsonAsync<GithubReleaseResponse>(cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        if (release is null || release.Draft || release.Prerelease)
        {
            return null;
        }

        var version = ParseVersion(release.TagName);
        if (version is null || version <= GetCurrentVersion())
        {
            return null;
        }

        var asset = release.Assets.FirstOrDefault(a =>
            string.Equals(a.Name, LauncherConstants.LauncherPackageAssetName, StringComparison.OrdinalIgnoreCase));
        if (asset is null || string.IsNullOrWhiteSpace(asset.BrowserDownloadUrl))
        {
            return null;
        }

        return new LauncherReleaseInfo
        {
            Version = version,
            TagName = release.TagName,
            PackageUrl = asset.BrowserDownloadUrl,
            PackageName = asset.Name,
            ReleasePageUrl = release.HtmlUrl ?? string.Empty
        };
    }

    public async Task<string> DownloadUpdatePackageAsync(
        LauncherReleaseInfo release,
        Action<int>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var updateDir = Path.Combine(Path.GetTempPath(), "DwemerDistro", "LauncherUpdates", release.Version.ToString());
        Directory.CreateDirectory(updateDir);

        var packagePath = Path.Combine(updateDir, release.PackageName);
        using var response = await _httpClient.GetAsync(
            release.PackageUrl,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var totalBytes = response.Content.Headers.ContentLength;
        await using var contentStream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using var fileStream = new FileStream(packagePath, FileMode.Create, FileAccess.Write, FileShare.None);

        var buffer = new byte[81920];
        long totalRead = 0;
        int read;
        while ((read = await contentStream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            await fileStream.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            totalRead += read;

            if (totalBytes.HasValue && totalBytes.Value > 0)
            {
                var percent = (int)Math.Clamp(totalRead * 100L / totalBytes.Value, 0L, 100L);
                progress?.Invoke(percent);
            }
        }

        progress?.Invoke(100);
        return packagePath;
    }

    public void StartUpdaterAndExit(string packagePath)
    {
        var installDirectory = AppContext.BaseDirectory;
        EnsureDirectoryWritable(installDirectory);

        var installedUpdaterPath = Path.Combine(installDirectory, LauncherConstants.LauncherUpdaterExeName);
        if (!File.Exists(installedUpdaterPath))
        {
            throw new FileNotFoundException("DwemerDistroUpdater.exe was not found beside the launcher.", installedUpdaterPath);
        }

        var tempUpdaterDirectory = Path.Combine(Path.GetTempPath(), "DwemerDistro", "Updater");
        if (Directory.Exists(tempUpdaterDirectory))
        {
            Directory.Delete(tempUpdaterDirectory, recursive: true);
        }
        Directory.CreateDirectory(tempUpdaterDirectory);

        var tempUpdaterPath = Path.Combine(tempUpdaterDirectory, LauncherConstants.LauncherUpdaterExeName);
        File.Copy(installedUpdaterPath, tempUpdaterPath, overwrite: true);

        _processRunner.StartDetached(tempUpdaterPath, new[]
        {
            "--pid", Environment.ProcessId.ToString(),
            "--install-dir", installDirectory,
            "--package", packagePath,
            "--entry-exe", Path.Combine(installDirectory, LauncherConstants.LauncherExeName),
            "--log", Path.Combine(installDirectory, "Logs", "launcher-update.log")
        });
    }

    private static void EnsureDirectoryWritable(string installDirectory)
    {
        var probePath = Path.Combine(installDirectory, ".write-test.tmp");
        try
        {
            Directory.CreateDirectory(installDirectory);
            File.WriteAllText(probePath, "ok");
            File.Delete(probePath);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"Launcher install folder is not writable: {installDirectory}. Install the launcher outside Program Files for self-updates.",
                ex);
        }
    }

    private static Version? ParseVersion(string? tagName)
    {
        if (string.IsNullOrWhiteSpace(tagName))
        {
            return null;
        }

        var normalized = tagName.Trim();
        if (normalized.StartsWith("v", StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized[1..];
        }

        return Version.TryParse(normalized, out var version) ? version : null;
    }

    private sealed class GithubReleaseResponse
    {
        [JsonPropertyName("tag_name")]
        public string TagName { get; init; } = string.Empty;

        [JsonPropertyName("html_url")]
        public string? HtmlUrl { get; init; }

        [JsonPropertyName("draft")]
        public bool Draft { get; init; }

        [JsonPropertyName("prerelease")]
        public bool Prerelease { get; init; }

        [JsonPropertyName("assets")]
        public List<GithubAssetResponse> Assets { get; init; } = [];
    }

    private sealed class GithubAssetResponse
    {
        [JsonPropertyName("name")]
        public string Name { get; init; } = string.Empty;

        [JsonPropertyName("browser_download_url")]
        public string BrowserDownloadUrl { get; init; } = string.Empty;
    }
}
