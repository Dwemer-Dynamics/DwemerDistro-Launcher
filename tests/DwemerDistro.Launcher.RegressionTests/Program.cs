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
    Assert(LauncherConstants.LauncherVersion == "3.3.1", "Launcher constants must report version 3.3.1.");

    var gameCatalog = GameProfile.CreateCatalog();
    Assert(gameCatalog.Count == 3 && gameCatalog.Select(game => game.Key).Distinct().Count() == 3,
        "The launcher rail must expose exactly three unique game profiles.");
    Assert(gameCatalog.All(game => game.HeroImageSource.EndsWith("-hero.jpg", StringComparison.Ordinal)
                                   && game.RailImageSource.EndsWith("-rail.jpg", StringComparison.Ordinal)),
        "Every game profile must use local hero and rail artwork.");

    var keyedPreferences = MainWindowViewModel.ParseUpdateIncludeSettings(
        "herika=0\nstobe=\ndialectic=1\n");
    Assert(!keyedPreferences.Herika && keyedPreferences.Stobe && keyedPreferences.Dialectic,
        "An empty update preference must not shift the following game's value.");

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

    herikaItem.ApplyVersionStatus("aiagent | 01-01-2026 | 1.2.3", "LimeGreen");
    herikaItem.ApplyStatus(stobeStatus with { Product = ServerProduct.Herika });
    Assert(herikaItem.ShowNotInstalledActions && !herikaItem.ShowInstalledActions && !herikaItem.ShowRepairActions,
        "A not-installed product must show only the install branch and Install Server.");
    Assert(herikaItem.StatusText == "Not installed"
           && herikaItem.StatusColor == ServerManagerItemViewModel.NotInstalledColor,
        "A not-installed product must read Not installed in the neutral grey, not the version colours.");
    Assert(!herikaItem.CanUseInstalledFeatures && herikaItem.CanInstall && !herikaItem.CanUninstall,
        "Webpage, rollback and the update checkbox must stay unreachable while nothing is installed.");
    Assert(!herikaItem.CanUpdate,
        "The single-product update must never be offered for a product that is not installed.");

    herikaItem.ApplyStatus(herikaStatus);
    Assert(herikaItem.ShowInstalledActions && !herikaItem.ShowNotInstalledActions && !herikaItem.ShowRepairActions,
        "An installed product must show the branch, webpage, rollback and uninstall actions.");
    Assert(herikaItem.StatusText == "aiagent | 01-01-2026 | 1.2.3" && herikaItem.StatusColor == "LimeGreen",
        "An installed product must keep the existing green/yellow version status semantics.");
    Assert(herikaItem.CanUseInstalledFeatures && herikaItem.CanUninstall && !herikaItem.CanInstall,
        "An installed product must offer uninstall but never a second install.");
    Assert(herikaItem.CanUpdate && herikaItem.UpdateCommand.CanExecute(null),
        "An installed, idle product must offer its own update action.");
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
        "A running Update Mods sweep or sibling server operation must disable the single-product update.");
    Assert(herikaItem.UpdateActionHelpText.Contains("unavailable", StringComparison.OrdinalIgnoreCase),
        "The disabled update action must say why it is unavailable, not just dim.");
    Assert(!herikaItem.CanUninstall && !herikaItem.UninstallCommand.CanExecute(null)
           && !herikaItem.CanUseInstalledFeatures && herikaItem.ShowInstalledActions,
        "Conflicting operations must disable lifecycle actions without removing their controls.");

    herikaItem.IsConflictingOperationRunning = false;
    Assert(herikaItem.CanUpdate && herikaItem.UpdateActionHelpText.Contains("Main", StringComparison.Ordinal)
           && herikaItem.UpdateActionHelpText.Contains("HerikaServer", StringComparison.Ordinal),
        "The enabled update action must name the one server and the branch it will use.");

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

    // --- Updates checkbox gates the single-product update ------------------------------------

    Assert(individualUpdateItem.IsIncludedInUpdates,
        "A product must start included in updates, so the button matches the checkbox default.");

    individualUpdateItem.IsIncludedInUpdates = false;
    Assert(!individualUpdateItem.CanUpdate && !individualUpdateItem.UpdateCommand.CanExecute(null),
        "Clearing a product's Updates checkbox must disable that product's own Update button.");
    individualUpdateItem.UpdateCommand.Execute(null);
    Assert(individualUpdateCount == 1,
        "An excluded product's update must not run even when its command is invoked directly.");
    Assert(individualUpdateItem.UpdateActionHelpText.Contains("Updates checkbox", StringComparison.Ordinal)
           && individualUpdateItem.UpdateActionHelpText.Contains("STOBE", StringComparison.Ordinal),
        "The disabled update action must tell the user to enable that product's Updates checkbox.");
    Assert(individualUpdateItem.CanUninstall && individualUpdateItem.ShowInstalledActions
           && individualUpdateItem.StatusColor != ServerManagerItemViewModel.ErrorColor,
        "The Updates checkbox must gate only the update action, not the rest of the lifecycle.");

    individualUpdateItem.IsIncludedInUpdates = true;
    Assert(individualUpdateItem.CanUpdate && individualUpdateItem.UpdateCommand.CanExecute(null),
        "Re-checking the Updates checkbox must re-enable the update button for an installed idle product.");
    individualUpdateItem.UpdateCommand.Execute(null);
    Assert(individualUpdateCount == 2,
        "A re-included product's update must run again as soon as the checkbox is restored.");

    // A sibling operation keeps priority over the checkbox explanation.
    individualUpdateItem.IsIncludedInUpdates = false;
    individualUpdateItem.IsConflictingOperationRunning = true;
    Assert(!individualUpdateItem.CanUpdate
           && individualUpdateItem.UpdateActionHelpText.Contains("another server, component, or system operation", StringComparison.Ordinal),
        "A running sweep must still explain itself, even for a product excluded from updates.");
    individualUpdateItem.IsConflictingOperationRunning = false;
    Assert(!individualUpdateItem.CanUpdate
           && individualUpdateItem.UpdateActionHelpText.Contains("Updates checkbox", StringComparison.Ordinal),
        "Once the transient blocks clear, an excluded product must still explain the checkbox.");
    individualUpdateItem.IsIncludedInUpdates = true;

    Assert(ServerManagerItemViewModel.MapBranchToChannel("aiagent", "aiagent", "dev") == ServerBranchChannel.Main
           && ServerManagerItemViewModel.MapBranchToChannel("dev", "aiagent", "dev") == ServerBranchChannel.Dev
           && ServerManagerItemViewModel.MapBranchToChannel("unstable", null, null) == ServerBranchChannel.Dev
           && ServerManagerItemViewModel.MapBranchToChannel("feature/x", "aiagent", "dev") is null
           && ServerManagerItemViewModel.MapBranchToChannel(null, "aiagent", "dev") is null,
        "Branch mapping must prefer the manager's reported branch names and leave anything unknown alone.");

    // --- Update Mods gating -----------------------------------------------------------------

    Assert(MainWindowViewModel.ShouldUpdateProduct(ServerInstallState.Installed, includeInUpdates: true),
        "An installed product with updates enabled must be updated.");
    Assert(!MainWindowViewModel.ShouldUpdateProduct(ServerInstallState.Installed, includeInUpdates: false),
        "An installed product with updates disabled must be left alone.");
    Assert(!MainWindowViewModel.ShouldUpdateProduct(ServerInstallState.NotInstalled, includeInUpdates: true)
           && !MainWindowViewModel.ShouldUpdateProduct(ServerInstallState.NeedsRepair, includeInUpdates: true)
           && !MainWindowViewModel.ShouldUpdateProduct(ServerInstallState.Unknown, includeInUpdates: true),
        "Update must never reach a product that is not installed, whatever the update checkbox says.");

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
        "Update System must bootstrap Git metadata for a freshly installed empty distro.");
    Assert(systemUpdateCommand.Contains("git fetch origin && git reset --hard origin/main", StringComparison.Ordinal)
           && systemUpdateCommand.EndsWith(sharedUpdateCommand, StringComparison.Ordinal),
        "Update System must update the distro checkout before running the server-free shared component update.");

    var modsUpdateConfirmation = MainWindowViewModel.BuildModsUpdateConfirmation([herikaItem, individualUpdateItem]);
    Assert(modsUpdateConfirmation.Contains("selected installed mods", StringComparison.Ordinal)
           && modsUpdateConfirmation.Contains("DwemerDistro and shared components first", StringComparison.Ordinal),
        "Update Mods must clearly include the system update before the selected mods.");
    Assert(modsUpdateConfirmation.Contains($"{herikaItem.DisplayName} target branch", StringComparison.Ordinal)
           && modsUpdateConfirmation.Contains($"{individualUpdateItem.DisplayName} target branch", StringComparison.Ordinal),
        "Update Mods must list every selected installed mod and its target branch.");

    var systemUpdateConfirmation = MainWindowViewModel.BuildSystemUpdateConfirmation();
    Assert(systemUpdateConfirmation.Contains("DwemerDistro and its shared components", StringComparison.Ordinal)
           && systemUpdateConfirmation.Contains("Installed mods will not be changed", StringComparison.Ordinal),
        "Update System must clearly exclude every installed mod server.");

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
        [herikaItem, individualUpdateItem],
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
        Assert(await MainWindowViewModel.UpdateInstalledServersAsync([item],
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
            var batchSucceeded = await MainWindowViewModel.UpdateInstalledServersAsync([herikaItem, individualUpdateItem],
                () => throwSystemError ? Task.FromException<bool>(new IOException("system failure")) : Task.FromResult(false),
                (_, _) => { skippedModCalls++; return Task.FromResult(true); });
            Assert(!batchSucceeded, "A failed shared update must fail the batch.");
        }
        catch (IOException) when (throwSystemError) { }
    }
    Assert(skippedModCalls == 0, "System failure or exception must prevent every mod update.");

    herikaItem.IsIncludedInUpdates = false;
    individualUpdateItem.ApplyStatus(stobeStatus with { State = ServerInstallState.NotInstalled });
    Assert(!await MainWindowViewModel.UpdateInstalledServersAsync([herikaItem, individualUpdateItem],
            () => throw new InvalidOperationException("An empty eligible selection must not update the system."),
            (_, _) => throw new InvalidOperationException("An unchecked or missing mod must not update.")),
        "No eligible mods must remain a no-op, including the system stage.");
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

    // --- Quickstart mod choices -------------------------------------------------------------

    var chimProfile = gameCatalog.First(game => game.Key == "CHIM");
    var choice = new QuickstartProductViewModel(
        chimProfile, ServerProduct.Herika, _ => Task.CompletedTask, _ => { });

    Assert(!choice.IsSelected, "No mod may be selected by default in Choose Your Mods.");
    Assert(choice.ArtworkSource == chimProfile.RailImageSource
           && choice.ArtworkSource.StartsWith("pack://application", StringComparison.Ordinal),
        "Choose Your Mods must reuse the local rail artwork rather than fetching anything.");

    choice.ApplyInstalledState(isInstalled: false, isStatusKnown: true);
    choice.IsSelected = true;
    Assert(choice.IsSelected && choice.IsSelectable && choice.StatusText == "Not installed",
        "A missing mod must be selectable and must say so.");
    Assert(!FirstRunSetupViewModel.CanAdvanceFromProductSelection(new[] { choice }),
        "Quickstart must not silently advance past a selected mod that was never installed.");

    choice.ApplyInstalledState(isInstalled: true, isStatusKnown: true);
    Assert(choice.StatusText == "Installed" && !choice.IsSelectable && !choice.IsSelected,
        "An already-installed mod must show Installed, drop out of the selection, and stay locked.");
    choice.IsSelected = true;
    Assert(!choice.IsSelected,
        "An installed mod must not be re-selectable for install from Quickstart.");

    var failing = new QuickstartProductViewModel(
        gameCatalog.First(game => game.Key == "STOBE"), ServerProduct.Stobe, _ => Task.CompletedTask, _ => { });
    failing.ApplyInstalledState(isInstalled: false, isStatusKnown: true);
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

static void Assert(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}
