using DwemerDistro.Launcher.Wpf.Services;

var root = Path.Combine(Path.GetTempPath(), "DwemerDistro", "ReleaseNoticeTests", Guid.NewGuid().ToString("N"));
var installDirectory = Path.Combine(root, "install");
var localAppDataDirectory = Path.Combine(root, "local-app-data");
var logDirectory = Path.Combine(installDirectory, "Logs");
var updateLogPath = Path.Combine(logDirectory, "launcher-update.log");

Directory.CreateDirectory(logDirectory);

try
{
    var service = new LauncherReleaseNoticeService(installDirectory, localAppDataDirectory);
    var targetVersion = new Version(3, 1, 13);

    Assert(service.GetPendingDedicatedTtsPortsNotice(targetVersion) is null,
        "A fresh install without an updater receipt must not show the notice.");

    File.WriteAllText(updateLogPath,
        "[test] Extracting package: C:\\Temp\\DwemerDistro\\LauncherUpdates\\3.1.12\\DwemerDistro-win-x64.zip\n" +
        "[test] Restarting launcher: C:\\DwemerDistro\\DwemerDistro.exe\n");
    Assert(service.GetPendingDedicatedTtsPortsNotice(targetVersion) is null,
        "A receipt for a different launcher version must not show the notice.");

    File.WriteAllText(updateLogPath,
        "[test] Extracting package: C:\\Temp\\DwemerDistro\\LauncherUpdates\\3.1.13\\DwemerDistro-win-x64.zip\n");
    Assert(service.GetPendingDedicatedTtsPortsNotice(targetVersion) is null,
        "An incomplete updater receipt must not show the notice.");

    File.AppendAllText(updateLogPath,
        "[test] Applying files into: C:\\DwemerDistro\n" +
        "[test] Restarting launcher: C:\\DwemerDistro\\DwemerDistro.exe\n");
    var notice = service.GetPendingDedicatedTtsPortsNotice(targetVersion);
    Assert(notice is not null,
        "A completed update into 3.1.13 must show the notice for an existing setup.");
    Assert(notice!.Message.Contains("do not need to reinstall", StringComparison.OrdinalIgnoreCase),
        "The notice must state that reinstalling TTS is unnecessary.");
    Assert(notice.Message.Contains("If your TTS is working, you do not need to reinstall anything", StringComparison.OrdinalIgnoreCase),
        "The notice must lead with the no-action path.");
    Assert(notice.Message.Contains("Configure Installed Components", StringComparison.Ordinal),
        "The notice must explain where to reinstall an affected service.");
    Assert(notice.Message.Contains("TTS Studio", StringComparison.Ordinal),
        "The notice must explain where to re-apply the connector.");
    Assert(notice.Message.Contains("Chatterbox", StringComparison.Ordinal)
           && notice.Message.Contains("Python PocketTTS", StringComparison.Ordinal),
        "The notice must identify the services that may need reinstalling.");
    Assert(notice.Message.Contains("XTTS and PocketTTS audio.cpp do not need to be reinstalled", StringComparison.Ordinal),
        "The notice must exclude unaffected services from reinstall instructions.");

    Assert(service.TryAcknowledge(notice, out var error),
        "Acknowledging the notice failed: " + error);
    Assert(service.GetPendingDedicatedTtsPortsNotice(targetVersion) is null,
        "An acknowledged notice must not appear again.");

    var firstRunStateDirectory = Path.Combine(root, "first-run-state");
    var firstRunService = new LauncherReleaseNoticeService(installDirectory, firstRunStateDirectory);
    var firstRunNotice = firstRunService.GetPendingDedicatedTtsPortsNotice(targetVersion);
    Assert(firstRunNotice is not null,
        "The test precondition requires a pending update notice.");
    Assert(firstRunService.TryAcknowledge(firstRunNotice!, out error),
        "First-run suppression could not acknowledge the notice: " + error);
    Assert(firstRunService.GetPendingDedicatedTtsPortsNotice(targetVersion) is null,
        "First-run suppression must prevent the notice on later launches.");

    Console.WriteLine("Launcher release notice regression tests: OK");
    return 0;
}
finally
{
    if (Directory.Exists(root))
    {
        Directory.Delete(root, recursive: true);
    }
}

static void Assert(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}
