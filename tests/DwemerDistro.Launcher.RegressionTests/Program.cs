using DwemerDistro.Launcher.Wpf.Services;
using DwemerDistro.Launcher.Wpf.ViewModels;
using DwemerDistro.Launcher.Wpf.Models;
using System.Net;

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
    Assert(LauncherConstants.LauncherVersion == "3.3.5", "Launcher constants must report version 3.3.5.");

    var gameCatalog = GameProfile.CreateCatalog();
    Assert(gameCatalog.Count == 3 && gameCatalog.Select(game => game.Key).Distinct().Count() == 3,
        "The launcher rail must expose exactly three unique game profiles.");
    Assert(gameCatalog.All(game => game.HeroImageSource.EndsWith("-hero.jpg", StringComparison.Ordinal)
                                   && game.RailImageSource.EndsWith("-rail.jpg", StringComparison.Ordinal)),
        "Every game profile must use local hero and rail artwork.");

    Assert(MainWindowViewModel.ResolveServerBranchChoice("Main", "aiagent") == "aiagent"
           && MainWindowViewModel.ResolveServerBranchChoice("Main", "stobe") == "stobe"
           && MainWindowViewModel.ResolveServerBranchChoice("Main", "dialectic") == "dialectic",
        "The Main branch choice must resolve to each server's production branch.");
    Assert(MainWindowViewModel.ResolveServerBranchChoice("Dev", "aiagent") == "dev",
        "The Dev branch choice must resolve to dev.");
    Assert(MainWindowViewModel.MapServerBranchToChoice("stobe", "stobe") == "Main"
           && MainWindowViewModel.MapServerBranchToChoice("dev", "stobe") == "Dev"
           && MainWindowViewModel.MapServerBranchToChoice("unstable", "stobe") == "Dev",
        "Existing production and development branches must map back to visible choices.");

    // --- mod version status line ------------------------------------------------------------

    Assert(MainWindowViewModel.BuildServerVersionStatusText("herika", "aiagent", "01-01-2026", "1.2.3")
               == "aiagent | 01-01-2026 | 1.2.3",
        "A mod with no confirmed update must keep the plain branch | date | semantic version line.");
    Assert(MainWindowViewModel.BuildServerVersionStatusText("herika", "aiagent", "01-01-2026", "1.2.3", true)
               == "aiagent | 01-01-2026 | 1.2.3 | Update Available",
        "A confirmed update must append Update Available after the version info on the existing separator.");
    Assert(MainWindowViewModel.BuildServerVersionStatusText("dialectic", null, null, null, false)
               == "dialectic | N/A | N/A",
        "An unknown version must not claim an update: the same yellow also means missing or unknown.");
    Assert(MainWindowViewModel.UpdateAvailableStatusSuffix == "Update Available",
        "Both the mod control menu and the 96px rail tile must show the exact text Update Available.");
    Assert(MainWindowViewModel.BuildServerVersionStatusText("stobe", "stobe", "01-01-2026", "1.2.3", true).Length <= 48,
        "The status line must stay short enough for the fixed status area and the 96px rail tiles.");

    Assert(InstallComponentsWindowViewModel.ShouldUnloadComponentsPage(false, false),
        "Leaving Components with nothing running must release the page so its visual tree can be collected.");
    Assert(!InstallComponentsWindowViewModel.ShouldUnloadComponentsPage(true, false),
        "The Components page must stay mounted while Components is the selected destination.");
    Assert(!InstallComponentsWindowViewModel.ShouldUnloadComponentsPage(false, true),
        "A running install or configuration run must keep the Components page mounted.");
    Assert(!InstallComponentsWindowViewModel.ShouldUnloadComponentsPage(true, true),
        "A running operation on the open Components page must never release it.");

    Assert(MainWindowViewModel.ResolveServerWebPageUrl("CHIM") == "http://127.0.0.1:8081/HerikaServer/ui/"
           && MainWindowViewModel.ResolveServerWebPageUrl("STOBE") == "http://127.0.0.1:8083/StobeServer/ui/"
           && MainWindowViewModel.ResolveServerWebPageUrl("DIALECTIC") == "http://127.0.0.1:8088/DialecticServer/ui/",
        "Each mod webpage button must open that product's local web UI, not its Nexus page.");
    Assert(gameCatalog.All(game => MainWindowViewModel.ResolveServerWebPageUrl(game.Key) is not null),
        "Every game profile on the rail must resolve to a webpage URL.");
    Assert(MainWindowViewModel.ResolveNexusPageUrl("CHIM") == "https://www.nexusmods.com/skyrimspecialedition/mods/126330"
           && MainWindowViewModel.ResolveNexusPageUrl("STOBE") == "https://www.nexusmods.com/kenshi/mods/1891"
           && MainWindowViewModel.ResolveNexusPageUrl("DIALECTIC") == "https://www.nexusmods.com/newvegas/mods/99233",
        "Each Nexus button must open that mod's own Nexus page.");
    Assert(gameCatalog.All(game => MainWindowViewModel.ResolveNexusPageUrl(game.Key) is not null),
        "Every game profile on the rail must resolve to a Nexus page.");
    Assert(MainWindowViewModel.ResolveNexusPageUrl("UNKNOWN") is null,
        "An unknown product must resolve to no Nexus page rather than another mod's page.");
    Assert(gameCatalog.All(game => MainWindowViewModel.ResolveNexusPageUrl(game.Key)!
            .StartsWith("https://www.nexusmods.com/", StringComparison.Ordinal)),
        "A Nexus button must open an external page, never a local server URL.");
    Assert(MainWindowViewModel.IsServerWebPageResponseUsable(HttpStatusCode.OK)
           && MainWindowViewModel.IsServerWebPageResponseUsable(HttpStatusCode.Unauthorized),
        "A webpage that answers must count as reachable even when it demands a login.");
    Assert(!MainWindowViewModel.IsServerWebPageResponseUsable(HttpStatusCode.ServiceUnavailable),
        "A server-side failure must count as unreachable so the launcher offers to start the server.");
    Assert(MainWindowViewModel.ShouldOfferServerStart(false, false, true),
        "An unavailable webpage must offer to start the server when it is stopped.");
    Assert(!MainWindowViewModel.ShouldOfferServerStart(true, false, false)
           && !MainWindowViewModel.ShouldOfferServerStart(false, true, false)
           && !MainWindowViewModel.ShouldOfferServerStart(false, false, false),
        "A webpage must not offer another start while the server is running, starting, or its start command is busy.");

    Assert(MainWindowViewModel.MaxConsoleLines == 3000,
        "Launcher session output and diagnostics must share the 3,000-line limit.");
    var consoleAtLimit = string.Concat(Enumerable.Range(1, MainWindowViewModel.MaxConsoleLines)
        .Select(index => $"line-{index}\n"));
    Assert(MainWindowViewModel.TrimConsoleOutput(consoleAtLimit) == consoleAtLimit,
        "Console output at the limit must remain byte-identical.");

    var overflowingConsole = string.Concat(Enumerable.Range(1, 4000)
        .Select(index => $"line-{index}\n"));
    var trimmedConsole = MainWindowViewModel.TrimConsoleOutput(overflowingConsole);
    Assert(trimmedConsole.StartsWith("[Earlier console output trimmed.]", StringComparison.Ordinal),
        "Overflowing console output must explain that older lines were removed.");
    Assert(trimmedConsole.EndsWith("line-4000\n", StringComparison.Ordinal),
        "Console trimming must retain the newest output line.");
    Assert(trimmedConsole.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length <=
           MainWindowViewModel.MaxConsoleLines + 1,
        "Console output must contain at most 3,000 data lines plus the trim notice.");

    // --- ddistro_server status contract ---------------------------------------------------

    const string statusJson = """
    {
      "schema_version": 1,
      "servers": [
        {
          "product": "herika",
          "state": "installed",
          "repository_state": "managed",
          "database_present": true,
          "root": "/var/www/html/HerikaServer",
          "database": "dwemer",
          "branch": "aiagent",
          "version": "20260101",
          "production_branch": "aiagent",
          "development_branch": "dev",
          "port": 8081
        },
        {
          "product": "stobe",
          "state": "not-installed",
          "repository_state": "absent",
          "database_present": false,
          "root": "/var/www/html/StobeServer",
          "database": "stobe",
          "branch": null,
          "version": null,
          "production_branch": "stobe",
          "development_branch": "dev",
          "port": 8083
        },
        {
          "product": "dialectic",
          "state": "needs-repair",
          "repository_state": "legacy",
          "database_present": null,
          "root": "/var/www/html/DialecticServer",
          "database": "dialectic",
          "branch": "dev",
          "version": "20251201",
          "production_branch": "dialectic",
          "development_branch": "dev",
          "port": 8088
        }
      ]
    }
    """;

    Assert(ServerManagementService.TryParseStatus(statusJson, out var snapshot, out var statusError),
        "The documented schema version 1 status document must parse: " + statusError);
    Assert(snapshot!.SchemaVersion == ServerManagementService.SupportedSchemaVersion && snapshot.Servers.Count == 3,
        "Every documented product must survive parsing.");

    var herikaStatus = snapshot.Find(ServerProduct.Herika)!;
    Assert(herikaStatus.State == ServerInstallState.Installed
           && herikaStatus.RepositoryState == ServerRepositoryState.Managed
           && herikaStatus.DatabasePresent == true
           && herikaStatus.Root == "/var/www/html/HerikaServer"
           && herikaStatus.Database == "dwemer"
           && herikaStatus.Port == 8081
           && herikaStatus.IsUsable,
        "An installed product must carry its state, root, database, and port through parsing.");

    var stobeStatus = snapshot.Find(ServerProduct.Stobe)!;
    Assert(stobeStatus.State == ServerInstallState.NotInstalled
           && stobeStatus.RepositoryState == ServerRepositoryState.Absent
           && stobeStatus.DatabasePresent == false
           && stobeStatus.Branch is null
           && !stobeStatus.IsUsable,
        "A not-installed product must report absent repository state and must never be usable.");

    var dialecticStatus = snapshot.Find(ServerProduct.Dialectic)!;
    Assert(dialecticStatus.State == ServerInstallState.NeedsRepair
           && dialecticStatus.RepositoryState == ServerRepositoryState.Legacy
           && dialecticStatus.DatabasePresent is null
           && !dialecticStatus.IsUsable,
        "A null database_present must stay unknown rather than collapsing to false, and needs-repair is not usable.");

    Assert(ServerManagementService.TryParseStatus(
            "Checking servers...\n{\"schema_version\":1,\"servers\":[]}\n", out var noisySnapshot, out _)
           && noisySnapshot!.Servers.Count == 0,
        "Progress text printed before the JSON payload must not break status parsing.");

    Assert(!ServerManagementService.TryParseStatus(
            "{\"schema_version\":2,\"servers\":[]}", out _, out var versionError),
        "An unknown status schema version must be rejected instead of guessed at.");
    Assert(versionError!.Contains("schema version 2", StringComparison.OrdinalIgnoreCase)
           && versionError.Contains("Update the launcher", StringComparison.OrdinalIgnoreCase),
        "The schema mismatch must name the version and tell the user what to do.");

    Assert(!ServerManagementService.TryParseStatus("bash: ddistro_server: command not found", out _, out _),
        "A shell error instead of JSON must be reported as a failure.");
    Assert(!ServerManagementService.TryParseStatus(null, out _, out _)
           && !ServerManagementService.TryParseStatus("   ", out _, out _),
        "Empty status output must be reported as a failure.");

    Assert(ServerManagementService.TryParseStatus(
            "{\"schema_version\":1,\"servers\":[{\"product\":\"reign\",\"state\":\"installed\"}]}",
            out var futureSnapshot, out _)
           && futureSnapshot!.Servers.Count == 0,
        "A product this build does not know must be ignored, not fatal.");

    Assert(ServerManagementService.ParseInstallState("INSTALLED") == ServerInstallState.Installed
           && ServerManagementService.ParseInstallState(" needs-repair ") == ServerInstallState.NeedsRepair
           && ServerManagementService.ParseInstallState("gone") == ServerInstallState.Unknown,
        "Install states must parse case-insensitively and fall back to Unknown, never to NotInstalled.");

    // --- command allowlist -----------------------------------------------------------------

    Assert(ServerManagementService.BuildStatusArguments()
            .SequenceEqual(new[] { "/usr/local/bin/ddistro_server", "status", "all", "--json" }),
        "The status probe must call the documented root command with the versioned JSON flag.");

    var expectedProductTokens = new Dictionary<ServerProduct, string>
    {
        [ServerProduct.Herika] = "herika",
        [ServerProduct.Stobe] = "stobe",
        [ServerProduct.Dialectic] = "dialectic"
    };
    var expectedPurgeTokens = new Dictionary<ServerProduct, string>
    {
        [ServerProduct.Herika] = "PURGE-HERIKA",
        [ServerProduct.Stobe] = "PURGE-STOBE",
        [ServerProduct.Dialectic] = "PURGE-DIALECTIC"
    };

    foreach (var (product, token) in expectedProductTokens)
    {
        Assert(ServerManagementService.ToProductToken(product) == token,
            $"{product} must map to the documented product token.");
        Assert(ServerManagementService.GetPurgeToken(product) == expectedPurgeTokens[product],
            $"{product} must map to its documented PURGE confirmation token.");

        Assert(ServerManagementService.BuildInstallArguments(product, ServerBranchChannel.Main)
                .SequenceEqual(new[] { "stdbuf", "-oL", "-eL", "/usr/local/bin/ddistro_server", "install", token, "--branch", "main" }),
            $"install {token} must pass the production branch as an allowlisted token.");
        Assert(ServerManagementService.BuildUpdateArguments(product, ServerBranchChannel.Dev)
                .SequenceEqual(new[] { "stdbuf", "-oL", "-eL", "/usr/local/bin/ddistro_server", "update", token, "--branch", "dev" }),
            $"update {token} must pass the development branch as an allowlisted token.");
        Assert(ServerManagementService.BuildRepairArguments(product, ServerBranchChannel.Main)
                .SequenceEqual(new[] { "stdbuf", "-oL", "-eL", "/usr/local/bin/ddistro_server", "repair", token, "--branch", "main" }),
            $"repair {token} must use the same allowlisted verb and branch shape.");
        Assert(ServerManagementService.BuildUninstallArguments(product)
                .SequenceEqual(new[] { "stdbuf", "-oL", "-eL", "/usr/local/bin/ddistro_server", "uninstall", token, "--confirm", expectedPurgeTokens[product] }),
            $"uninstall {token} must pass only its own PURGE token to --confirm.");
    }

    var allBuiltArguments = expectedProductTokens.Keys
        .SelectMany(product => new[]
        {
            ServerManagementService.BuildInstallArguments(product, ServerBranchChannel.Main),
            ServerManagementService.BuildInstallArguments(product, ServerBranchChannel.Dev),
            ServerManagementService.BuildUpdateArguments(product, ServerBranchChannel.Main),
            ServerManagementService.BuildRepairArguments(product, ServerBranchChannel.Dev),
            ServerManagementService.BuildUninstallArguments(product)
        })
        .SelectMany(arguments => arguments)
        .Concat(ServerManagementService.BuildStatusArguments())
        .ToArray();
    Assert(allBuiltArguments.All(argument => argument.IndexOfAny(new[] { ' ', ';', '&', '|', '$', '`', '\n', '"', '\'' }) < 0),
        "No manager argument may carry shell metacharacters; every token is drawn from a fixed allowlist.");

    Assert(Throws(() => ServerManagementService.ToProductToken((ServerProduct)99)),
        "An out-of-range product must throw instead of being forwarded to the distro.");
    Assert(Throws(() => ServerManagementService.ToBranchToken((ServerBranchChannel)99)),
        "An out-of-range branch must throw instead of being forwarded to the distro.");
    Assert(Throws(() => ServerManagementService.GetPurgeToken((ServerProduct)99)),
        "An out-of-range product must never produce a purge token.");

    Assert(ServerManagementService.ParseBranchChannel("Dev") == ServerBranchChannel.Dev
           && ServerManagementService.ParseBranchChannel("dev") == ServerBranchChannel.Dev
           && ServerManagementService.ParseBranchChannel("Main") == ServerBranchChannel.Main
           && ServerManagementService.ParseBranchChannel(null) == ServerBranchChannel.Main
           && ServerManagementService.ParseBranchChannel("aiagent; rm -rf /") == ServerBranchChannel.Main,
        "Any branch text other than Dev must resolve to the production channel, never pass through.");

    Assert(ServerManagementService.TryParseGameKey("CHIM") == ServerProduct.Herika
           && ServerManagementService.TryParseGameKey("stobe") == ServerProduct.Stobe
           && ServerManagementService.TryParseGameKey("DIALECTIC") == ServerProduct.Dialectic
           && ServerManagementService.TryParseGameKey("REIGN") is null,
        "Rail keys must map onto managed products, and an unmanaged key must map to nothing.");

    Assert(gameCatalog.All(game => ServerManagementService.TryParseGameKey(game.Key) is not null),
        "Every rail product must be manageable, so all three icons keep working actions.");

    // --- Mods page state mapping ------------------------------------------------------------

    var herikaItem = new ServerManagerItemViewModel(
        ServerProduct.Herika, "CHIM", _ => Task.CompletedTask, _ => Task.CompletedTask, _ => Task.CompletedTask,
        _ => Task.CompletedTask);

    herikaItem.ApplyVersionStatus("aiagent | 01-01-2026 | 1.2.3", "LimeGreen", false);
    herikaItem.ApplyStatus(stobeStatus with { Product = ServerProduct.Herika });
    Assert(herikaItem.ShowNotInstalledActions && !herikaItem.ShowInstalledActions && !herikaItem.ShowRepairActions,
        "A not-installed product must show only the install branch and Install Server.");
    Assert(herikaItem.StatusText == "Not installed"
           && herikaItem.StatusColor == ServerManagerItemViewModel.NotInstalledColor,
        "A not-installed product must read Not installed in the neutral grey, not the version colours.");
    Assert(!herikaItem.CanUseInstalledFeatures && herikaItem.CanInstall && !herikaItem.CanUninstall,
        "Webpage, rollback and the update action must stay unreachable while nothing is installed.");
    Assert(!herikaItem.CanUpdate,
        "The single-product update must never be offered for a product that is not installed.");
    Assert(!herikaItem.IsUpdateAvailable,
        "A product that is not installed has nothing to update, so its button must stay themed.");

    herikaItem.ApplyStatus(herikaStatus);
    Assert(herikaItem.ShowInstalledActions && !herikaItem.ShowNotInstalledActions && !herikaItem.ShowRepairActions,
        "An installed product must show the branch, webpage, rollback and uninstall actions.");
    Assert(herikaItem.StatusText == "aiagent | 01-01-2026 | 1.2.3" && herikaItem.StatusColor == "LimeGreen",
        "An installed product must keep the existing green/yellow version status semantics.");
    Assert(herikaItem.CanUseInstalledFeatures && herikaItem.CanUninstall && !herikaItem.CanInstall,
        "An installed product must offer uninstall but never a second install.");
    Assert(herikaItem.CanUpdate && herikaItem.UpdateCommand.CanExecute(null),
        "An installed, idle product must offer its own update action.");
    Assert(!herikaItem.IsUpdateAvailable,
        "An installed product that is current must keep its per-mod themed update button.");
    Assert(herikaItem.SelectedBranch == "Main",
        "The reported production branch must map back to the Main branch choice.");
    Assert(herikaItem.LocationSummary.Contains("/var/www/html/HerikaServer", StringComparison.Ordinal)
           && herikaItem.LocationSummary.Contains("dwemer", StringComparison.Ordinal),
        "The status help text must name the exact files and database the uninstall would delete.");

    herikaItem.ApplyStatus(dialecticStatus with { Product = ServerProduct.Herika });
    Assert(herikaItem.ShowRepairActions && !herikaItem.ShowInstalledActions && !herikaItem.ShowNotInstalledActions,
        "A needs-repair product must show Repair Installation and Uninstall Server only.");
    Assert(herikaItem.StatusText == "Needs repair"
           && herikaItem.StatusColor == ServerManagerItemViewModel.NeedsRepairColor,
        "Needs repair must read in the orange warning colour, not the installed green.");
    Assert(herikaItem.CanRepair && herikaItem.CanUninstall && !herikaItem.CanUseInstalledFeatures,
        "A needs-repair product must not expose webpage or rollback, but must stay uninstallable.");
    Assert(!herikaItem.CanUpdate,
        "A needs-repair product must be repaired, not updated over the top of a broken install.");
    Assert(!herikaItem.IsUpdateAvailable,
        "A needs-repair product must not go green: repair comes before any update.");
    Assert(herikaItem.SelectedBranch == "Dev",
        "A checked-out development branch must map back to the Dev branch choice.");

    herikaItem.BeginOperation("Installing (Main)...");
    Assert(herikaItem.IsBusy && herikaItem.StatusText == "Installing (Main)..."
           && herikaItem.StatusColor == ServerManagerItemViewModel.BusyColor,
        "A running operation must take over the status line without changing which actions exist.");
    Assert(!herikaItem.CanRepair && !herikaItem.CanUninstall && !herikaItem.CanInstall && !herikaItem.CanUpdate,
        "No second operation may start while one is running.");
    Assert(herikaItem.UpdateActionHelpText.Contains("busy", StringComparison.OrdinalIgnoreCase),
        "A product update disabled by its own running operation must explain why it is unavailable.");

    herikaItem.EndOperation("Last repair failed");
    Assert(!herikaItem.IsBusy && herikaItem.StatusText == "Last repair failed"
           && herikaItem.StatusColor == ServerManagerItemViewModel.ErrorColor,
        "A failed operation must surface in the same fixed status line, in the error colour.");

    herikaItem.ApplyStatusError("Server status unavailable");
    Assert(herikaItem.StatusText == "Server status unavailable",
        "A failed status probe must explain itself instead of silently claiming Not installed.");

    // --- single-product update action -------------------------------------------------------

    Assert(ServerManagerItemViewModel.BuildUpdateActionName(ServerProduct.Herika) == "Update CHIM"
           && ServerManagerItemViewModel.BuildUpdateActionName(ServerProduct.Stobe) == "Update STOBE"
           && ServerManagerItemViewModel.BuildUpdateActionName(ServerProduct.Dialectic) == "Update Dialectic",
        "Each mod page must name its own update action after the rail product it is showing.");
    Assert(Throws(() => ServerManagerItemViewModel.BuildUpdateActionName((ServerProduct)99)),
        "An out-of-range product must never produce an update action label.");

    herikaItem.ApplyStatus(herikaStatus);
    Assert(herikaItem.CanUpdate, "The test precondition requires an installed, idle product.");
    herikaItem.IsConflictingOperationRunning = true;
    Assert(!herikaItem.CanUpdate && !herikaItem.UpdateCommand.CanExecute(null),
        "A running system update or sibling server operation must disable the single-product update.");
    Assert(herikaItem.UpdateActionHelpText.Contains("unavailable", StringComparison.OrdinalIgnoreCase),
        "The disabled update action must say why it is unavailable, not just dim.");
    Assert(!herikaItem.CanUninstall && !herikaItem.UninstallCommand.CanExecute(null)
           && !herikaItem.CanUseInstalledFeatures && herikaItem.ShowInstalledActions,
        "Conflicting operations must disable lifecycle actions without removing their controls.");

    herikaItem.IsConflictingOperationRunning = false;
    Assert(herikaItem.CanUpdate && herikaItem.UpdateActionHelpText.Contains("Main", StringComparison.Ordinal)
           && herikaItem.UpdateActionHelpText.Contains("HerikaServer", StringComparison.Ordinal),
        "The enabled update action must name the one server and the branch it will use.");

    // --- confirmed update available drives the green update button --------------------------

    // The flag is the version comparison's own answer, not a reading of the status colour: the
    // yellow below also stands for an unknown or a missing version.
    herikaItem.ApplyVersionStatus(
        MainWindowViewModel.BuildServerVersionStatusText("herika", "aiagent", "01-01-2026", "1.2.3", true),
        "Yellow",
        true);
    Assert(herikaItem.IsUpdateAvailable,
        "A confirmed newer version on the selected branch must turn the mod's update button green.");
    Assert(herikaItem.StatusText.EndsWith(" | Update Available", StringComparison.Ordinal),
        "The mod control menu status line must end with Update Available after the version info.");
    Assert(herikaItem.UpdateActionHelpText.Contains("Update Available", StringComparison.Ordinal),
        "A screen reader must hear the confirmed update, not only see the colour change.");

    herikaItem.ApplyVersionStatus(
        MainWindowViewModel.BuildServerVersionStatusText("herika", "aiagent", null, null),
        "Yellow",
        false);
    Assert(!herikaItem.IsUpdateAvailable && herikaItem.StatusColor == "Yellow",
        "Yellow for an unknown or missing version must leave the update button in its mod theme.");
    Assert(!herikaItem.StatusText.Contains("Update Available", StringComparison.Ordinal),
        "An unknown version must never claim an update in either place the version is shown.");

    herikaItem.ApplyVersionStatus(
        MainWindowViewModel.BuildServerVersionStatusText("herika", "aiagent", "01-01-2026", "1.2.3", true),
        "Yellow",
        true);
    Assert(herikaItem.IsUpdateAvailable, "The test precondition requires a confirmed update.");
    herikaItem.IsConflictingOperationRunning = true;
    Assert(!herikaItem.IsUpdateAvailable,
        "A confirmed update must drop back to the themed button while another operation is running.");
    herikaItem.IsConflictingOperationRunning = false;

    herikaItem.BeginOperation("Updating (Main)...");
    Assert(!herikaItem.IsUpdateAvailable,
        "A busy mod must not show a green call to action for work that is already running.");
    herikaItem.EndOperation("Last update failed");
    Assert(!herikaItem.IsUpdateAvailable,
        "A failed operation must read as failed, not as an available update.");

    herikaItem.EndOperation();
    Assert(herikaItem.IsUpdateAvailable,
        "Clearing the failure must restore the still-confirmed update.");
    herikaItem.ApplyStatus(dialecticStatus with { Product = ServerProduct.Herika });
    Assert(!herikaItem.IsUpdateAvailable,
        "A product that falls into needs-repair must stop advertising an update.");
    herikaItem.ApplyStatus(herikaStatus);

    var individualUpdateInvoked = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    var individualUpdateCount = 0;
    var individualUpdateItem = new ServerManagerItemViewModel(
        ServerProduct.Stobe,
        "STOBE",
        _ => Task.CompletedTask,
        _ =>
        {
            individualUpdateCount++;
            individualUpdateInvoked.TrySetResult(true);
            return Task.CompletedTask;
        },
        _ => Task.CompletedTask,
        _ => Task.CompletedTask);
    individualUpdateItem.ApplyStatus(stobeStatus with
    {
        State = ServerInstallState.Installed,
        RepositoryState = ServerRepositoryState.Managed,
        DatabasePresent = true
    });
    individualUpdateItem.UpdateCommand.Execute(null);
    await individualUpdateInvoked.Task.WaitAsync(TimeSpan.FromSeconds(2));
    Assert(individualUpdateCount == 1,
        "The individual update command must invoke only its product update delegate once.");

    // --- No saved preference gates the single-product update ---------------------------------

    // No saved preference gates a mod update: an installed, idle mod is updatable with nothing else set.
    Assert(individualUpdateItem.CanUpdate && individualUpdateItem.UpdateCommand.CanExecute(null),
        "An installed idle mod must be updatable without any saved update preference.");
    individualUpdateItem.UpdateCommand.Execute(null);
    Assert(individualUpdateCount == 2,
        "An installed idle mod's update must run every time its own button is invoked.");

    individualUpdateItem.IsConflictingOperationRunning = true;
    Assert(!individualUpdateItem.CanUpdate && !individualUpdateItem.UpdateCommand.CanExecute(null)
           && individualUpdateItem.UpdateActionHelpText.Contains("another server, component, or system operation", StringComparison.Ordinal),
        "A conflicting operation must disable the mod update and say why.");
    individualUpdateItem.UpdateCommand.Execute(null);
    Assert(individualUpdateCount == 2,
        "A conflicted product's update must not run even when its command is invoked directly.");
    individualUpdateItem.IsConflictingOperationRunning = false;
    Assert(individualUpdateItem.CanUpdate && individualUpdateItem.CanUninstall
           && individualUpdateItem.ShowInstalledActions
           && individualUpdateItem.StatusColor != ServerManagerItemViewModel.ErrorColor,
        "Clearing the conflict must restore the update action alongside the rest of the lifecycle.");

    foreach (var ineligible in new[]
             {
                 ServerInstallState.NotInstalled, ServerInstallState.NeedsRepair, ServerInstallState.Unknown
             })
    {
        individualUpdateItem.ApplyStatus(stobeStatus with { State = ineligible, DatabasePresent = true });
        Assert(!individualUpdateItem.CanUpdate && !individualUpdateItem.UpdateCommand.CanExecute(null)
               && individualUpdateItem.UpdateActionHelpText.Contains("until STOBE is installed", StringComparison.Ordinal),
            $"A {ineligible} mod must stay ineligible for update and explain that it is not installed.");
        individualUpdateItem.UpdateCommand.Execute(null);
    }

    Assert(individualUpdateCount == 2,
        "No un-installed state may run a mod update, however its command is invoked.");
    individualUpdateItem.ApplyStatus(stobeStatus with
    {
        State = ServerInstallState.Installed,
        RepositoryState = ServerRepositoryState.Managed,
        DatabasePresent = true
    });

    Assert(ServerManagerItemViewModel.MapBranchToChannel("aiagent", "aiagent", "dev") == ServerBranchChannel.Main
           && ServerManagerItemViewModel.MapBranchToChannel("dev", "aiagent", "dev") == ServerBranchChannel.Dev
           && ServerManagerItemViewModel.MapBranchToChannel("unstable", null, null) == ServerBranchChannel.Dev
           && ServerManagerItemViewModel.MapBranchToChannel("feature/x", "aiagent", "dev") is null
           && ServerManagerItemViewModel.MapBranchToChannel(null, "aiagent", "dev") is null,
        "Branch mapping must prefer the manager's reported branch names and leave anything unknown alone.");

    // A status callback can arrive while the confirmation dialog pumps the UI dispatcher.
    foreach (var status in new[] { herikaStatus, stobeStatus, dialecticStatus })
    {
        var installedStatus = status with
        {
            State = ServerInstallState.Installed,
            RepositoryState = ServerRepositoryState.Managed,
            DatabasePresent = true,
            Branch = status.ProductionBranch
        };
        var branchRaceItem = new ServerManagerItemViewModel(status.Product, status.Product.ToString(),
            _ => Task.CompletedTask, _ => Task.CompletedTask, _ => Task.CompletedTask, _ => Task.CompletedTask);
        branchRaceItem.ApplyStatus(installedStatus);
        var defaultUpdates = MainWindowViewModel.SnapshotModUpdates([branchRaceItem]);
        branchRaceItem.ApplyStatus(installedStatus with { Branch = status.DevelopmentBranch });
        ServerBranchChannel? executedBranch = null;
        await MainWindowViewModel.UpdateInstalledServersAsync(defaultUpdates,
            () => Task.FromResult(true),
            (_, branch) => { executedBranch = branch; return Task.FromResult(true); });
        Assert(branchRaceItem.SelectedBranch == "Dev" && executedBranch == ServerBranchChannel.Main
               && MainWindowViewModel.BuildModsUpdateConfirmation(defaultUpdates).Contains("target branch: Main", StringComparison.Ordinal),
            $"{status.Product}: an automatic branch refresh must not rewrite the already-confirmed default branch.");
        branchRaceItem.ApplyStatus(installedStatus);
        branchRaceItem.SelectedBranch = "Dev";
        var confirmedUpdates = MainWindowViewModel.SnapshotModUpdates([branchRaceItem]);
        var confirmedUpdate = MainWindowViewModel.BuildModsUpdateConfirmation(confirmedUpdates);
        branchRaceItem.ApplyStatus(installedStatus);
        executedBranch = null;
        await MainWindowViewModel.UpdateInstalledServersAsync(confirmedUpdates,
            () => Task.FromResult(true),
            (_, branch) => { executedBranch = branch; return Task.FromResult(true); });
        Assert(confirmedUpdate.Contains("target branch: Dev", StringComparison.Ordinal)
               && executedBranch == ServerBranchChannel.Dev,
            $"{status.Product}: a refresh during confirmation must not change the approved update branch.");
        Assert(branchRaceItem.SelectedBranch == "Dev",
            $"{status.Product}: passive status refresh must preserve the user's staged branch choice.");
    }

    // --- Mod update gating ------------------------------------------------------------------

    Assert(MainWindowViewModel.ShouldUpdateProduct(ServerInstallState.Installed),
        "An installed product must be updated; no saved preference gates it any more.");
    Assert(!MainWindowViewModel.ShouldUpdateProduct(ServerInstallState.NotInstalled)
           && !MainWindowViewModel.ShouldUpdateProduct(ServerInstallState.NeedsRepair)
           && !MainWindowViewModel.ShouldUpdateProduct(ServerInstallState.Unknown),
        "Update must never reach a product that is not installed.");

    var sharedUpdateCommand = MainWindowViewModel.BuildSharedComponentsUpdateCommand();
    Assert(sharedUpdateCommand.StartsWith("/usr/local/bin/update_gws", StringComparison.Ordinal),
        "The shared distro update must still run update_gws.");
    Assert(sharedUpdateCommand.Contains("--skip-herika", StringComparison.Ordinal)
           && sharedUpdateCommand.Contains("--skip-stobe", StringComparison.Ordinal)
           && sharedUpdateCommand.Contains("--skip-dialectic", StringComparison.Ordinal),
        "update_gws must skip every application server; the server manager owns those repositories.");

    var systemUpdateCommand = MainWindowViewModel.BuildSystemUpdateCommand();
    Assert(systemUpdateCommand.Contains("if [ ! -d .git ]; then git init", StringComparison.Ordinal)
           && systemUpdateCommand.Contains("git remote add origin https://github.com/abeiro/dwemerdistro.git", StringComparison.Ordinal),
        "Update Distro must bootstrap Git metadata for a freshly installed empty distro.");
    var systemReleaseMarkerWrite = MainWindowViewModel.BuildSystemReleaseMarkerWriteCommand();
    Assert(systemUpdateCommand.Contains("git fetch origin && git reset --hard origin/main", StringComparison.Ordinal)
           && systemUpdateCommand.Contains(sharedUpdateCommand + " && ", StringComparison.Ordinal)
           && systemUpdateCommand.EndsWith(systemReleaseMarkerWrite, StringComparison.Ordinal),
        "Update Distro must update the distro and shared components before recording the successful system release.");
    Assert(systemReleaseMarkerWrite.Contains("sudo -S install -D -m 0644", StringComparison.Ordinal)
           && systemReleaseMarkerWrite.Contains("/home/dwemer/dwemerdistro/system-release.json", StringComparison.Ordinal)
           && systemReleaseMarkerWrite.Contains("/var/lib/dwemerdistro/system-release.json", StringComparison.Ordinal),
        "The installed system marker must be copied from the fetched release manifest with root-owned system permissions.");
    Assert(MainWindowViewModel.BuildSystemReleaseMarkerReadCommand()
            == "cat /var/lib/dwemerdistro/system-release.json 2>/dev/null || true",
        "A missing installed system marker must remain a normal update-available state, not a shell failure.");

    const string systemRelease100 = "{\"schema_version\":1,\"version\":\"1.0.0\"}";
    Assert(MainWindowViewModel.ParseSystemReleaseVersion(systemRelease100) == "1.0.0",
        "A valid system release manifest must expose its semantic version.");
    Assert(MainWindowViewModel.ParseSystemReleaseVersion(null) is null
           && MainWindowViewModel.ParseSystemReleaseVersion("not json") is null
           && MainWindowViewModel.ParseSystemReleaseVersion("{\"schema_version\":2,\"version\":\"1.0.0\"}") is null
           && MainWindowViewModel.ParseSystemReleaseVersion("{\"schema_version\":1,\"version\":\"1..0\"}") is null,
        "Missing, malformed, unsupported, or invalid system manifests must fail closed to Unknown.");
    Assert(MainWindowViewModel.ResolveSystemUpdateAvailability("1.0.0", "1.0.0")
            == SystemUpdateAvailability.Current
           && MainWindowViewModel.ResolveSystemUpdateAvailability(null, "1.0.0")
            == SystemUpdateAvailability.UpdateAvailable
           && MainWindowViewModel.ResolveSystemUpdateAvailability("1.0.0", "1.0.1")
            == SystemUpdateAvailability.UpdateAvailable
           && MainWindowViewModel.ResolveSystemUpdateAvailability("1.0.0", null)
            == SystemUpdateAvailability.Unknown,
        "System availability must compare the last successful marker with the published manifest and preserve recovery when remote status is unknown.");

    var modsUpdateConfirmation = MainWindowViewModel.BuildModsUpdateConfirmation(
        MainWindowViewModel.SnapshotModUpdates([herikaItem, individualUpdateItem]));
    Assert(modsUpdateConfirmation.Contains("selected installed mods", StringComparison.Ordinal)
           && modsUpdateConfirmation.Contains("DwemerDistro and shared components first", StringComparison.Ordinal),
        "A mod update must clearly include the system update before the selected mods.");
    Assert(modsUpdateConfirmation.Contains($"{herikaItem.DisplayName} target branch", StringComparison.Ordinal)
           && modsUpdateConfirmation.Contains($"{individualUpdateItem.DisplayName} target branch", StringComparison.Ordinal),
        "A mod update must list every selected installed mod and its target branch.");

    var systemUpdateConfirmation = MainWindowViewModel.BuildSystemUpdateConfirmation();
    Assert(systemUpdateConfirmation.Contains("DwemerDistro and its shared components", StringComparison.Ordinal)
           && systemUpdateConfirmation.Contains("Installed mods will not be changed", StringComparison.Ordinal),
        "Update Distro must clearly exclude every installed mod server.");

    Assert(MainWindowViewModel.CanRunUpdateOperation(false, false, [false, false, false]),
        "Update actions must be available when every shared operation gate is idle.");
    Assert(!MainWindowViewModel.CanRunUpdateOperation(true, false, [false, false, false])
           && !MainWindowViewModel.CanRunUpdateOperation(false, true, [false, false, false])
           && !MainWindowViewModel.CanRunUpdateOperation(false, false, [false, true, false]),
        "A system update, component operation, or individual server operation must block every competing update action.");

    // --- System-first mod update sequence (no live WSL commands) -----------------------------

    var updateOrder = new List<string>();
    herikaItem.SelectedBranch = "Dev";
    individualUpdateItem.SelectedBranch = "Main";
    var systemFinished = new TaskCompletionSource<bool>();
    var batchUpdate = MainWindowViewModel.UpdateInstalledServersAsync(
        MainWindowViewModel.SnapshotModUpdates([herikaItem, individualUpdateItem]),
        () => { updateOrder.Add("system"); return systemFinished.Task; },
        (product, branch) =>
        {
            updateOrder.Add($"{product.Product}:{branch}");
            return Task.FromResult(product.Product != ServerProduct.Herika);
        });
    Assert(updateOrder.SequenceEqual(["system"]), "Mods must wait for the shared system update to finish.");
    herikaItem.SelectedBranch = "Main";
    individualUpdateItem.SelectedBranch = "Dev";
    systemFinished.SetResult(true);
    Assert(!await batchUpdate && updateOrder.SequenceEqual(["system", "Herika:Dev", "Stobe:Main"]),
        "A batch must run system once, preserve original branches, continue after a mod failure, and report failure.");

    foreach (var product in Enum.GetValues<ServerProduct>())
    {
        var item = new ServerManagerItemViewModel(product, product.ToString(),
            _ => Task.CompletedTask, _ => Task.CompletedTask, _ => Task.CompletedTask, _ => Task.CompletedTask);
        item.ApplyStatus(herikaStatus with { Product = product });
        updateOrder.Clear();
        Assert(await MainWindowViewModel.UpdateInstalledServersAsync(MainWindowViewModel.SnapshotModUpdates([item]),
                () => { updateOrder.Add("system"); return Task.FromResult(true); },
                (selected, _) => { updateOrder.Add(selected.Product.ToString()); return Task.FromResult(true); })
               && updateOrder.SequenceEqual(["system", product.ToString()]),
            "Every individual mod update must run system first and then only that mod.");
        Assert(item.UpdateActionHelpText.Contains("DwemerDistro and shared components first", StringComparison.Ordinal),
            "Each individual update must expose the system-first behavior in accessible help.");
    }

    var skippedModCalls = 0;
    foreach (var throwSystemError in new[] { false, true })
    {
        try
        {
            var batchSucceeded = await MainWindowViewModel.UpdateInstalledServersAsync(
                MainWindowViewModel.SnapshotModUpdates([herikaItem, individualUpdateItem]),
                () => throwSystemError ? Task.FromException<bool>(new IOException("system failure")) : Task.FromResult(false),
                (_, _) => { skippedModCalls++; return Task.FromResult(true); });
            Assert(!batchSucceeded, "A failed shared update must fail the batch.");
        }
        catch (IOException) when (throwSystemError) { }
    }
    Assert(skippedModCalls == 0, "System failure or exception must prevent every mod update.");

    var staleUpdates = MainWindowViewModel.SnapshotModUpdates([herikaItem, individualUpdateItem]);
    herikaItem.ApplyStatus(herikaStatus with { State = ServerInstallState.NeedsRepair });
    individualUpdateItem.ApplyStatus(stobeStatus with { State = ServerInstallState.NotInstalled });
    Assert(MainWindowViewModel.SnapshotModUpdates([herikaItem, individualUpdateItem]).Count == 0,
        "A mod that stopped being installed must be excluded from a newly confirmed selection.");
    var staleSystemRuns = 0;
    Assert(await MainWindowViewModel.UpdateInstalledServersAsync(staleUpdates,
            () => { staleSystemRuns++; return Task.FromResult(true); },
            (_, _) => throw new InvalidOperationException("A missing or broken mod must not update."))
           && staleSystemRuns == 1,
        "Mods that became missing or broken must still leave a successful system-only update.");
    individualUpdateItem.IsConflictingOperationRunning = true;
    Assert(!individualUpdateItem.CanInstall && !individualUpdateItem.InstallCommand.CanExecute(null),
        "A system operation must block installation of a missing mod.");
    individualUpdateItem.ApplyStatus(stobeStatus with { State = ServerInstallState.NeedsRepair });
    Assert(!individualUpdateItem.CanRepair && !individualUpdateItem.RepairCommand.CanExecute(null)
           && !individualUpdateItem.CanUninstall,
        "A system operation must block repair and uninstall of a damaged mod.");
    individualUpdateItem.IsConflictingOperationRunning = false;
    Assert(individualUpdateItem.CanRepair && individualUpdateItem.CanUninstall,
        "Lifecycle controls must be restored after the operation finishes.");

    // --- Update Distro as the single top-level update and the recovery action -----------------

    var systemStates = Enum.GetValues<SystemUpdateAvailability>();
    Assert(systemStates.Length == 6,
        "The system status line must cover checking, current, update available, unknown, updating, and failed.");

    foreach (var state in systemStates)
    {
        var statusText = MainWindowViewModel.BuildSystemStatusText(state, null, null);
        Assert(statusText.StartsWith("System:", StringComparison.Ordinal) && statusText.Length > "System:".Length,
            $"The {state} system state must say what it is in words, not only in colour.");
        Assert(MainWindowViewModel.BuildSystemStatusColor(state).Length > 0,
            $"The {state} system state must resolve to a status colour.");

        // The explicit availability state, never the status colour, decides the label: checking,
        // current, unknown and failed all keep the plain action label.
        var idleButtonText = MainWindowViewModel.BuildSystemUpdateIdleButtonText(state);
        Assert(idleButtonText == (state == SystemUpdateAvailability.UpdateAvailable
                ? "Distro Update Available"
                : "Update Distro"),
            $"The {state} system state must decide the top button label on its own.");

        var accessibleName = MainWindowViewModel.BuildSystemUpdateAccessibleName(idleButtonText, state);
        Assert(accessibleName.StartsWith(idleButtonText + ",", StringComparison.Ordinal),
            $"The {state} system state must keep the button label at the front of its accessible name.");

        // Update Distro is also the recovery action, so no state may describe it as unavailable.
        Assert(!MainWindowViewModel.BuildUpdateSystemHelpText(true, state, null, null)
                .Contains("Unavailable", StringComparison.OrdinalIgnoreCase),
            $"Update Distro must stay available in the {state} state whenever no other operation is running.");
        Assert(MainWindowViewModel.BuildUpdateSystemHelpText(false, state, null, null)
                .Contains("another server, component, or system operation", StringComparison.Ordinal),
            "A competing operation must stay the only reason Update Distro is unavailable.");
    }

    Assert(systemStates.Select(state => MainWindowViewModel.BuildSystemStatusText(state, null, null))
               .Distinct(StringComparer.Ordinal).Count() == systemStates.Length,
        "Every system state must read differently, so the line is never ambiguous without colour.");
    Assert(systemStates.Select(state => MainWindowViewModel.BuildSystemUpdateAccessibleName(
                   MainWindowViewModel.BuildSystemUpdateIdleButtonText(state), state))
               .Distinct(StringComparer.Ordinal).Count() == systemStates.Length,
        "Every system state must be distinguishable from the button's accessible name alone.");
    Assert(systemStates.Count(state => MainWindowViewModel.BuildSystemUpdateIdleButtonText(state)
            == MainWindowViewModel.SystemUpdateAvailableButtonText) == 1,
        "Only a confirmed available update may advertise one on the top button.");
    Assert(MainWindowViewModel.BuildSystemUpdateIdleButtonText(SystemUpdateAvailability.UpdateAvailable)
            == "Distro Update Available"
           && MainWindowViewModel.BuildSystemUpdateIdleButtonText(SystemUpdateAvailability.Failed)
            == "Update Distro"
           && MainWindowViewModel.BuildSystemUpdateIdleButtonText(SystemUpdateAvailability.Unknown)
            == "Update Distro",
        "The top action must read Update Distro until a check confirms an available distro update.");

    Assert(MainWindowViewModel.BuildSystemStatusText(SystemUpdateAvailability.UpdateAvailable, "1.2", "1.3")
               .Contains("installed 1.2", StringComparison.Ordinal)
           && MainWindowViewModel.BuildSystemStatusText(SystemUpdateAvailability.UpdateAvailable, "1.2", "1.3")
               .Contains("latest 1.3", StringComparison.Ordinal),
        "A known version pair must name both the installed and the available system version.");
    Assert(MainWindowViewModel.BuildSystemStatusText(SystemUpdateAvailability.UpdateAvailable, null, null)
            == "System: update available.",
        "An update reported without versions must still say an update is available.");
    Assert(MainWindowViewModel.BuildSystemStatusText(SystemUpdateAvailability.Current, "  1.3  ", null)
            == "System: up to date (version 1.3).",
        "A reported version must be trimmed before it reaches the status line.");
    Assert(MainWindowViewModel.BuildSystemStatusText(SystemUpdateAvailability.Current, "   ", null)
            == "System: up to date.",
        "A blank version must be treated as no version at all.");
    Assert(MainWindowViewModel.BuildSystemStatusText(SystemUpdateAvailability.Unknown, null, null)
            .Contains("repairs a distro that cannot report it", StringComparison.Ordinal),
        "The unknown state must point at Update Distro as the recovery action.");
    Assert(MainWindowViewModel.BuildSystemStatusText(SystemUpdateAvailability.Failed, null, null)
            .Contains("Run Update Distro to retry", StringComparison.Ordinal),
        "A failed update must offer the retry instead of going quiet.");

    // The badge is a glyph, so the top button's signal survives a display that drops its colour.
    Assert(MainWindowViewModel.BuildSystemUpdateBadgeGlyph(SystemUpdateAvailability.UpdateAvailable)
            == MainWindowViewModel.SystemUpdateAvailableGlyph,
        "An available system update must raise the badge on the top Update Distro button.");
    Assert(MainWindowViewModel.BuildSystemUpdateBadgeGlyph(SystemUpdateAvailability.Failed)
            == MainWindowViewModel.SystemUpdateFailedGlyph
           && MainWindowViewModel.BuildSystemUpdateBadgeGlyph(SystemUpdateAvailability.Unknown)
            == MainWindowViewModel.SystemUpdateUnknownGlyph,
        "A failed or unknown system state must raise its own badge rather than reuse another one.");
    Assert(new[] { MainWindowViewModel.SystemUpdateAvailableGlyph, MainWindowViewModel.SystemUpdateFailedGlyph,
                   MainWindowViewModel.SystemUpdateUnknownGlyph }.Distinct(StringComparer.Ordinal).Count() == 3,
        "Each signalled system state must use its own glyph.");
    Assert(MainWindowViewModel.BuildSystemUpdateBadgeGlyph(SystemUpdateAvailability.Checking).Length == 0
           && MainWindowViewModel.BuildSystemUpdateBadgeGlyph(SystemUpdateAvailability.Current).Length == 0
           && MainWindowViewModel.BuildSystemUpdateBadgeGlyph(SystemUpdateAvailability.Updating).Length == 0,
        "A state the button label already reports must not add a badge on top of it.");

    Assert(MainWindowViewModel.BuildSystemUpdateAccessibleName("Updating Distro...", SystemUpdateAvailability.Updating)
            .StartsWith("Updating Distro...,", StringComparison.Ordinal),
        "The accessible name must follow the running button label instead of a fixed one.");
    Assert(MainWindowViewModel.BuildSystemUpdateAccessibleName("   ", SystemUpdateAvailability.Current)
            .StartsWith("Update Distro,", StringComparison.Ordinal),
        "A missing button label must still leave a usable accessible name.");

    var systemOnlyConfirmation = MainWindowViewModel.BuildModsUpdateConfirmation([]);
    Assert(systemOnlyConfirmation.Contains("shared components only", StringComparison.Ordinal)
           && systemOnlyConfirmation.Contains("will not be updated", StringComparison.Ordinal)
           && systemOnlyConfirmation.Contains("Missing mods are never installed", StringComparison.Ordinal),
        "An empty selection must confirm a system-only update rather than report a missing-mod error.");

    var systemOnlyRuns = 0;
    Assert(await MainWindowViewModel.UpdateInstalledServersAsync(
            [],
            () => { systemOnlyRuns++; return Task.FromResult(true); },
            (_, _) => throw new InvalidOperationException("A system-only update must not touch any mod."))
           && systemOnlyRuns == 1,
        "A mod update with no eligible mod must run the system update once and succeed.");
    Assert(!await MainWindowViewModel.UpdateInstalledServersAsync(
            [],
            () => Task.FromResult(false),
            (_, _) => throw new InvalidOperationException("A failed system update must not touch any mod.")),
        "A failed system-only update must still report failure.");

    // Recovery: the status probe is unreadable when the sweep starts, and the system update repairs it.
    var recoveredItem = new ServerManagerItemViewModel(
        ServerProduct.Herika, "CHIM", _ => Task.CompletedTask, _ => Task.CompletedTask, _ => Task.CompletedTask,
        _ => Task.CompletedTask);
    var recoveredInstalledStatus = herikaStatus with { Branch = herikaStatus.ProductionBranch };
    recoveredItem.ApplyStatusError("Server status unavailable");
    var recoveredUpdates = MainWindowViewModel.SnapshotModUpdates([recoveredItem]);
    Assert(recoveredUpdates.Count == 0,
        "A product whose status is unavailable must not be part of the confirmed selection.");
    var recoveredOrder = new List<string>();
    Assert(await MainWindowViewModel.UpdateInstalledServersAsync(
            recoveredUpdates,
            () =>
            {
                recoveredItem.ApplyStatus(recoveredInstalledStatus);
                recoveredOrder.Add("system");
                return Task.FromResult(true);
            },
            (product, branch) => { recoveredOrder.Add($"{product.Product}:{branch}"); return Task.FromResult(true); },
            () => Task.FromResult(MainWindowViewModel.SnapshotModUpdates([recoveredItem])))
           && recoveredOrder.SequenceEqual(["system", "Herika:Main"]),
        "A mod that only becomes visible after the system update must still be updated on its selected branch.");

    recoveredOrder.Clear();
    recoveredItem.ApplyStatus(recoveredInstalledStatus with { State = ServerInstallState.NotInstalled });
    Assert(await MainWindowViewModel.UpdateInstalledServersAsync(
            recoveredUpdates,
            () => { recoveredOrder.Add("system"); return Task.FromResult(true); },
            (_, _) => throw new InvalidOperationException("A missing mod must never be installed by a mod update."),
            () => Task.FromResult(MainWindowViewModel.SnapshotModUpdates([recoveredItem])))
           && recoveredOrder.SequenceEqual(["system"]),
        "The post-update refresh must never install a mod that is still missing.");

    recoveredOrder.Clear();
    recoveredItem.ApplyStatus(recoveredInstalledStatus with { State = ServerInstallState.NeedsRepair });
    Assert(await MainWindowViewModel.UpdateInstalledServersAsync(
            recoveredUpdates,
            () => { recoveredOrder.Add("system"); return Task.FromResult(true); },
            (_, _) => throw new InvalidOperationException("A broken mod must never be updated by a mod update."),
            () => Task.FromResult(MainWindowViewModel.SnapshotModUpdates([recoveredItem])))
           && recoveredOrder.SequenceEqual(["system"]),
        "The post-update refresh must never update a mod that still needs repair.");
    recoveredItem.ApplyStatus(recoveredInstalledStatus);

    recoveredItem.SelectedBranch = "Main";
    var branchApprovedUpdates = MainWindowViewModel.SnapshotModUpdates([recoveredItem]);
    recoveredItem.SelectedBranch = "Dev";
    ServerBranchChannel? mergedBranch = null;
    Assert(await MainWindowViewModel.UpdateInstalledServersAsync(
            branchApprovedUpdates,
            () => Task.FromResult(true),
            (_, branch) => { mergedBranch = branch; return Task.FromResult(true); },
            () => Task.FromResult(MainWindowViewModel.SnapshotModUpdates([recoveredItem])))
           && mergedBranch == ServerBranchChannel.Main,
        "The post-update refresh must not rewrite the branch the user already approved.");
    Assert(MainWindowViewModel.MergeConfirmedBranches(
                branchApprovedUpdates,
                MainWindowViewModel.SnapshotModUpdates([recoveredItem]))
            .Single().Branch == ServerBranchChannel.Main,
        "A confirmed product must keep its approved branch when the refreshed snapshot disagrees.");
    Assert(MainWindowViewModel.MergeConfirmedBranches(
                [],
                MainWindowViewModel.SnapshotModUpdates([recoveredItem]))
            .Single().Branch == ServerBranchChannel.Dev,
        "A product the confirmation never saw must use the branch currently selected for it.");

    // --- Automatic launcher-version system sync -----------------------------------------------

    Assert(MainWindowViewModel.ShouldSyncLauncherVersion(null, LauncherConstants.LauncherVersion)
           && MainWindowViewModel.ShouldSyncLauncherVersion(string.Empty, LauncherConstants.LauncherVersion)
           && MainWindowViewModel.ShouldSyncLauncherVersion("3.3.4", "3.3.5"),
        "A missing, empty, or stale marker must run the automatic system sync.");
    Assert(!MainWindowViewModel.ShouldSyncLauncherVersion("3.3.5\n", "3.3.5")
           && !MainWindowViewModel.ShouldSyncLauncherVersion("3.3.5", "3.3.5"),
        "A recorded launcher version must stop the automatic sync from running every launch.");

    Assert(MainWindowViewModel.SanitizeLauncherSyncVersion(" 3.3.5 ") == "3.3.5"
           && MainWindowViewModel.SanitizeLauncherSyncVersion(LauncherConstants.LauncherVersion) == LauncherConstants.LauncherVersion,
        "A plain dotted version must survive sanitizing so the marker can be written.");
    Assert(MainWindowViewModel.SanitizeLauncherSyncVersion(null) is null
           && MainWindowViewModel.SanitizeLauncherSyncVersion("3.3.2; rm -rf /") is null
           && MainWindowViewModel.SanitizeLauncherSyncVersion("3.3.2'") is null
           && MainWindowViewModel.SanitizeLauncherSyncVersion("$(id)") is null,
        "Nothing but digits and dots may reach the launcher sync marker command.");

    var syncMarkerRead = MainWindowViewModel.BuildLauncherSyncMarkerReadCommand();
    Assert(syncMarkerRead.Contains("cat /home/dwemer/.launcher_synced_version", StringComparison.Ordinal)
           && syncMarkerRead.Contains("|| true", StringComparison.Ordinal),
        "A missing sync marker must read as empty rather than as a distro failure.");
    var syncMarkerWrite = MainWindowViewModel.BuildLauncherSyncMarkerWriteCommand(LauncherConstants.LauncherVersion);
    Assert(syncMarkerWrite.Contains($"printf '%s' '{LauncherConstants.LauncherVersion}'", StringComparison.Ordinal)
           && syncMarkerWrite.EndsWith("> /home/dwemer/.launcher_synced_version", StringComparison.Ordinal),
        "A successful sync must persist the launcher version inside the distro.");
    var markerWriteRejected = false;
    try
    {
        MainWindowViewModel.BuildLauncherSyncMarkerWriteCommand("3.3.2; touch /tmp/pwned");
    }
    catch (ArgumentException)
    {
        markerWriteRejected = true;
    }

    Assert(markerWriteRejected, "A version carrying shell syntax must never build a marker command.");

    // --- Quickstart mod choices -------------------------------------------------------------

    var chimProfile = gameCatalog.First(game => game.Key == "CHIM");
    var choice = new QuickstartProductViewModel(
        chimProfile, ServerProduct.Herika, _ => Task.CompletedTask, _ => { });

    Assert(!choice.IsSelected, "No mod may be selected by default in Choose Your Mods.");
    Assert(choice.ArtworkSource == chimProfile.RailImageSource
           && choice.ArtworkSource.StartsWith("pack://application", StringComparison.Ordinal),
        "Choose Your Mods must reuse the local rail artwork rather than fetching anything.");

    Assert(!choice.IsSelectable && !choice.IsEligibleForInstall && choice.StatusText == "Checking",
        "A mod whose status has not been read yet must not be selectable for install.");
    choice.IsSelected = true;
    Assert(!choice.IsSelected,
        "A tick must not stick on a mod whose install status is still unknown.");

    choice.ApplyStatus(ServerInstallState.NotInstalled);
    choice.IsSelected = true;
    Assert(choice.IsSelected && choice.IsSelectable && choice.StatusText == "Not installed",
        "A missing mod must be selectable and must say so.");
    Assert(!FirstRunSetupViewModel.CanAdvanceFromProductSelection(new[] { choice }),
        "Quickstart must not silently advance past a selected mod that was never installed.");

    choice.ApplyStatus(ServerInstallState.Unknown);
    Assert(!choice.IsSelected && !choice.IsSelectable && choice.StatusText == "Status unknown",
        "A refresh that cannot confirm the status must drop the stale tick and lock the row.");
    choice.IsSelected = true;
    Assert(!choice.IsSelected,
        "A row whose status refresh failed must not be re-selectable.");

    choice.ApplyStatus(ServerInstallState.NeedsRepair);
    Assert(!choice.IsSelectable && !choice.IsInstalled && choice.StatusText == "Needs repair",
        "A damaged mod must not be offered for a Quickstart install.");

    choice.ApplyStatus(ServerInstallState.NotInstalled);
    Assert(choice.IsSelectable && choice.StatusText == "Not installed",
        "A row must become selectable again once the status probe recovers.");

    choice.ApplyStatus(ServerInstallState.Installed);
    Assert(choice.StatusText == "Installed" && !choice.IsSelectable && !choice.IsSelected,
        "An already-installed mod must show Installed, drop out of the selection, and stay locked.");
    choice.IsSelected = true;
    Assert(!choice.IsSelected,
        "An installed mod must not be re-selectable for install from Quickstart.");

    var failing = new QuickstartProductViewModel(
        gameCatalog.First(game => game.Key == "STOBE"), ServerProduct.Stobe, _ => Task.CompletedTask, _ => { });
    failing.ApplyStatus(ServerInstallState.NotInstalled);
    failing.IsSelected = true;
    failing.SetInstallState(QuickstartProductInstallState.Failed, "git clone failed");
    Assert(failing.ShowRetry && failing.StatusText == "Failed" && failing.ResultDetail == "git clone failed",
        "A failed install must offer retry and explain what went wrong.");
    Assert(failing.ToInstallResultKey() == "failed",
        "A failed install must be recorded as failed in onboarding state.");

    failing.SetInstallState(QuickstartProductInstallState.Skipped, null);
    Assert(!failing.ShowRetry && failing.StatusText == "Skipped" && failing.ToInstallResultKey() == "skipped",
        "Skipping a failed mod must clear the retry prompt and be recorded as skipped.");
    Assert(FirstRunSetupViewModel.CanAdvanceFromProductSelection(new[] { failing }),
        "Quickstart may advance after the user explicitly skips a failed selection.");

    var succeeded = new QuickstartProductViewModel(
        gameCatalog.First(game => game.Key == "DIALECTIC"), ServerProduct.Dialectic, _ => Task.CompletedTask, _ => { });
    succeeded.ApplyStatus(ServerInstallState.NotInstalled);
    succeeded.IsSelected = true;
    succeeded.SetInstallState(QuickstartProductInstallState.Installed);
    Assert(succeeded.IsInstalled && !succeeded.IsSelectable && !succeeded.IsSelected
           && succeeded.ToInstallResultKey() == "installed",
        "A mod that installed before a later failure must clear its selection, stay installed, and remain locked.");
    Assert(FirstRunSetupViewModel.CanAdvanceFromProductSelection(new[] { succeeded }),
        "Quickstart may advance after every selected mod installs successfully.");

    var stillFailing = new QuickstartProductViewModel(
        gameCatalog.First(game => game.Key == "STOBE"), ServerProduct.Stobe, _ => Task.CompletedTask, _ => { });
    stillFailing.SetInstallState(QuickstartProductInstallState.Failed, "git clone failed");
    Assert(FirstRunSetupViewModel.BuildProductInstallSummary(new[] { succeeded, stillFailing })
            .Contains("retry or skip", StringComparison.OrdinalIgnoreCase),
        "A partly failed install run must tell the user that retry and skip are available.");
    Assert(FirstRunSetupViewModel.BuildProductInstallSummary(new[] { succeeded })
            == "Installed 1 of 1 mods.",
        "A clean install run must report plainly without a failure hint.");

    // --- Quickstart install guard ------------------------------------------------------------

    // Every product starts locked, so a status the launcher never managed to read cannot be
    // installed over from Quickstart.
    foreach (var product in new[] { ServerProduct.Herika, ServerProduct.Stobe, ServerProduct.Dialectic })
    {
        var unread = new QuickstartProductViewModel(
            gameCatalog.First(game => ServerManagementService.TryParseGameKey(game.Key) == product),
            product,
            _ => Task.CompletedTask,
            _ => { });
        Assert(!unread.IsEligibleForInstall && !unread.IsSelectable,
            $"{product} must not be installable before its status has been read.");
    }

    Assert(FirstRunSetupViewModel.ResolveReportedState(
            ServerStatusResult.Failed("wsl unavailable"), ServerProduct.Herika) == ServerInstallState.Unknown,
        "A failed status read must resolve to Unknown, never to not-installed.");
    Assert(FirstRunSetupViewModel.ResolveReportedState(null, ServerProduct.Herika) == ServerInstallState.Unknown,
        "A missing status result must resolve to Unknown.");
    Assert(FirstRunSetupViewModel.ResolveReportedState(
            ServerStatusResult.Succeeded(new ServerStatusSnapshot(
                ServerManagementService.SupportedSchemaVersion, Array.Empty<ServerStatus>())),
            ServerProduct.Stobe) == ServerInstallState.Unknown,
        "A product missing from the status document must resolve to Unknown, not to absent.");

    Assert(ServerManagementService.TryParseStatus(
        "{\"schema_version\":1,\"servers\":[{\"product\":\"stobe\",\"state\":\"who-knows\"}]}",
        out var malformedSnapshot,
        out _), "The unknown state fixture must be a valid status document.");
    Assert(FirstRunSetupViewModel.ResolveReportedState(
            ServerStatusResult.Succeeded(malformedSnapshot!), ServerProduct.Stobe) == ServerInstallState.Unknown,
        "A state string this build cannot parse must resolve to Unknown.");

    var installCalls = 0;
    Func<Task<CommandResult>> countingInstall = () =>
    {
        installCalls++;
        return Task.FromResult(new CommandResult(0, string.Empty, string.Empty));
    };

    // All products must refuse incomplete/unknown answers at the actual install boundary.
    foreach (var product in Enum.GetValues<ServerProduct>())
    {
        var blockedRow = new QuickstartProductViewModel(
            gameCatalog.First(game => ServerManagementService.TryParseGameKey(game.Key) == product),
            product, _ => Task.CompletedTask, _ => { });
        foreach (var answer in new[]
        {
            ServerStatusResult.Succeeded(new ServerStatusSnapshot(1, Array.Empty<ServerStatus>())),
            ServerStatusResult.Succeeded(malformedSnapshot!),
            StatusFor(product, (ServerInstallState)999),
            StatusFor(product, ServerInstallState.NeedsRepair),
            StatusFor(product, ServerInstallState.Installed)
        })
        {
            blockedRow.ResetInstallState();
            blockedRow.ApplyStatus(ServerInstallState.NotInstalled);
            blockedRow.IsSelected = true;
            await FirstRunSetupViewModel.InstallProductGuardedAsync(
                blockedRow, _ => Task.FromResult(answer), countingInstall, _ => { });
            Assert(installCalls == 0 && !blockedRow.IsSelected,
                $"{product}: missing, unknown, repair and installed answers must never invoke install.");
        }
    }

    var guarded = new QuickstartProductViewModel(
        gameCatalog.First(game => game.Key == "CHIM"), ServerProduct.Herika,
        row => FirstRunSetupViewModel.InstallProductGuardedAsync(
            row, _ => Task.FromResult(StatusFor(row.Product, ServerInstallState.NotInstalled)),
            countingInstall, _ => { }), _ => { });
    guarded.ApplyStatus(ServerInstallState.NotInstalled);
    guarded.IsSelected = true;

    // Batch path: the row was ticked while the status was readable, then the pre-install check fails.
    await FirstRunSetupViewModel.InstallProductGuardedAsync(
        guarded,
        _ => Task.FromResult(ServerStatusResult.Failed("wsl unavailable")),
        countingInstall,
        _ => { });
    Assert(installCalls == 0,
        "A stale ticked row must not reach the install command when the pre-install status check fails.");
    Assert(guarded.InstallState == QuickstartProductInstallState.Failed && guarded.ShowRetry,
        "A blocked install must land on Failed so Retry and Skip stay available.");
    Assert(guarded.ResultDetail.Contains("Refresh installed mods", StringComparison.OrdinalIgnoreCase),
        "A blocked install must tell the user to refresh rather than failing silently.");
    Assert(!guarded.IsSelected,
        "A blocked install must clear the tick that the unreadable status can no longer justify.");

    // Retry path: same seam, and a mod that needs repair is sent to the Mods page instead.
    await FirstRunSetupViewModel.InstallProductGuardedAsync(
        guarded,
        _ => Task.FromResult(StatusFor(ServerProduct.Herika, ServerInstallState.NeedsRepair)),
        countingInstall,
        _ => { });
    Assert(installCalls == 0,
        "Retry must not issue an install for a mod the manager reports as needing repair.");
    Assert(guarded.InstallState == QuickstartProductInstallState.Failed && guarded.ShowRetry
           && guarded.ResultDetail.Contains("Mods page", StringComparison.OrdinalIgnoreCase),
        "A needs-repair mod must keep Retry available and point at the Mods page.");

    // Retry recovers once the manager answers not-installed again.
    Assert(guarded.RetryCommand.CanExecute(null), "Retry must be enabled after a guard refusal.");
    guarded.RetryCommand.Execute(null);
    Assert(installCalls == 1 && guarded.InstallState == QuickstartProductInstallState.Installed
           && guarded.IsInstalled,
        "Retry must install once the status probe recovers and reports the mod as not installed.");

    var alreadyInstalled = new QuickstartProductViewModel(
        gameCatalog.First(game => game.Key == "DIALECTIC"), ServerProduct.Dialectic, _ => Task.CompletedTask, _ => { });
    alreadyInstalled.ApplyStatus(ServerInstallState.NotInstalled);
    alreadyInstalled.IsSelected = true;
    await FirstRunSetupViewModel.InstallProductGuardedAsync(
        alreadyInstalled,
        _ => Task.FromResult(StatusFor(ServerProduct.Dialectic, ServerInstallState.Installed)),
        countingInstall,
        _ => { });
    Assert(installCalls == 1,
        "A mod that became installed between selection and install must not be installed over.");
    Assert(alreadyInstalled.InstallState == QuickstartProductInstallState.Installed
           && alreadyInstalled.ResultDetail.Contains("already installed", StringComparison.OrdinalIgnoreCase),
        "An already-installed mod must be recorded as installed and explained, not reported as a failure.");

    var throwingStatus = new QuickstartProductViewModel(
        gameCatalog.First(game => game.Key == "STOBE"), ServerProduct.Stobe, _ => Task.CompletedTask, _ => { });
    throwingStatus.ApplyStatus(ServerInstallState.NotInstalled);
    await FirstRunSetupViewModel.InstallProductGuardedAsync(
        throwingStatus,
        _ => throw new InvalidOperationException("status probe crashed"),
        countingInstall,
        _ => { });
    Assert(installCalls == 1 && throwingStatus.InstallState == QuickstartProductInstallState.Failed,
        "A status probe that throws must block the install instead of falling through to it.");
    Console.WriteLine("Quickstart guard: initial/stale selection, all-product blocked callbacks, Retry recovery and throwing status: OK");

    // --- onboarding schema version 2 ---------------------------------------------------------

    var v2StatePath = Path.Combine(root, "onboarding-v2.json");
    var v2Onboarding = new OnboardingStateService(v2StatePath);
    await v2Onboarding.MarkCompletedAsync(
        SetupPresetKey.NvidiaGpu,
        "pockettts",
        false,
        true,
        new[] { "herika", "stobe" },
        new Dictionary<string, string> { ["herika"] = "installed", ["stobe"] = "failed", ["dialectic"] = "pending" });

    var v2State = await v2Onboarding.LoadAsync();
    Assert(v2State.Version == OnboardingStateService.CurrentVersion && v2State.Version == 2,
        "Completed onboarding must be written at schema version 2.");
    Assert(v2State.SelectedProducts is not null
           && v2State.SelectedProducts.SequenceEqual(new[] { "herika", "stobe" }),
        "Schema version 2 must record which products the user chose.");
    Assert(v2State.ProductInstallResults is not null
           && v2State.ProductInstallResults["herika"] == "installed"
           && v2State.ProductInstallResults["stobe"] == "failed"
           && v2State.ProductInstallResults["dialectic"] == "pending",
        "Schema version 2 must record each product's install outcome.");
    Assert(!await FirstRunSetupViewModel.ShouldShowFirstRunSetupAsync(default, v2Onboarding),
        "A completed version 2 setup must not reopen QuickStart.");

    await v2Onboarding.MarkSkippedAsync(SetupPresetKey.AmdCpu, Array.Empty<string>(), new Dictionary<string, string>());
    var v2Skipped = await v2Onboarding.LoadAsync();
    Assert(v2Skipped.Version == 2 && v2Skipped.Skipped && !v2Skipped.Completed,
        "Skipping must also be written at schema version 2.");
    Assert(!await FirstRunSetupViewModel.ShouldShowFirstRunSetupAsync(default, v2Onboarding),
        "A skipped version 2 setup must not reopen QuickStart.");

    // A version 1 file written by an older launcher must still be honoured exactly as written.
    var v1CompletedPath = Path.Combine(root, "onboarding-v1-completed.json");
    File.WriteAllText(v1CompletedPath, """
    {
      "Version": 1,
      "Completed": true,
      "CompletedAtUtc": "2026-01-01T00:00:00+00:00",
      "SelectedPreset": "NvidiaGpu",
      "VoiceEngine": "xtts",
      "HuggingFaceConfigured": true
    }
    """);
    var v1Onboarding = new OnboardingStateService(v1CompletedPath);
    var v1State = await v1Onboarding.LoadAsync();
    Assert(v1State.Version == 1 && v1State.Completed && v1State.SelectedPreset == "NvidiaGpu",
        "A version 1 onboarding file must be read back unchanged, still at version 1.");
    Assert(v1State.SelectedProducts is null && v1State.ProductInstallResults is null,
        "A version 1 file must not invent product keys it never stored.");
    Assert(!await FirstRunSetupViewModel.ShouldShowFirstRunSetupAsync(default, v1Onboarding),
        "An existing version 1 completed setup must never reopen QuickStart after the schema bump.");

    var v1SkippedPath = Path.Combine(root, "onboarding-v1-skipped.json");
    File.WriteAllText(v1SkippedPath, """
    {
      "Version": 1,
      "Skipped": true,
      "SkippedAtUtc": "2026-01-01T00:00:00+00:00",
      "SelectedPreset": "AmdCpu"
    }
    """);
    var v1SkippedOnboarding = new OnboardingStateService(v1SkippedPath);
    var v1SkippedState = await v1SkippedOnboarding.LoadAsync();
    Assert(v1SkippedState.Version == 1 && v1SkippedState.Skipped && !v1SkippedState.Completed,
        "A version 1 skipped file must be read back unchanged.");
    Assert(!await FirstRunSetupViewModel.ShouldShowFirstRunSetupAsync(default, v1SkippedOnboarding),
        "An existing version 1 skipped setup must never reopen QuickStart after the schema bump.");

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

static bool Throws(Action action)
{
    try
    {
        action();
        return false;
    }
    catch (ArgumentOutOfRangeException)
    {
        return true;
    }
}

static ServerStatusResult StatusFor(ServerProduct product, ServerInstallState state)
{
    return ServerStatusResult.Succeeded(new ServerStatusSnapshot(
        ServerManagementService.SupportedSchemaVersion,
        new[]
        {
            new ServerStatus(
                product,
                state,
                ServerRepositoryState.Unknown,
                DatabasePresent: null,
                Root: null,
                Database: null,
                Branch: null,
                Version: null,
                ProductionBranch: null,
                DevelopmentBranch: null,
                Port: null)
        }));
}

static void Assert(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}
