using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DwemerDistro.Launcher.Wpf.Services;

public sealed class LauncherReleaseNoticeService
{
    public static readonly Version DedicatedTtsPortsVersion = new(3, 1, 13);

    private const string DedicatedTtsPortsNoticeKey = "dedicated-tts-ports-3.1.13";
    private readonly string _updateLogPath;
    private readonly string _statePath;

    public LauncherReleaseNoticeService(
        string? installDirectory = null,
        string? localAppDataDirectory = null)
    {
        var resolvedInstallDirectory = installDirectory ?? AppContext.BaseDirectory;
        var resolvedLocalAppDataDirectory = localAppDataDirectory
                                            ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        _updateLogPath = Path.Combine(resolvedInstallDirectory, "Logs", "launcher-update.log");
        _statePath = Path.Combine(resolvedLocalAppDataDirectory, "DwemerDistro", "release-notices.json");
    }

    public LauncherReleaseNotice? GetPendingDedicatedTtsPortsNotice(Version currentVersion)
    {
        if (currentVersion < DedicatedTtsPortsVersion
            || IsAcknowledged(DedicatedTtsPortsNoticeKey)
            || !HasSuccessfulUpdaterReceipt(currentVersion))
        {
            return null;
        }

        return new LauncherReleaseNotice(
            DedicatedTtsPortsNoticeKey,
            "TTS Update",
            "If your TTS is working, you do not need to reinstall anything.\n\n" +
            "Only reinstall if Chatterbox or Python PocketTTS stopped connecting, or if you want to move an older installation to its new port:\n\n" +
            "1. Open Configure Installed Components and reinstall that TTS service.\n" +
            "2. Open TTS Studio and re-apply the connector.\n\n" +
            "XTTS and PocketTTS audio.cpp do not need to be reinstalled.");
    }

    public bool TryAcknowledge(LauncherReleaseNotice notice, out string? error)
    {
        try
        {
            var state = ReadState();
            state.Acknowledged.Add(notice.Key);

            var stateDirectory = Path.GetDirectoryName(_statePath)!;
            Directory.CreateDirectory(stateDirectory);
            var temporaryPath = Path.Combine(stateDirectory, $"release-notices.{Guid.NewGuid():N}.tmp");

            try
            {
                File.WriteAllText(temporaryPath, JsonSerializer.Serialize(state, JsonOptions));
                File.Move(temporaryPath, _statePath, overwrite: true);
            }
            finally
            {
                if (File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }
            }

            error = null;
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private bool HasSuccessfulUpdaterReceipt(Version currentVersion)
    {
        if (!File.Exists(_updateLogPath))
        {
            return false;
        }

        try
        {
            var normalizedLog = File.ReadAllText(_updateLogPath).Replace('\\', '/');
            var versionText = $"{currentVersion.Major}.{currentVersion.Minor}.{Math.Max(currentVersion.Build, 0)}";
            var packageMarker = $"/LauncherUpdates/{versionText}/";
            var packageIndex = normalizedLog.LastIndexOf(packageMarker, StringComparison.OrdinalIgnoreCase);
            if (packageIndex < 0)
            {
                return false;
            }

            var restartIndex = normalizedLog.LastIndexOf("Restarting launcher:", StringComparison.OrdinalIgnoreCase);
            return restartIndex > packageIndex;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private bool IsAcknowledged(string noticeKey)
    {
        return ReadState().Acknowledged.Contains(noticeKey);
    }

    private ReleaseNoticeState ReadState()
    {
        if (!File.Exists(_statePath))
        {
            return new ReleaseNoticeState();
        }

        try
        {
            return JsonSerializer.Deserialize<ReleaseNoticeState>(File.ReadAllText(_statePath), JsonOptions)
                   ?? new ReleaseNoticeState();
        }
        catch (JsonException)
        {
            return new ReleaseNoticeState();
        }
        catch (IOException)
        {
            return new ReleaseNoticeState();
        }
        catch (UnauthorizedAccessException)
        {
            return new ReleaseNoticeState();
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private sealed class ReleaseNoticeState
    {
        [JsonPropertyName("acknowledged")]
        public HashSet<string> Acknowledged { get; init; } = [];
    }
}

public sealed record LauncherReleaseNotice(string Key, string Title, string Message);
