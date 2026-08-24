using DwemerDistro.Launcher.Wpf.Services;
using DwemerDistro.Launcher.Wpf.ViewModels;
using DwemerDistro.Launcher.Wpf.Models;

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

    Assert(InstallComponentsWindowViewModel.BuildMeloTtsProbeEntry() ==
            ": provider_status(Path('/home/dwemer/python-melotts/bin/python').exists(), Path('/home/dwemer/MeloTTS/start.sh').exists(), Path('/home/dwemer/MeloTTS'), 8084, 'melotts', 'MeloTTS'),",
        "The MeloTTS component probe must pass its base path before the dedicated port.");

    var onboardingStatePath = Path.Combine(root, "onboarding.json");
    var onboarding = new OnboardingStateService(onboardingStatePath);
    await onboarding.MarkSkippedAsync(SetupPresetKey.AmdCpu);

    var skipped = await onboarding.LoadAsync();
    Assert(skipped.Skipped, "Skip Quick Setup must persist the skipped state.");
    Assert(!skipped.Completed, "Skipping Quick Setup must not claim setup was completed.");
    Assert(skipped.SkippedAtUtc is not null, "Skipping Quick Setup must record when it was skipped.");
    Assert(skipped.SelectedPreset == SetupPresetKey.AmdCpu.ToString(),
        "Skipping Quick Setup must preserve the selected preset.");
    Assert(!await FirstRunSetupViewModel.ShouldShowFirstRunSetupAsync(default, onboarding),
        "A skipped setup must not reopen QuickStart.");

    await onboarding.MarkCompletedAsync(SetupPresetKey.NvidiaGpu, "pockettts", false, true);
    var completed = await onboarding.LoadAsync();
    Assert(completed.Completed, "Completed setup must persist the completed state.");
    Assert(!completed.Skipped, "Completing setup must clear the skipped state.");
    Assert(!await FirstRunSetupViewModel.ShouldShowFirstRunSetupAsync(default, onboarding),
        "A completed setup must not reopen QuickStart.");
    Assert(LauncherConstants.LauncherVersion == "3.2.4", "Launcher constants must report version 3.2.4.");

    var gameCatalog = GameProfile.CreateCatalog();
    Assert(gameCatalog.Count == 3 && gameCatalog.Select(game => game.Key).Distinct().Count() == 3,
        "The Game Center rail must expose exactly three unique game profiles.");
    Assert(gameCatalog.All(game => game.HeroImageSource.EndsWith("-hero.jpg", StringComparison.Ordinal)
                                   && game.RailImageSource.EndsWith("-rail.jpg", StringComparison.Ordinal)),
        "Every game profile must use local hero and rail artwork.");

    var keyedPreferences = MainWindowViewModel.ParseUpdateIncludeSettings(
        "herika=0\nstobe=\ndialectic=1\n");
    Assert(!keyedPreferences.Herika && keyedPreferences.Stobe && keyedPreferences.Dialectic,
        "An empty update preference must not shift the following game's value.");

    Console.WriteLine("Launcher regression tests: OK");
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
