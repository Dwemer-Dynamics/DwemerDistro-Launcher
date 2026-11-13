using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using DwemerDistro.Launcher.Wpf.Models;

namespace DwemerDistro.Launcher.Wpf.Services;

/// <summary>
/// One answer from <c>ddistro_trixie_maintenance ... --json</c>: the last non-empty output line, a JSON object with
/// <c>"schema": 1</c>. Unknown fields are ignored; a missing field reads as empty or null, which no check accepts as
/// proof. <see cref="InstanceId"/> and <see cref="Codename"/> corroborate when Core reports them and are never
/// required alone.
/// </summary>
internal sealed record TrixieCoreReply(
    string Result,
    string State,
    string Release,
    string Codename,
    string InstanceId,
    string PlanId,
    string CaptureToken,
    bool? Fence,
    long? UsedBytes,
    long? UpgradeBytes,
    string Reasons)
{
    /// <summary>
    /// What <c>release</c> and <c>upgrade --cancel</c> report of starting again what ran before the fence; null from a
    /// Core that predates it, which is then accepted as before.
    /// </summary>
    public TrixieCoreRestore? Restore { get; init; }
    /// <summary><c>status</c>: Core's journal still names services it has to start again once its fence lifted.</summary>
    public bool RestorePending { get; init; }
}

/// <summary>Core's journaled restore: PG17 and the writers that ran when it fenced, what started, what failed and why not.</summary>
internal sealed record TrixieCoreRestore(bool Pending, bool Pg17, string[] Services, string[] Running, string[] Failed, string Problems);

/// <summary>A WSL registration, read from HKCU without starting WSL. The disk is <c>BasePath\VhdFileName</c>.</summary>
internal sealed record TrixieRegistration(string Name, string Id, string BasePath, string VhdFileName, string? DiskFileId)
{
    [JsonIgnore] public string DiskPath => Path.Combine(BasePath, VhdFileName);
}

internal enum TrixieMaintenanceOutcome
{
    /// <summary>Rollout is off and no journal exists: the launcher behaves exactly as before.</summary>
    NotActive,
    /// <summary>Nothing to do: already Trixie, already completed, unsupported, or no tool shipped.</summary>
    NotNeeded,
    Completed,
    /// <summary>Postponed (a game, the server, another operation or a lock). Nothing new was changed.</summary>
    Deferred,
    /// <summary>Refused or cancelled before any package changed; Core's fence, if any, was released.</summary>
    NotStarted,
    /// <summary>A failed upgrade was replaced by its proven recovery copy, which booted and was released by Core.</summary>
    Restored,
    /// <summary>The distro's state can't be proven, or it is past the gameplay-write boundary. Writes stay blocked.</summary>
    RecoveryRequired
}

/// <param name="LeavesWritesBlocked">The journal still blocks distro writers after this run, so nothing may record a release.</param>
internal sealed record TrixieMaintenanceResult(TrixieMaintenanceOutcome Outcome, string Message, bool LeavesWritesBlocked = false)
{
    public bool AllowsUpdateToFinish => !LeavesWritesBlocked;
}

internal enum TrixieJournalPhase
{
    /// <summary>About to run <c>upgrade --prepare</c>; Core may be fencing.</summary>
    Preparing,
    /// <summary>Core fenced the distro; the capture token is protected in the journal.</summary>
    Fenced,
    Exporting,
    Exported,
    /// <summary>Importing and booting the isolated recovery copy.</summary>
    ProvingCopy,
    /// <summary>The recovery copy booted and holds Core's fence, token and PG17 seal.</summary>
    CopyProven,
    Upgrading,
    Upgraded,
    /// <summary>Past the gameplay-write boundary: Core's release was requested, so nothing is restored automatically.</summary>
    Releasing,
    Completed,
    Cancelling,
    /// <summary>Ended with nothing changed. Terminal and non-blocking; retried later.</summary>
    Cancelled,
    /// <summary>Swapping the failed disk and the proven recovery disk, then cancelling Core's fence on the recovered distro.</summary>
    Restoring,
    /// <summary>The recovery copy is serving again with the fence released. Terminal and non-blocking.</summary>
    Restored,
    RecoveryRequired,
    /// <summary>The journal's registration no longer exists under the distro name. Terminal and non-blocking.</summary>
    Superseded
}

internal sealed record TrixieJournal
{
    public int Schema { get; init; } = TrixieJournalStore.SchemaVersion;
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public TrixieJournalPhase Phase { get; init; }
    public TrixieRegistration Registration { get; init; } = new("", "", "", "", null);
    public string? InstanceId { get; init; }
    public string? ToolSha256 { get; init; }
    public string? PlanId { get; init; }
    /// <summary>Core's capture token, DPAPI-protected for this Windows user and registration. Never logged.</summary>
    public string? ProtectedToken { get; init; }
    /// <summary>Core's PG17 <c>pg_control</c> seal read from its journal after the fence.</summary>
    public string[]? Seal { get; init; }
    /// <summary>Bytes the distro uses, from preflight; sizes the space check and the export's plausibility check.</summary>
    public long UsedBytes { get; init; }
    public string? BackupPath { get; init; }
    public long BackupBytes { get; init; }
    public string? BackupSha256 { get; init; }
    public TrixieRegistration? Recovery { get; init; }
    /// <summary>The recovery copy booted and held Core's fence, token and PG17 seal.</summary>
    public bool CopyVerified { get; init; }
    public int UpgradeAttempts { get; init; }
    public int RestoreAttempts { get; init; }
    /// <summary>Restore: 0 not started; 1 to 3 that rename is under way (failed disk aside, recovery disk in place, failed disk to the copy); 4 done.</summary>
    public int SwapStep { get; init; }
    public string? SwapAsidePath { get; init; }
    /// <summary>Irrevocable: once true for a registration, no write may clear it and nothing is restored automatically.</summary>
    public bool GameplayWriteBoundary { get; init; }
    /// <summary>
    /// Core's cancel lifted its fence on the disk this attempt now runs, so writers may have run there: no disk is swapped
    /// any more, and an unfinished restart of its services is only retried with <c>upgrade --cancel</c>.
    /// </summary>
    public bool FenceLifted { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
    public string? Note { get; init; }
}

/// <summary>
/// The Windows-side record of the Bookworm to Trixie upgrade, beside the other launcher state. Unlike update
/// preferences it fails closed: a journal that exists but can't be read blocks distro writers and is never
/// overwritten. Every write is flushed before it replaces the previous file, and a set gameplay-write boundary can't
/// be cleared. <see cref="TryLock"/> keeps a second launcher from running the step at the same time.
/// </summary>
internal sealed class TrixieJournalStore(string path)
{
    internal const int SchemaVersion = 2;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public string StatePath { get; } = path;

    internal static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DwemerDistro", "system-upgrade-journal.json");

    /// <returns><c>Unreadable</c> is true when the file exists but is not a journal this build understands.</returns>
    public (TrixieJournal? Journal, bool Unreadable) Read()
    {
        try
        {
            if (!File.Exists(StatePath)) return (null, false);
            var journal = JsonSerializer.Deserialize<TrixieJournal>(File.ReadAllText(StatePath), JsonOptions);
            return journal is { Schema: SchemaVersion, Registration.Id.Length: > 0 } && Enum.IsDefined(journal.Phase)
                ? (journal, false)
                : (null, true);
        }
        catch
        {
            return (null, true);
        }
    }

    /// <summary>Throws when the journal can't be saved; the caller must not take the next step then.</summary>
    public void Write(TrixieJournal journal)
    {
        var existing = Read();
        if (existing.Unreadable) throw new IOException("The system upgrade journal can't be read, so it is not overwritten.");
        if (existing.Journal is { GameplayWriteBoundary: true } previous && !journal.GameplayWriteBoundary
            && string.Equals(previous.Registration.Id, journal.Registration.Id, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The gameplay-write boundary of the system upgrade can't be cleared.");

        var directory = Path.GetDirectoryName(StatePath)!;
        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(directory, $"system-upgrade-journal.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                stream.Write(JsonSerializer.SerializeToUtf8Bytes(journal, JsonOptions));
                stream.Flush(flushToDisk: true);
            }

            TrixieWindowsFiles.ReplaceWriteThrough(temporaryPath, StatePath);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }

    /// <summary>Held for a whole run of the step; null while another launcher holds it.</summary>
    public IDisposable? TryLock()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(StatePath)!);
            return new FileStream(StatePath + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// True while the journal says the registered distro is mid-upgrade, restoring or unproven, or the journal can't be
    /// read. A journal for another registration blocks nothing; the step supersedes it.
    /// </summary>
    internal static bool BlocksWrites((TrixieJournal? Journal, bool Unreadable) read, string? currentRegistrationId) =>
        read.Unreadable
        || read.Journal is { } journal && IsBlocking(journal.Phase) && currentRegistrationId is not null
           && string.Equals(journal.Registration.Id, currentRegistrationId, StringComparison.OrdinalIgnoreCase);

    internal static bool IsBlocking(TrixieJournalPhase phase) => phase is not (TrixieJournalPhase.Completed
        or TrixieJournalPhase.Cancelled or TrixieJournalPhase.Restored or TrixieJournalPhase.Superseded);
}

/// <summary>
/// What the Bookworm to Trixie upgrade needs on each Windows volume: the offline recovery export where it is
/// written, the upgrade's growth on the volume holding the distro's virtual disk, and the isolated recovery copy,
/// imported beside the distro on that same volume so a restore only renames. Shared volumes add up.
/// </summary>
internal static class TrixieSpacePlan
{
    private const long GiB = 1L << 30;

    internal static long BackupBytes(long usedBytes) => checked(usedBytes + usedBytes / 10 + GiB);
    internal static long GrowthBytes(long upgradeBytes) => checked(upgradeBytes + upgradeBytes / 4 + GiB);
    internal static long RestoreBytes(long usedBytes) => checked(usedBytes + usedBytes / 5 + 2 * GiB);

    /// <summary>Core's <c>upgrade_bytes</c> always includes its 2 GiB margin, so a preview before preflight assumes at least that.</summary>
    internal const long MinimumUpgradeBytes = 2 * GiB;

    /// <summary>Bytes required and free on each distinct volume, in the order first named.</summary>
    internal static IReadOnlyList<TrixieVolumeNeed> Needs(
        long usedBytes,
        long upgradeBytes,
        (string Volume, long FreeBytes) backup,
        (string Volume, long FreeBytes) disk,
        (string Volume, long FreeBytes) restore)
    {
        var needs = new List<TrixieVolumeNeed>();
        void Add((string Volume, long FreeBytes) target, long bytes)
        {
            var index = needs.FindIndex(need => string.Equals(need.Volume, target.Volume, StringComparison.OrdinalIgnoreCase));
            if (index < 0) needs.Add(new(target.Volume, bytes, target.FreeBytes));
            else needs[index] = needs[index] with { RequiredBytes = checked(needs[index].RequiredBytes + bytes), FreeBytes = target.FreeBytes };
        }
        Add(backup, BackupBytes(usedBytes));
        Add(disk, GrowthBytes(upgradeBytes));
        Add(restore, RestoreBytes(usedBytes));
        return needs;
    }

    /// <returns>Null when every volume has room; otherwise one sentence naming the short volumes.</returns>
    internal static string? FindShortfall(
        long usedBytes,
        long upgradeBytes,
        (string Volume, long FreeBytes) backup,
        (string Volume, long FreeBytes) disk,
        (string Volume, long FreeBytes) restore)
    {
        var shortVolumes = Needs(usedBytes, upgradeBytes, backup, disk, restore).Where(need => !need.HasRoom)
            .Select(need => $"{need.Volume} needs {FormatGiB(need.RequiredBytes)} free and has {FormatGiB(need.FreeBytes)}")
            .ToArray();
        return shortVolumes.Length == 0 ? null : "Not enough free space for the recovery copy and upgrade: " + string.Join("; ", shortVolumes) + ".";
    }

    internal static string FormatGiB(long bytes) => $"{bytes / (double)GiB:0.0} GB";
}

/// <summary>What one Windows volume must hold for the upgrade, and what it has.</summary>
internal sealed record TrixieVolumeNeed(string Volume, long RequiredBytes, long FreeBytes)
{
    public bool HasRoom => FreeBytes >= RequiredBytes;
}

/// <summary>
/// A fresh Debian 13 upgrade the user may be offered, from <see cref="TrixieUpdateMigration.PreviewAsync"/>. Null
/// <see cref="Volumes"/> means none is offered, for <see cref="Reason"/>.
/// </summary>
internal sealed record TrixieUpgradeOffer(IReadOnlyList<TrixieVolumeNeed>? Volumes, string? Reason)
{
    public bool Available => Volumes is not null;
    public bool HasRoom => Volumes is { } volumes && volumes.All(volume => volume.HasRoom);
    internal static TrixieUpgradeOffer None(string reason) => new(null, reason);
}

/// <summary>Everything the upgrade does outside its own logic, so the orchestration runs against fakes.</summary>
internal sealed record TrixieMaintenanceHost
{
    /// <summary>Runs a root command in DwemerDistro under the distro update lock; exit 75 means another update holds it.</summary>
    public required Func<string[], Action<string>, CancellationToken, Task<CommandResult>> RunCoreAsync { get; init; }
    /// <summary>Runs a root command in the named distro, starting it without <c>start_env</c>; no lock, no output shown.</summary>
    public required Func<string, string[], CancellationToken, Task<CommandResult>> RunAsRootAsync { get; init; }
    /// <summary>True only when PostgreSQL 17 maintenance reports <c>current</c>.</summary>
    public required Func<CancellationToken, Task<bool>> IsPg17CurrentAsync { get; init; }
    public required Func<IReadOnlyList<string>> FindRunningGames { get; init; }
    public required Func<bool> IsServerRunning { get; init; }
    public required Func<TimeSpan, CancellationToken, Task> DelayAsync { get; init; }
    /// <summary>The registration with this distribution name, with its disk's file identity; null when absent or incomplete.</summary>
    public required Func<string, TrixieRegistration?> ReadRegistration { get; init; }
    public required Func<string, (string Volume, long FreeBytes)?> ReadVolumeFreeSpace { get; init; }
    /// <summary>Claims the exclusive distro slot for the backup and upgrade; null when something else holds the distro.</summary>
    public required Func<CancellationToken, Task<IDisposable?>> EnterCutoverAsync { get; init; }
    /// <summary>Terminates the named distro; true once WSL reports it stopped.</summary>
    public required Func<string, CancellationToken, Task<bool>> TerminateAsync { get; init; }
    public required Func<string, CancellationToken, Task<bool>> IsRunningAsync { get; init; }
    /// <summary>Exports DwemerDistro to the given path.</summary>
    public required Func<string, Action<string>, CancellationToken, Task<CommandResult>> ExportAsync { get; init; }
    /// <summary>Imports (name, folder, archive) as a new WSL 2 registration.</summary>
    public required Func<string, string, string, Action<string>, CancellationToken, Task<CommandResult>> ImportAsync { get; init; }
    public required Func<string, long, CancellationToken, Task<(bool Ok, long Bytes, string? Sha256, string Reason)>> VerifyArchiveAsync { get; init; }
    public Func<string, string?> FileId { get; init; } = TrixieWindowsFiles.FileId;
    public Func<string, bool> IsExclusive { get; init; } = TrixieWindowsFiles.IsExclusive;
    /// <summary>Paces the restore's wait for WSL to let go of a terminated distro's disk; not the update deadline's delay.</summary>
    public Func<TimeSpan, CancellationToken, Task> DiskReleaseDelayAsync { get; init; } = Task.Delay;
    public Action<string, string> MoveSameVolume { get; init; } = TrixieWindowsFiles.MoveSameVolume;
    public Func<string, string, string> Protect { get; init; } = TrixieWindowsFiles.Protect;
    public Func<string?, string, string?> Unprotect { get; init; } = TrixieWindowsFiles.Unprotect;
    public required string BackupDirectory { get; init; }
    public Func<DateTimeOffset> Now { get; init; } = () => DateTimeOffset.Now;
}

/// <summary>
/// The zero-click Bookworm to Trixie step of the distro update, run after PostgreSQL 17 is verified, against Core's
/// <c>ddistro_trixie_maintenance</c> host protocol (TRIXIE.md). Core fences its writers and issues a capture token;
/// the launcher terminates the exact registration, exports it, imports and boots the export as an isolated copy that
/// must hold the same token, fence and PG17 seal, then hands Core the token with the export's digest and size. A failure
/// before the gameplay-write boundary is recovered without a click: Core cancels when no package changed, otherwise the
/// proven copy's disk replaces the failed one on the same volume, the failed disk is retained, and Core releases the
/// fence on the booted copy. Nothing is unregistered or deleted except the launcher's own unverified partial export.
/// </summary>
internal sealed class TrixieUpdateMigration(
    TrixieMaintenanceHost host,
    TrixieJournalStore journal,
    Func<bool> rolloutEnabled,
    PausableDeadline? deadline)
{
    internal const string ToolPath = "/usr/local/bin/ddistro_trixie_maintenance";
    internal const string CoreStatePath = "/var/lib/dwemerdistro/trixie-maintenance/state.json";
    internal const string CoreFencePath = "/etc/dwemer/trixie-upgrade.fence";
    internal const string Pg17DataPath = "/var/lib/postgresql/17/dwemer";
    internal const string SourceCodename = "bookworm";
    internal const string TargetCodename = "trixie";
    internal const string BackupFilePrefix = LauncherConstants.DistroName + "-bookworm-";
    internal const string RecoveryNamePrefix = LauncherConstants.DistroName + "-TrixieRecovery-";
    internal const int ExitDeferred = 3;
    internal const int ExitRestart = 4;
    internal const int ExitBusy = DistroUpdateRunner.OtherUpdateRunningExitCode;
    internal const int MaxUpgradeAttempts = 3;
    internal const int MaxVerifyRestarts = 3;
    internal const int MaxRestoreAttempts = 3;
    /// <summary>How long a restore waits for WSL to detach a terminated distro's disk; <c>wsl -t</c> returns before it does.</summary>
    internal static readonly TimeSpan DiskReleaseTimeout = TimeSpan.FromSeconds(90);
    internal static readonly TimeSpan DiskReleasePoll = TimeSpan.FromSeconds(1);
    internal static readonly TimeSpan CancelledRetryDelay = TimeSpan.FromHours(24);

    private static readonly string[] SealKeys =
        ["Database system identifier", "Latest checkpoint location", "Time of latest checkpoint", "Database cluster state"];

    // The run's workToken: it also stops a restore's wait for a disk to be released, which then rolls back and defers.
    private CancellationToken restoreToken;

    /// <summary>False when rollout is off and no journal exists: the step is then a no-op and the update shows no stage for it.</summary>
    public bool MayRun() => rolloutEnabled() || journal.Read() is not { Journal: null, Unreadable: false };

    /// <summary>The rollout itself, without a journal.</summary>
    public bool RolloutEnabled => rolloutEnabled();

    /// <summary>The OS codename, only when the Trixie tool is installed; empty otherwise. Read-only.</summary>
    internal const string CodenameReadScript = $"[ -x {ToolPath} ] && sed -n 's/^VERSION_CODENAME=//p' /etc/os-release || true";

    internal static string[] BuildCodenameReadArguments() => ["sh", "-c", CodenameReadScript];

    /// <summary>
    /// Whether the distro update about to run belongs to the migration, so it keeps local changes: rollout on, no completed
    /// journal for this registration, and the OS reads Bookworm, or nothing (a Core without the tool, which this update
    /// installs), or can't be read. Any other codename, such as Trixie, or a completed journal is an ordinary update, so
    /// component installers and dependencies update as before. Rollout off or a completed journal makes no WSL call.
    /// </summary>
    public async Task<bool> KeepsLocalChangesAsync(CancellationToken token)
    {
        if (!rolloutEnabled()) return false;
        var read = journal.Read();
        if (!read.Unreadable && read.Journal is { Phase: TrixieJournalPhase.Completed } entry
            && host.ReadRegistration(LauncherConstants.DistroName) is { } current && SameId(entry.Registration, current))
            return false;
        var reply = await host.RunAsRootAsync(LauncherConstants.DistroName, BuildCodenameReadArguments(), token).ConfigureAwait(false);
        var codename = reply.Succeeded ? reply.StandardOutput.Trim() : string.Empty;
        return codename.Length == 0 || codename == SourceCodename;
    }

    /// <summary>The root filesystem's allocated bytes as <c>statvfs</c> reports them: total blocks, free blocks, block size.</summary>
    internal const string UsedBytesReadScript = "stat -f -c '%b %f %S' /";

    /// <summary>
    /// Whether a fresh upgrade can be offered to the user, and the space it needs, without changing anything: no journal
    /// write, no lock, no preflight. Every command runs as root with <c>--exec</c> (no <c>start_env</c>) and only reads:
    /// the tool's digest and provenance, PostgreSQL 17 and Trixie <c>status</c>, and <c>statvfs</c>. The space is a lower
    /// bound (Core's <c>used_bytes</c> is at least the allocated blocks, its <c>upgrade_bytes</c> at least its 2 GiB margin);
    /// the start checks the exact figures again after preflight and changes nothing when they don't fit.
    /// </summary>
    public async Task<TrixieUpgradeOffer> PreviewAsync(CancellationToken token)
    {
        if (!rolloutEnabled()) return TrixieUpgradeOffer.None("rollout is off");
        var read = journal.Read();
        if (read.Unreadable) return TrixieUpgradeOffer.None("the system upgrade journal can't be read");
        if (host.ReadRegistration(LauncherConstants.DistroName) is not { } current)
            return TrixieUpgradeOffer.None("the distro's WSL registration could not be read");
        var previous = read.Journal is { } last && SameId(last.Registration, current) ? last : null;
        if (previous is not null && (TrixieJournalStore.IsBlocking(previous.Phase) || previous.Phase == TrixieJournalPhase.Completed))
            return TrixieUpgradeOffer.None($"the journal is {previous.Phase}");

        Task<CommandResult> Root(string[] arguments) => host.RunAsRootAsync(LauncherConstants.DistroName, arguments, token);
        var probe = await Root(["sha256sum", ToolPath]).ConfigureAwait(false);
        var toolSha = probe.Succeeded ? probe.StandardOutput.Trim().Split(' ', 2)[0] : string.Empty;
        if (!IsHex(toolSha, 64)) return TrixieUpgradeOffer.None("no Trixie maintenance tool is installed");
        if (previous is { Phase: TrixieJournalPhase.Restored } restored && restored.ToolSha256 == toolSha)
            return TrixieUpgradeOffer.None("an earlier attempt with this tool was restored");
        if (previous is { Phase: TrixieJournalPhase.Cancelled } cancelled && cancelled.ToolSha256 == toolSha
            && host.Now() - cancelled.UpdatedAt < CancelledRetryDelay)
            return TrixieUpgradeOffer.None("Core refused the upgrade recently");
        if (!(await Root(BuildToolProvenanceArguments()).ConfigureAwait(false)).Succeeded)
            return TrixieUpgradeOffer.None("the installed tool is not a version Core shipped");
        var pg17 = await Root(Pg17UpdateMigration.BuildStatusArguments()).ConfigureAwait(false);
        if (!pg17.Succeeded || Pg17UpdateMigration.ParseStatus(pg17.StandardOutput) != Pg17Status.Current)
            return TrixieUpgradeOffer.None("PostgreSQL 17 is not verified current");
        var statusResult = await Root(BuildStatusArguments()).ConfigureAwait(false);
        if ((statusResult.Succeeded ? ParseReply(statusResult.StandardOutput) : null) is not
            { State: "needed", Release: "12", Codename: "" or SourceCodename, Fence: not true })
            return TrixieUpgradeOffer.None("Core does not report the upgrade as needed");

        var usedBytes = ParseUsedBytes((await Root(["sh", "-c", UsedBytesReadScript]).ConfigureAwait(false)).StandardOutput);
        if (usedBytes is not > 0) return TrixieUpgradeOffer.None("the distro's used space could not be read");
        // Read-only: the backup folder is not created here, so its volume is read from the nearest folder that exists.
        var backupProbe = host.BackupDirectory;
        while (!Directory.Exists(backupProbe) && Path.GetDirectoryName(backupProbe) is { } parent) backupProbe = parent;
        var recoveryParent = RecoveryParent(current);
        if (host.ReadVolumeFreeSpace(backupProbe) is not { } backupVolume
            || host.ReadVolumeFreeSpace(current.DiskPath) is not { } diskVolume
            || host.ReadVolumeFreeSpace(recoveryParent) is not { } recoveryVolume)
            return TrixieUpgradeOffer.None("the free space of the backup and distro volumes could not be read");
        if (!string.Equals(diskVolume.Volume, recoveryVolume.Volume, StringComparison.OrdinalIgnoreCase))
            return TrixieUpgradeOffer.None($"the folder beside the distro ({recoveryParent}) is not on the volume that holds its virtual disk");
        return new TrixieUpgradeOffer(
            TrixieSpacePlan.Needs(usedBytes.Value, TrixieSpacePlan.MinimumUpgradeBytes, backupVolume, diskVolume, recoveryVolume), null);
    }

    /// <summary><c>(blocks - free) * block size</c> from <see cref="UsedBytesReadScript"/>; null when it is not three numbers.</summary>
    internal static long? ParseUsedBytes(string? output)
    {
        var fields = (output ?? string.Empty).Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length != 3 || !long.TryParse(fields[0], out var blocks) || !long.TryParse(fields[1], out var free)
            || !long.TryParse(fields[2], out var size) || blocks < free || size <= 0)
            return null;
        try
        {
            return checked((blocks - free) * size);
        }
        catch (OverflowException)
        {
            return null;
        }
    }

    // --- Core protocol -------------------------------------------------------------------------------------------

    internal static string[] BuildStatusArguments() => [ToolPath, "status", "--json"];
    internal static string[] BuildPreflightArguments() => [ToolPath, "preflight", "--json"];
    internal static string[] BuildPrepareArguments(string planId) =>
        [ToolPath, "upgrade", "--prepare", "--plan-id", IsHex(planId, 32) ? planId : throw new ArgumentException("Not a plan id.", nameof(planId)), "--json"];
    internal static string[] BuildUpgradeArguments(string token, string sha256, long bytes) =>
    [
        ToolPath, "upgrade",
        "--capture-token", IsHex(token, 64) ? token : throw new ArgumentException("Not a capture token.", nameof(token)),
        "--recovery-sha256", IsHex(sha256, 64) ? sha256 : throw new ArgumentException("Not a SHA-256.", nameof(sha256)),
        "--recovery-bytes", bytes > 0 ? bytes.ToString(System.Globalization.CultureInfo.InvariantCulture) : throw new ArgumentException("Not a size.", nameof(bytes)),
        "--json"
    ];
    internal static string[] BuildCancelArguments() => [ToolPath, "upgrade", "--cancel", "--json"];
    internal static string[] BuildVerifyArguments() => [ToolPath, "verify", "--json"];
    internal static string[] BuildReleaseArguments() => [ToolPath, "release", "--json"];

    /// <summary>
    /// Succeeds only when the installed tool's bytes are a blob that DwemerDistro-Core's fetched main ever had at
    /// <c>bin/ddistro_trixie_maintenance</c>, so a locally edited root script is never run. Root hashes the root-only
    /// file; dwemer reads its own checkout, so its Git configuration never runs as root. A raw record is
    /// <c>:old-mode new-mode old-blob new-blob status&lt;TAB&gt;path</c>; only records for exactly that path count, and
    /// any Git failure (pipefail) or a malformed hash fails the check with exit 1, never a code the lock reserves.
    /// </summary>
    internal static string[] BuildToolProvenanceArguments() =>
    [
        "bash", "-c",
        $"set -o pipefail; cd / && blob=\"$(git hash-object --no-filters -- {ToolPath})\" && " +
        "[[ \"$blob\" =~ ^[0-9a-f]{40}([0-9a-f]{24})?$ ]] && " +
        "runuser -u dwemer -- env GIT_NO_REPLACE_OBJECTS=1 GIT_CONFIG_GLOBAL=/dev/null GIT_CONFIG_SYSTEM=/dev/null " +
        "git -C /home/dwemer/dwemerdistro log --root --full-history -m --no-renames --no-abbrev --raw --format= " +
        "refs/remotes/origin/main -- bin/ddistro_trixie_maintenance " +
        "| awk -F '\\t' -v b=\"$blob\" -v p=bin/ddistro_trixie_maintenance " +
        "'NF == 2 && $2 == p && split($1, f, \" \") == 5 && f[1] ~ /^:/ && (f[3] == b || f[4] == b) { found = 1 } END { exit !found }' " +
        "|| exit 1"
    ];

    internal static bool IsHex(string? value, int length) =>
        value is not null && value.Length == length && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    internal static TrixieCoreReply? ParseReply(string? output)
    {
        var line = (output ?? string.Empty)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .LastOrDefault();
        if (line is null || !line.StartsWith('{')) return null;
        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("schema", out var schema) || !schema.TryGetInt32(out var version) || version != 1)
                return null;
            string Text(string name) => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString() ?? string.Empty : string.Empty;
            long? Number(string name) => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
                                         && value.TryGetInt64(out var number) ? number : null;
            bool? Flag(string name) => root.TryGetProperty(name, out var value)
                ? value.ValueKind switch { JsonValueKind.True => true, JsonValueKind.False => false, _ => null }
                : null;
            return new(Text("result"), Text("state"), Text("release"), Text("codename"), Text("instance_id"), Text("plan_id"),
                Text("capture_token"), Flag("fence"), Number("used_bytes"), Number("upgrade_bytes"), Details(root, "reasons"))
            {
                Restore = root.TryGetProperty("restore", out var restore) ? ParseRestore(restore) : null,
                RestorePending = Flag("restore_pending") == true
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The <c>detail</c> texts of a list of Core reasons, joined; empty without one.</summary>
    private static string Details(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var list) && list.ValueKind == JsonValueKind.Array
            ? string.Join("; ", list.EnumerateArray()
                .Where(item => item.ValueKind == JsonValueKind.Object && item.TryGetProperty("detail", out var detail) && detail.ValueKind == JsonValueKind.String)
                .Select(item => item.GetProperty("detail").GetString()))
            : string.Empty;

    /// <summary>Null for JSON null (nothing journaled); anything else without a boolean <c>pending</c> reads as pending.</summary>
    private static TrixieCoreRestore? ParseRestore(JsonElement restore)
    {
        if (restore.ValueKind != JsonValueKind.Object)
            return restore.ValueKind == JsonValueKind.Null ? null : new(true, false, [], [], [], string.Empty);
        bool? Flag(string name) => restore.TryGetProperty(name, out var value)
            ? value.ValueKind switch { JsonValueKind.True => true, JsonValueKind.False => false, _ => null }
            : null;
        string[] Names(string name) => restore.TryGetProperty(name, out var list) && list.ValueKind == JsonValueKind.Array
            ? list.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String).Select(item => item.GetString()!).ToArray()
            : [];
        return new(Flag("pending") != false, Flag("pg17") == true, Names("services"), Names("running"), Names("failed"), Details(restore, "problems"));
    }

    /// <summary>Core's own journal as root reads it: token, phase, plan and PG17 seal. Null when it isn't one.</summary>
    internal static (string Token, string Phase, string PlanId, string[] Seal)? ParseCoreState(string? text)
    {
        try
        {
            using var document = JsonDocument.Parse(text ?? string.Empty);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            string Text(string name) => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString() ?? string.Empty : string.Empty;
            if (!root.TryGetProperty("seal", out var seal) || seal.ValueKind != JsonValueKind.Array || seal.GetArrayLength() != SealKeys.Length
                || seal.EnumerateArray().Any(item => item.ValueKind != JsonValueKind.String))
                return null;
            return (Text("token"), Text("phase"), Text("plan_id"), seal.EnumerateArray().Select(item => item.GetString()!).ToArray());
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The four <c>pg_controldata</c> fields Core seals, in Core's order; null when one is missing.</summary>
    internal static string[]? ParseControlSeal(string? text)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in (text ?? string.Empty).Split('\n'))
        {
            var colon = line.IndexOf(':');
            if (colon > 0) values.TryAdd(line[..colon].Trim(), line[(colon + 1)..].Trim());
        }

        return SealKeys.All(values.ContainsKey) ? SealKeys.Select(key => values[key]).ToArray() : null;
    }

    // --- Entry point ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Resolves an unfinished journal first, without the generic updater; then, when <paramref name="allowFreshStart"/>
    /// and rollout are on, starts the upgrade. <paramref name="waitToken"/> ends a wait for games before anything changes;
    /// <paramref name="workToken"/> stops only the export or import, after which Core's fence is cancelled. Core's
    /// upgrade, verify and release always run to their own end, because stopping wsl.exe does not stop root work.
    /// </summary>
    public async Task<TrixieMaintenanceResult> RunAsync(
        Action<string> log, bool allowFreshStart, CancellationToken waitToken, CancellationToken workToken)
    {
        var read = journal.Read();
        if (read is { Journal: null, Unreadable: false } && !rolloutEnabled())
            return new(TrixieMaintenanceOutcome.NotActive, "The Debian Trixie upgrade is not enabled for this launcher.");
        if (read.Unreadable) return UnreadableJournal();
        restoreToken = workToken;

        using var instance = journal.TryLock();
        if (instance is null)
            return Blocked(TrixieMaintenanceOutcome.Deferred, "another launcher is running the Debian Trixie step; this one changed nothing");

        read = journal.Read();
        if (read.Unreadable) return UnreadableJournal();
        var current = host.ReadRegistration(LauncherConstants.DistroName);
        try
        {
            if (read.Journal is { } entry)
            {
                var same = current is not null && SameId(entry.Registration, current);
                if (TrixieJournalStore.IsBlocking(entry.Phase))
                {
                    if (current is null)
                        return Blocked(TrixieMaintenanceOutcome.RecoveryRequired,
                            $"{LauncherConstants.DistroName} is no longer registered with WSL during an unfinished system upgrade" +
                            CopyClause(entry) + ". Nothing was imported or removed automatically");
                    if (same) return await ResumeAsync(entry, log, workToken).ConfigureAwait(false);

                    log($"The system upgrade journal belongs to an earlier {LauncherConstants.DistroName} registration; it no longer applies." + Environment.NewLine);
                    Save(entry, TrixieJournalPhase.Superseded, "superseded by a new WSL registration");
                }
                else if (entry.Phase == TrixieJournalPhase.Completed && same)
                {
                    return new(TrixieMaintenanceOutcome.NotNeeded, "This distro already finished the Debian Trixie upgrade.");
                }
            }

            if (!allowFreshStart || !rolloutEnabled())
                return new(TrixieMaintenanceOutcome.NotNeeded, "No system upgrade is pending.");
            if (current is null)
                return new(TrixieMaintenanceOutcome.NotStarted, $"{LauncherConstants.DistroName} is not registered with WSL, or its registration has no virtual disk name.");
            var previous = read.Journal is { } last && SameId(last.Registration, current) ? last : null;
            return await StartAsync(current, previous, log, waitToken, workToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            // The journal could not be written: the step stops here, and what the journal last said still decides.
            return Blocked(TrixieMaintenanceOutcome.RecoveryRequired, "the system upgrade journal could not be saved (" + ex.Message.TrimEnd('.') +
                                                                       "); the step stopped before its next change");
        }
    }

    private TrixieMaintenanceResult UnreadableJournal() =>
        Blocked(TrixieMaintenanceOutcome.RecoveryRequired,
            $"the system upgrade journal ({journal.StatePath}) can't be read, so the launcher can't tell whether an upgrade " +
            "was interrupted. Nothing was changed; distro writes stay blocked until it is restored or reviewed");

    // --- Fresh start ---------------------------------------------------------------------------------------------

    private async Task<TrixieMaintenanceResult> StartAsync(
        TrixieRegistration registration, TrixieJournal? previous, Action<string> log, CancellationToken waitToken, CancellationToken workToken)
    {
        TrixieCoreReply preflight;
        TrixieJournal plan;
        try
        {
            var probe = await host.RunCoreAsync(["sha256sum", ToolPath], _ => { }, waitToken).ConfigureAwait(false);
            if (probe.ExitCode == ExitBusy) return Busy("nothing was changed");
            var toolSha = probe.Succeeded ? probe.StandardOutput.Trim().Split(' ', 2)[0] : string.Empty;
            if (!IsHex(toolSha, 64))
                return new(TrixieMaintenanceOutcome.NotNeeded, "This distro release does not include the Debian Trixie maintenance tool.");

            if (previous is { Phase: TrixieJournalPhase.Restored } restored && restored.ToolSha256 == toolSha)
                return new(TrixieMaintenanceOutcome.NotNeeded,
                    "An earlier Debian Trixie upgrade of this distro was rolled back to its recovery copy; it is retried once Core ships a new upgrade tool.");
            if (previous is { Phase: TrixieJournalPhase.Cancelled } cancelled && cancelled.ToolSha256 == toolSha
                && host.Now() - cancelled.UpdatedAt < CancelledRetryDelay)
                return new(TrixieMaintenanceOutcome.NotNeeded, "The Debian Trixie upgrade was refused recently; it is retried later.");

            var provenance = await host.RunCoreAsync(BuildToolProvenanceArguments(), _ => { }, waitToken).ConfigureAwait(false);
            if (provenance.ExitCode == ExitBusy) return Busy("nothing was changed");
            if (!provenance.Succeeded)
                return new(TrixieMaintenanceOutcome.NotStarted,
                    "the installed Debian Trixie maintenance tool is not a version DwemerDistro-Core shipped, so it was not run; nothing was changed");

            // PostgreSQL 15 has no Trixie packages; the OS upgrade only follows a verified PostgreSQL 17.
            if (!await host.IsPg17CurrentAsync(waitToken).ConfigureAwait(false))
                return new(TrixieMaintenanceOutcome.NotStarted,
                    "the Debian Trixie upgrade waits until the database is verified on PostgreSQL 17; nothing was changed");

            var statusResult = await host.RunCoreAsync(BuildStatusArguments(), _ => { }, waitToken).ConfigureAwait(false);
            if (statusResult.ExitCode == ExitBusy) return Busy("nothing was changed");
            var status = statusResult.Succeeded ? ParseReply(statusResult.StandardOutput) : null;
            plan = new TrixieJournal
            {
                Registration = registration, ToolSha256 = toolSha, InstanceId = NullIfEmpty(status?.InstanceId), UpdatedAt = host.Now()
            };
            switch (status?.State)
            {
                case "not-needed" or "current" when status.Release != "12":
                    Save(plan, TrixieJournalPhase.Completed, "already Debian 13");
                    return new(TrixieMaintenanceOutcome.NotNeeded, "The distro already runs Debian Trixie.");
                case "unsupported" or "deferred":
                    Save(plan, TrixieJournalPhase.Cancelled, "Core status " + status.State);
                    return new(TrixieMaintenanceOutcome.NotStarted,
                        "this distro can't be upgraded to Debian Trixie yet" + ReasonClause(status) + "; it was left unchanged");
                case "needed" when status.Release == "12" && (status.Codename is "" or SourceCodename) && status.Fence != true:
                    break;
                case null:
                    return new(TrixieMaintenanceOutcome.NotStarted, $"the Debian Trixie status check gave no answer (exit {statusResult.ExitCode}); nothing was changed");
                default:
                    // Fenced, interrupted, upgraded or failed without a journal of this launcher: never guessed at.
                    Save(plan, TrixieJournalPhase.RecoveryRequired, $"Core reported '{status.State}' with no launcher journal");
                    return Blocked(TrixieMaintenanceOutcome.RecoveryRequired,
                        $"Core reports the Debian Trixie upgrade as '{status.State}', which this launcher did not record. Core keeps its own " +
                        "services fenced; distro writes stay blocked and nothing was imported or removed");
            }

            if (IdleProblem() is { } busy) return busy;

            var preflightResult = await host.RunCoreAsync(BuildPreflightArguments(), _ => { }, waitToken).ConfigureAwait(false);
            if (preflightResult.ExitCode == ExitBusy) return Busy("nothing was changed");
            var parsed = ParseReply(preflightResult.StandardOutput);
            if (!preflightResult.Succeeded || parsed is not { Result: "ready" } || !IsHex(parsed.PlanId, 32))
            {
                Save(plan, TrixieJournalPhase.Cancelled, $"preflight exit {preflightResult.ExitCode}");
                return new(TrixieMaintenanceOutcome.NotStarted, "the Debian Trixie preflight refused the upgrade" +
                    (parsed is null ? $" (exit {preflightResult.ExitCode}): {LastLine(preflightResult)}" : ReasonClause(parsed)) + "; nothing was changed");
            }

            preflight = parsed;
            if (!Corroborates(plan.InstanceId, preflight.InstanceId))
                return new(TrixieMaintenanceOutcome.NotStarted, "the distro reported a different instance between checks; nothing was changed");
            if (preflight.UsedBytes is not > 0 || preflight.UpgradeBytes is not > 0)
            {
                Save(plan, TrixieJournalPhase.Cancelled, "preflight without sizes");
                return new(TrixieMaintenanceOutcome.NotStarted, "the Debian Trixie preflight did not report the sizes the space check needs; nothing was changed");
            }

            if (SpaceProblem(registration, preflight) is { } space)
                return new(TrixieMaintenanceOutcome.NotStarted, space + "; nothing was changed");

            // Once more right before cutover: the preflight and space checks take time.
            if (IdleProblem() is { } stillBusy) return stillBusy;
        }
        catch (OperationCanceledException) when (waitToken.IsCancellationRequested)
        {
            return new(TrixieMaintenanceOutcome.Deferred, "the Debian Trixie upgrade was postponed before it started; nothing was changed");
        }

        using var cutover = await host.EnterCutoverAsync(workToken).ConfigureAwait(false);
        if (cutover is null)
            return new(TrixieMaintenanceOutcome.Deferred, "another DwemerDistro operation is using the distro, so the Debian Trixie upgrade was postponed; nothing was changed");

        deadline?.Pause();
        try
        {
            if (Revalidate(registration, LauncherConstants.DistroName) is { } moved)
                return new(TrixieMaintenanceOutcome.NotStarted, moved + "; nothing was changed");
            if (host.IsServerRunning() || host.FindRunningGames().Count > 0)
                return new(TrixieMaintenanceOutcome.Deferred, "a game or the server started before the upgrade; nothing was changed");
            return await PrepareAsync(plan with { PlanId = preflight.PlanId, UsedBytes = preflight.UsedBytes!.Value }, log, workToken).ConfigureAwait(false);
        }
        finally
        {
            deadline?.Resume();
        }
    }

    private string? SpaceProblem(TrixieRegistration registration, TrixieCoreReply preflight)
    {
        Directory.CreateDirectory(host.BackupDirectory);
        var recoveryParent = RecoveryParent(registration);
        if (host.ReadVolumeFreeSpace(host.BackupDirectory) is not { } backupVolume
            || host.ReadVolumeFreeSpace(registration.DiskPath) is not { } diskVolume
            || host.ReadVolumeFreeSpace(recoveryParent) is not { } recoveryVolume)
            return "the launcher could not read the free space of the backup and distro volumes";
        if (!string.Equals(diskVolume.Volume, recoveryVolume.Volume, StringComparison.OrdinalIgnoreCase))
            return $"the folder beside the distro ({recoveryParent}) is not on the volume that holds its virtual disk, so a recovery copy there could not be swapped in";
        return TrixieSpacePlan.FindShortfall(preflight.UsedBytes!.Value, preflight.UpgradeBytes!.Value, backupVolume, diskVolume, recoveryVolume)?.TrimEnd('.');
    }

    private static string RecoveryParent(TrixieRegistration registration) =>
        Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(registration.BasePath)) ?? registration.BasePath;

    private async Task<TrixieMaintenanceResult> PrepareAsync(TrixieJournal plan, Action<string> log, CancellationToken workToken)
    {
        var entry = Save(plan, TrixieJournalPhase.Preparing);
        log("Fencing DwemerDistro's services for the Debian Trixie upgrade." + Environment.NewLine);
        var prepare = await host.RunCoreAsync(BuildPrepareArguments(plan.PlanId!), _ => { }, CancellationToken.None).ConfigureAwait(false);
        var reply = ParseReply(prepare.StandardOutput);
        if (prepare.ExitCode == ExitBusy)
        {
            Save(entry, TrixieJournalPhase.Cancelled, "prepare busy");
            return Busy("nothing was changed");
        }

        if (prepare.ExitCode == ExitDeferred)
        {
            // A prepare that defers after fencing lifts its own fence and starts again what it stopped; a start it left
            // unfinished is finished like a cancel's.
            var after = await StatusAsync().ConfigureAwait(false);
            if (after is { RestorePending: true } && after.Fence != true)
                return ServicesPendingAfterCancel(entry, "Core refused to fence the distro" + ReasonClause(reply), null);
            Save(entry, TrixieJournalPhase.Cancelled, "prepare deferred");
            return new(TrixieMaintenanceOutcome.NotStarted, "Core refused to fence the distro" + ReasonClause(reply) + "; nothing was changed");
        }

        if (!prepare.Succeeded || reply is not { Result: "fenced" } || !IsHex(reply.CaptureToken, 64))
            return await CancelAsync(entry, $"preparing the upgrade failed (exit {prepare.ExitCode})", log).ConfigureAwait(false);

        string protectedToken;
        try
        {
            protectedToken = host.Protect(reply.CaptureToken, TokenPurpose(entry));
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            return await CancelAsync(entry, "the capture token could not be protected for this Windows user: " + ex.Message, log).ConfigureAwait(false);
        }

        entry = Save(entry with { ProtectedToken = protectedToken }, TrixieJournalPhase.Fenced);
        // The fence as Core recorded it, read before the distro stops: the copy must hold exactly this.
        var proof = await ProveGuestAsync(LauncherConstants.DistroName, entry, reply.CaptureToken, viaCore: true).ConfigureAwait(false);
        if (proof.Problem is not null) return await CancelAsync(entry, "the fence could not be confirmed: " + proof.Problem, log).ConfigureAwait(false);
        entry = Save(entry with { Seal = proof.Seal }, TrixieJournalPhase.Fenced);
        return await BackupAsync(entry, reply.CaptureToken, log, workToken).ConfigureAwait(false);
    }

    // --- Recovery copy -------------------------------------------------------------------------------------------

    private async Task<TrixieMaintenanceResult> BackupAsync(TrixieJournal entry, string token, Action<string> log, CancellationToken workToken)
    {
        var stamp = host.Now().ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N")[..6];
        var backupPath = Path.Combine(host.BackupDirectory, $"{BackupFilePrefix}{stamp}.tar");
        entry = Save(entry with { BackupPath = backupPath }, TrixieJournalPhase.Exporting);
        try
        {
            log($"Making an offline recovery copy of {LauncherConstants.DistroName} before the Debian Trixie upgrade: {backupPath}{Environment.NewLine}");
            if (!await host.TerminateAsync(LauncherConstants.DistroName, workToken).ConfigureAwait(false))
                return await CancelAsync(entry, "the distro could not be stopped for the recovery copy", log).ConfigureAwait(false);
            if (Revalidate(entry.Registration, LauncherConstants.DistroName) is { } before)
                return await CancelAsync(entry, before, log).ConfigureAwait(false);
            var export = await host.ExportAsync(backupPath, log, workToken).ConfigureAwait(false);
            if (!export.Succeeded) return await CancelAsync(entry, $"the recovery copy failed (exit {export.ExitCode})", log).ConfigureAwait(false);
            // Anything that started the distro during the export may have written into it while it was copied.
            if (await host.IsRunningAsync(LauncherConstants.DistroName, workToken).ConfigureAwait(false))
                return await CancelAsync(entry, "something started the distro while the recovery copy was being made", log).ConfigureAwait(false);
            if (Revalidate(entry.Registration, LauncherConstants.DistroName) is { } after)
                return await CancelAsync(entry, after, log).ConfigureAwait(false);
            var check = await host.VerifyArchiveAsync(backupPath, entry.UsedBytes / 2, workToken).ConfigureAwait(false);
            if (!check.Ok || !IsHex(check.Sha256, 64))
                return await CancelAsync(entry, "the recovery copy did not verify: " + check.Reason, log).ConfigureAwait(false);
            entry = Save(entry with { BackupBytes = check.Bytes, BackupSha256 = check.Sha256 }, TrixieJournalPhase.Exported);
            log($"Recovery copy written ({check.Bytes:N0} bytes, SHA-256 {check.Sha256}).{Environment.NewLine}");
            return await ProveCopyAsync(entry, token, log, workToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return await CancelAsync(entry, "the recovery copy was stopped before it finished", log).ConfigureAwait(false);
        }
    }

    /// <summary>Imports the export as its own registration beside the distro and boots it to prove it holds the fence.</summary>
    private async Task<TrixieMaintenanceResult> ProveCopyAsync(TrixieJournal entry, string token, Action<string> log, CancellationToken workToken)
    {
        var stamp = Path.GetFileNameWithoutExtension(entry.BackupPath!)[BackupFilePrefix.Length..];
        var name = RecoveryNamePrefix + stamp;
        var folder = Path.Combine(RecoveryParent(entry.Registration), name);
        if (host.ReadRegistration(name) is not null || Directory.Exists(folder) || File.Exists(folder))
            return await CancelAsync(entry, $"the recovery copy's name {name} is already in use", log).ConfigureAwait(false);
        entry = Save(entry with { Recovery = new TrixieRegistration(name, "", folder, "", null) }, TrixieJournalPhase.ProvingCopy);

        log($"Checking the recovery copy by starting it on its own as {name}." + Environment.NewLine);
        var import = await host.ImportAsync(name, folder, entry.BackupPath!, log, workToken).ConfigureAwait(false);
        if (!import.Succeeded) return await CancelAsync(entry, $"the recovery copy could not be imported for its check (exit {import.ExitCode})", log).ConfigureAwait(false);
        var copy = host.ReadRegistration(name);
        if (copy is null || SameId(copy, entry.Registration) || copy.DiskFileId is null
            || !SamePath(copy.BasePath, folder) || copy.DiskFileId == entry.Registration.DiskFileId)
            return await CancelAsync(entry, "the recovery copy's WSL registration is not the one that was imported", log).ConfigureAwait(false);
        if (host.ReadVolumeFreeSpace(copy.DiskPath)?.Volume is not { } copyVolume
            || !string.Equals(copyVolume, host.ReadVolumeFreeSpace(entry.Registration.DiskPath)?.Volume, StringComparison.OrdinalIgnoreCase))
            return await CancelAsync(entry, "the recovery copy's disk is not on the distro's volume", log).ConfigureAwait(false);
        entry = Save(entry with { Recovery = copy }, TrixieJournalPhase.ProvingCopy);

        var proof = await ProveGuestAsync(name, entry, token, viaCore: false).ConfigureAwait(false);
        var stopped = await host.TerminateAsync(name, CancellationToken.None).ConfigureAwait(false);
        if (proof.Problem is not null) return await CancelAsync(entry, "the recovery copy did not prove itself: " + proof.Problem, log).ConfigureAwait(false);
        if (!stopped) return await CancelAsync(entry, "the recovery copy could not be stopped after its check", log).ConfigureAwait(false);
        if (await host.IsRunningAsync(LauncherConstants.DistroName, CancellationToken.None).ConfigureAwait(false)
            || Revalidate(entry.Registration, LauncherConstants.DistroName) is not null || Revalidate(copy, name) is not null)
            return await CancelAsync(entry, "the distro or its recovery copy changed during the check", log).ConfigureAwait(false);

        entry = Save(entry with { CopyVerified = true }, TrixieJournalPhase.CopyProven);
        log("The recovery copy started on its own and holds the same fence, token and database seal." + Environment.NewLine);
        workToken.ThrowIfCancellationRequested();
        return await UpgradeAsync(entry, log).ConfigureAwait(false);
    }

    private readonly record struct GuestProof(string[]? Seal, string? Problem);

    /// <summary>
    /// Boots <paramref name="distro"/> without <c>start_env</c> and checks Core's state there: <c>status</c> fenced,
    /// its journal with this token in phase fenced, the fence file, and a cleanly shut down PG17 whose control data
    /// matches Core's seal (and the launcher's record of it). Read-only.
    /// </summary>
    private async Task<GuestProof> ProveGuestAsync(string distro, TrixieJournal entry, string token, bool viaCore)
    {
        Task<CommandResult> Run(string[] arguments) => viaCore
            ? host.RunCoreAsync(arguments, _ => { }, CancellationToken.None)
            : host.RunAsRootAsync(distro, arguments, CancellationToken.None);

        var statusResult = await Run(BuildStatusArguments()).ConfigureAwait(false);
        var status = statusResult.Succeeded ? ParseReply(statusResult.StandardOutput) : null;
        if (status is not { State: "fenced" } || status.Fence == false)
            return new(null, $"Core reports '{status?.State ?? $"exit {statusResult.ExitCode}"}', not fenced");
        if (!Corroborates(entry.InstanceId, status.InstanceId)) return new(null, "Core reports another instance id");
        if (status.PlanId.Length > 0 && status.PlanId != entry.PlanId) return new(null, "Core reports another plan");

        var stateResult = await Run(["cat", CoreStatePath]).ConfigureAwait(false);
        var state = stateResult.Succeeded ? ParseCoreState(stateResult.StandardOutput) : null;
        if (state is null) return new(null, "Core's journal could not be read");
        if (!FixedEquals(state.Value.Token, token)) return new(null, "Core's journal holds another capture token");
        if (state.Value.Phase != "fenced" || state.Value.PlanId != entry.PlanId) return new(null, $"Core's journal is in phase '{state.Value.Phase}'");
        if (entry.Seal is { } recorded && !recorded.SequenceEqual(state.Value.Seal)) return new(null, "Core's PG17 seal differs from the one recorded at the fence");

        var fence = await Run(["test", "-f", CoreFencePath]).ConfigureAwait(false);
        if (!fence.Succeeded) return new(null, "the fence file is missing");

        var control = await Run(["env", "LC_ALL=C", "/usr/lib/postgresql/17/bin/pg_controldata", Pg17DataPath]).ConfigureAwait(false);
        var seal = control.Succeeded ? ParseControlSeal(control.StandardOutput) : null;
        if (seal is null) return new(null, "PostgreSQL 17's control data could not be read");
        if (!seal.SequenceEqual(state.Value.Seal) || seal[^1] != "shut down") return new(null, "PostgreSQL 17 is not the cluster Core sealed");
        return new(seal, null);
    }

    // --- Upgrade, verify and release -----------------------------------------------------------------------------

    private async Task<TrixieMaintenanceResult> UpgradeAsync(TrixieJournal entry, Action<string> log)
    {
        while (true)
        {
            var token = host.Unprotect(entry.ProtectedToken, TokenPurpose(entry));
            if (!IsHex(token, 64)) return await CancelAsync(entry, "the protected capture token could not be read", log).ConfigureAwait(false);
            if (entry.UpgradeAttempts >= MaxUpgradeAttempts)
                return await CancelOrRestoreAsync(entry, $"the upgrade did not finish after {MaxUpgradeAttempts} attempts", log).ConfigureAwait(false);
            if (Revalidate(entry.Registration, LauncherConstants.DistroName) is { } moved)
                return Manual(entry, moved + " before the upgrade");
            if (Revalidate(entry.Recovery!, entry.Recovery!.Name) is { } copyMoved)
                return await CancelAsync(entry, copyMoved, log).ConfigureAwait(false);

            entry = Save(entry with { UpgradeAttempts = entry.UpgradeAttempts + 1 }, TrixieJournalPhase.Upgrading);
            log("Upgrading the distro from Debian Bookworm to Trixie. This can take a while; keep the launcher open." + Environment.NewLine);
            var result = await host.RunCoreAsync(BuildUpgradeArguments(token!, entry.BackupSha256!, entry.BackupBytes), log, CancellationToken.None).ConfigureAwait(false);
            var reply = ParseReply(result.StandardOutput);
            if (result.Succeeded && reply is { Result: "upgraded" })
                return await VerifyAsync(Save(entry, TrixieJournalPhase.Upgraded), log).ConfigureAwait(false);
            if (result.ExitCode == ExitBusy)
            {
                Save(entry with { UpgradeAttempts = entry.UpgradeAttempts - 1 }, TrixieJournalPhase.CopyProven);
                return Blocked(TrixieMaintenanceOutcome.Deferred, "another maintenance operation holds the distro's lock, so the Debian Trixie upgrade waits; the distro was not changed");
            }

            var status = await StatusAsync().ConfigureAwait(false);
            switch (status?.State)
            {
                case "fenced" when result.ExitCode == ExitDeferred:
                    // Core went back to its fence: the solution changed or the fence check failed. Nothing changed.
                    return await CancelAsync(entry, "Core refused the upgrade" + ReasonClause(reply), log).ConfigureAwait(false);
                case "interrupted" or "fenced":
                    log($"The upgrade stopped (exit {result.ExitCode}); resuming it with the same capture token." + Environment.NewLine);
                    continue;
                case "upgraded":
                    return await VerifyAsync(Save(entry, TrixieJournalPhase.Upgraded), log).ConfigureAwait(false);
                case "verified":
                    return await ReleaseAsync(entry, log).ConfigureAwait(false);
                case null:
                    return Blocked(TrixieMaintenanceOutcome.Deferred, "the distro did not answer after the upgrade stopped; it is checked again on the next launch" + CopyClause(entry));
                default:
                    return Manual(entry, $"Core reports '{status.State}' after the upgrade stopped");
            }
        }
    }

    private async Task<TrixieMaintenanceResult> VerifyAsync(TrixieJournal entry, Action<string> log)
    {
        for (var attempt = 1; attempt <= MaxVerifyRestarts; attempt++)
        {
            // Verified from a fresh start, so the proof covers booting the upgraded distro, not just the running session.
            if (!await host.TerminateAsync(LauncherConstants.DistroName, CancellationToken.None).ConfigureAwait(false))
                return Blocked(TrixieMaintenanceOutcome.Deferred, "the upgraded distro could not be restarted for its check; it is checked on the next launch");
            if (Revalidate(entry.Registration, LauncherConstants.DistroName) is { } moved) return Manual(entry, moved + " before the check");
            var result = await host.RunCoreAsync(BuildVerifyArguments(), _ => { }, CancellationToken.None).ConfigureAwait(false);
            var reply = ParseReply(result.StandardOutput);
            switch (result.ExitCode)
            {
                case 0 when reply is { Result: "current" }:
                    // Core was already released (its journal is current), so the boundary was crossed before this run.
                    // Releasing again changes nothing then, except to start what an earlier release left stopped.
                    return await ReleaseAsync(entry, log).ConfigureAwait(false);
                case 0 when reply is { Result: "verified" }:
                    return await ReleaseAsync(entry, log).ConfigureAwait(false);
                case 0:
                    return Manual(entry, "Core's verify gave an answer this launcher does not know");
                case ExitRestart:
                    log("Restarting the distro: processes still used replaced files." + Environment.NewLine);
                    continue;
                case ExitBusy:
                    return Blocked(TrixieMaintenanceOutcome.Deferred, "another maintenance operation holds the distro's lock, so the Debian Trixie check waits; distro writes stay blocked");
                case ExitDeferred:
                    return await RestoreAsync(entry, "the upgraded distro did not verify" + ReasonClause(reply), log).ConfigureAwait(false);
                default:
                    if (attempt < MaxVerifyRestarts) continue;
                    return await RestoreAsync(entry, $"the upgraded distro's check failed (exit {result.ExitCode}): {LastLine(result)}", log).ConfigureAwait(false);
            }
        }

        return Blocked(TrixieMaintenanceOutcome.Deferred, "the upgraded distro still runs replaced files after restarts; it is checked again on the next launch");
    }

    /// <summary>
    /// Records the irrevocable gameplay-write boundary, then asks Core to release its fence. Core refuses (exit 3)
    /// when anything changed since <c>verify</c>; then it is verified once more, still fenced, and released again.
    /// Past the boundary nothing is restored automatically, whatever <c>verify</c> finds.
    /// </summary>
    private async Task<TrixieMaintenanceResult> ReleaseAsync(TrixieJournal entry, Action<string> log)
    {
        entry = Save(entry with { GameplayWriteBoundary = true }, TrixieJournalPhase.Releasing);
        log("The upgraded distro verified; releasing its services." + Environment.NewLine);
        for (var attempt = 1; ; attempt++)
        {
            var result = await host.RunCoreAsync(BuildReleaseArguments(), _ => { }, CancellationToken.None).ConfigureAwait(false);
            var reply = ParseReply(result.StandardOutput);
            if (result.Succeeded && reply is { Result: "current" })
                return Released(entry, reply.Restore is { Pending: true }, reply.Restore);

            if (result.ExitCode == ExitBusy)
                return Blocked(TrixieMaintenanceOutcome.Deferred, "another maintenance operation holds the distro's lock, so releasing the upgraded distro waits");
            var status = await StatusAsync().ConfigureAwait(false);
            if (status is { State: "current" } && status.Fence != true)
                return Released(entry, status.RestorePending, reply?.Restore);

            if (result.ExitCode != ExitDeferred || status is not { State: "verified" } || attempt > 1)
                return Manual(entry, $"Core did not release the verified upgrade (exit {result.ExitCode})" +
                                     (reply is { Reasons.Length: > 0 } ? ReasonClause(reply) : ": " + LastLine(result)));

            var again = await host.RunCoreAsync(BuildVerifyArguments(), _ => { }, CancellationToken.None).ConfigureAwait(false);
            if (!again.Succeeded || ParseReply(again.StandardOutput) is not { Result: "verified" })
                return Manual(entry, $"the upgraded distro no longer verifies before its release (exit {again.ExitCode})" + ReasonClause(ParseReply(again.StandardOutput)));
        }
    }

    /// <summary>
    /// Core is current and its fence is gone. Until Core reports that what ran before the fence started again, the journal
    /// stays <see cref="TrixieJournalPhase.Releasing"/>: the next launch runs <c>release</c> again, which only starts what
    /// is still stopped. Nothing is restored past this point.
    /// </summary>
    private TrixieMaintenanceResult Released(TrixieJournal entry, bool servicesPending, TrixieCoreRestore? restore)
    {
        if (servicesPending)
        {
            Save(entry, TrixieJournalPhase.Releasing, "Core released the upgrade but has not started again what ran before");
            return Blocked(TrixieMaintenanceOutcome.Deferred,
                "the distro runs Debian Trixie and stays upgraded, but Core has not yet started again what ran before the upgrade" + PendingClause(restore) +
                ". Releasing it is retried on the next launch, and the server stays stopped until then");
        }

        Save(entry, TrixieJournalPhase.Completed);
        return Done(entry);
    }

    private TrixieMaintenanceResult Done(TrixieJournal entry) =>
        new(TrixieMaintenanceOutcome.Completed, "The distro was upgraded to Debian Trixie and verified." + KeptCopyClause(entry));

    // --- Cancel and restore --------------------------------------------------------------------------------------

    /// <summary>Before any package changed: Core removes its fence. Core decides; its refusal is never overridden.</summary>
    private async Task<TrixieMaintenanceResult> CancelAsync(TrixieJournal entry, string reason, Action<string> log)
    {
        if (entry.Phase == TrixieJournalPhase.Exporting && entry.BackupSha256 is null && entry.BackupPath is { } partial)
            TryDeleteOwnedPartialExport(partial);
        entry = Save(entry, TrixieJournalPhase.Cancelling, reason);
        log($"Cancelling the Debian Trixie upgrade: {reason}." + Environment.NewLine);
        var result = await host.RunCoreAsync(BuildCancelArguments(), _ => { }, CancellationToken.None).ConfigureAwait(false);
        if (result.ExitCode == ExitBusy)
            return Blocked(TrixieMaintenanceOutcome.Deferred, reason + "; cancelling waits for another maintenance operation, and distro writes stay blocked");

        var reply = result.Succeeded ? ParseReply(result.StandardOutput) : null;
        var status = await StatusAsync().ConfigureAwait(false);
        if (status is { State: "needed" or "deferred" or "unsupported" } && status.Fence != true)
            return Cancelled(entry, reason, reply, status, "; the upgrade was cancelled and the distro was not changed");

        if (status is null) return Blocked(TrixieMaintenanceOutcome.Deferred, reason + "; the distro did not answer while cancelling, which is retried on the next launch");
        if (FenceGone(entry, status)) return Manual(entry, reason + $"; Core reports '{status.State}' without its fence, so writers may have run and no disk is swapped");
        return CopyIsProven(entry)
            ? await RestoreAsync(entry, reason + $"; Core refused to cancel (state '{status.State}')", log).ConfigureAwait(false)
            : Manual(entry, reason + $"; Core reports '{status.State}' and refused to cancel");
    }

    private async Task<TrixieMaintenanceResult> CancelOrRestoreAsync(TrixieJournal entry, string reason, Action<string> log)
    {
        var result = await host.RunCoreAsync(BuildCancelArguments(), _ => { }, CancellationToken.None).ConfigureAwait(false);
        if (result.ExitCode == ExitBusy) return Blocked(TrixieMaintenanceOutcome.Deferred, reason + "; another maintenance operation holds the distro's lock");
        var reply = result.Succeeded ? ParseReply(result.StandardOutput) : null;
        var status = await StatusAsync().ConfigureAwait(false);
        if (status is { State: "needed" or "deferred" } && status.Fence != true)
            return Cancelled(entry, reason, reply, status, " before any package changed; the upgrade was cancelled");
        // Whether the cancel lifted the fence is unknown until Core answers; the next launch asks again.
        if (status is null) return Blocked(TrixieMaintenanceOutcome.Deferred, reason + "; the distro did not answer after the cancel, which is checked again on the next launch");
        if (FenceGone(entry, status)) return Manual(entry, reason + $"; Core reports '{status.State}' without its fence, so writers may have run and no disk is swapped");

        return await RestoreAsync(entry, reason, log).ConfigureAwait(false);
    }

    /// <summary>Core cancelled before any package changed; settled only once it reports that what ran before the fence started again.</summary>
    private TrixieMaintenanceResult Cancelled(TrixieJournal entry, string reason, TrixieCoreReply? reply, TrixieCoreReply status, string tail)
    {
        if (reply?.Restore is { Pending: true } || status.RestorePending) return ServicesPendingAfterCancel(entry, reason, reply?.Restore);
        Save(entry, TrixieJournalPhase.Cancelled, reason);
        return new(TrixieMaintenanceOutcome.NotStarted, reason + tail + LeftoverCopyClause(entry));
    }

    /// <summary>
    /// The fence is gone but Core has not started everything again: the journal stays <see cref="TrixieJournalPhase.Cancelling"/>
    /// with the fence recorded as lifted, so the next launch only runs <c>upgrade --cancel</c> again and never swaps a disk.
    /// </summary>
    private TrixieMaintenanceResult ServicesPendingAfterCancel(TrixieJournal entry, string reason, TrixieCoreRestore? restore)
    {
        Save(entry with { FenceLifted = true }, TrixieJournalPhase.Cancelling, reason);
        return Blocked(TrixieMaintenanceOutcome.Deferred,
            reason + "; nothing was upgraded and Core lifted its fence, but it has not yet started again what ran before" + PendingClause(restore) +
            ". This is retried on the next launch, and the server stays stopped until then");
    }

    /// <summary>A refused cancel may lead to a restore only while Core still holds the fence it recorded.</summary>
    private static bool FenceGone(TrixieJournal entry, TrixieCoreReply status) => entry.FenceLifted || status.Fence != true;

    private static bool CopyIsProven(TrixieJournal entry) =>
        !entry.GameplayWriteBoundary && !entry.FenceLifted && entry.CopyVerified && entry.Recovery is { Id.Length: > 0, DiskFileId: not null } && entry.Registration.DiskFileId is not null
        && entry.BackupSha256 is not null && entry.Seal is not null
        && entry.Phase is TrixieJournalPhase.CopyProven or TrixieJournalPhase.Upgrading or TrixieJournalPhase.Upgraded
            or TrixieJournalPhase.Cancelling or TrixieJournalPhase.Restoring;

    /// <summary>
    /// No-click recovery before the gameplay-write boundary: the proven recovery disk takes the failed disk's place in
    /// the original registration (same GUID, folder and disk name), the failed disk is retained by the recovery copy's
    /// registration, and Core releases its fence on the booted copy. Three same-volume renames, journaled one by one;
    /// an interrupted swap is finished from wherever the files are, and a failed rename is rolled back.
    /// </summary>
    private async Task<TrixieMaintenanceResult> RestoreAsync(TrixieJournal entry, string reason, Action<string> log)
    {
        if (!CopyIsProven(entry)) return Manual(entry, reason);
        if (entry.Phase != TrixieJournalPhase.Restoring)
        {
            if (entry.RestoreAttempts >= MaxRestoreAttempts) return Manual(entry, reason + "; restoring the recovery copy failed before");
            var stamp = Guid.NewGuid().ToString("N")[..8];
            entry = Save(entry with
            {
                RestoreAttempts = entry.RestoreAttempts + 1, SwapStep = 0,
                SwapAsidePath = Path.Combine(entry.Registration.BasePath, Path.GetFileNameWithoutExtension(entry.Registration.VhdFileName) + $".trixie-failed-{stamp}.vhdx")
            }, TrixieJournalPhase.Restoring, reason);
        }

        log($"Restoring the recovery copy made before the Debian Trixie upgrade: {reason}." + Environment.NewLine);
        var original = entry.Registration;
        var copy = entry.Recovery!;
        foreach (var name in new[] { original.Name, copy.Name })
            if (!await host.TerminateAsync(name, CancellationToken.None).ConfigureAwait(false))
                return Blocked(TrixieMaintenanceOutcome.Deferred, $"{name} could not be stopped to restore the recovery copy; this is retried on the next launch");
        if (!SameRegistrationFields(original, host.ReadRegistration(original.Name)) || !SameRegistrationFields(copy, host.ReadRegistration(copy.Name)))
            return Manual(entry, "the distro or its recovery copy's WSL registration changed before the restore");

        string a = original.DiskFileId!, r = copy.DiskFileId!;
        string pa = original.DiskPath, pr = copy.DiskPath, pt = entry.SwapAsidePath!;
        int Arrangement()
        {
            var (atA, atR, atT) = (host.FileId(pa), host.FileId(pr), host.FileId(pt));
            if (atA == a && atR == r && atT is null) return 0;
            if (atA is null && atR == r && atT == a) return 1;
            if (atA == r && atR is null && atT == a) return 2;
            if (atA == r && atR == a && atT is null) return 3;
            return -1;
        }

        var step = Arrangement();
        if (step < 0) return Manual(entry, "the distro's disks are not where the restore left them; nothing more was moved");
        if (step == 0)
        {
            // The copy may have been started by hand since it was proven: prove it again right before it is swapped in.
            var token = host.Unprotect(entry.ProtectedToken, TokenPurpose(entry));
            var proof = IsHex(token, 64) ? await ProveGuestAsync(copy.Name, entry, token!, viaCore: false).ConfigureAwait(false) : new(null, "the capture token could not be read");
            await host.TerminateAsync(copy.Name, CancellationToken.None).ConfigureAwait(false);
            if (proof.Problem is not null) return Manual(entry, reason + "; the recovery copy no longer proves itself: " + proof.Problem);
        }

        var moves = new (string From, string To)[] { (pa, pt), (pr, pa), (pt, pr) };
        var waited = TimeSpan.Zero;
        // wsl -t returns before WSL detaches the disk: wait, bounded and cancellable, until nothing holds the disks still
        // to be moved. Only these two registrations were stopped; no other distro is shut down and no handle is closed.
        async Task WaitForReleaseAsync(IEnumerable<string> paths)
        {
            foreach (var path in paths)
            {
                while (true)
                {
                    if (await host.IsRunningAsync(original.Name, CancellationToken.None).ConfigureAwait(false)
                        || await host.IsRunningAsync(copy.Name, CancellationToken.None).ConfigureAwait(false))
                        throw new IOException("a distro started during the restore");
                    if (host.IsExclusive(path)) break;
                    if (waited >= DiskReleaseTimeout)
                        throw new DiskStillOpenException($"{path} was still open after waiting {(int)waited.TotalSeconds} seconds for WSL to release it");
                    if (waited == TimeSpan.Zero) log($"Waiting for WSL to release {path} before restoring the recovery copy." + Environment.NewLine);
                    await host.DiskReleaseDelayAsync(DiskReleasePoll, restoreToken).ConfigureAwait(false);
                    waited += DiskReleasePoll;
                }
            }
        }

        // What a wait may have let change: the files, the two registrations, or the journal's no-write boundary.
        string? ChangedSince(int expectedStep)
        {
            if (Arrangement() != expectedStep) return "the distro's disks moved while the restore waited for them; nothing more was moved";
            if (!SameRegistrationFields(original, host.ReadRegistration(original.Name)) || !SameRegistrationFields(copy, host.ReadRegistration(copy.Name)))
                return "the distro or its recovery copy's WSL registration changed during the restore; nothing more was moved";
            return journal.Read().Journal is { Phase: TrixieJournalPhase.Restoring } onDisk && SameId(onDisk.Registration, original)
                                                                                         && onDisk.Recovery is { } recorded && SameId(recorded, copy) && CopyIsProven(onDisk)
                ? null
                : "the system upgrade journal no longer allows the restore; nothing more was moved";
        }

        var moved = step > 0;
        try
        {
            // Every disk still to be moved is released before the first rename, so a held disk usually moves nothing.
            await WaitForReleaseAsync(moves[step..].Select(move => move.From).Where(path => host.FileId(path) is not null).ToArray()).ConfigureAwait(false);
            for (; step < 3; step++)
            {
                var (from, to) = moves[step];
                await WaitForReleaseAsync([from]).ConfigureAwait(false);
                if (ChangedSince(step) is { } changed) return Manual(entry, changed);
                entry = Save(entry with { SwapStep = step + 1 }, TrixieJournalPhase.Restoring, reason);
                host.MoveSameVolume(from, to);
                moved = true;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            for (var back = Arrangement() - 1; back >= 0; back--)
            {
                try
                {
                    host.MoveSameVolume(moves[back].To, moves[back].From);
                }
                catch (Exception undo) when (undo is IOException or UnauthorizedAccessException)
                {
                    return Manual(entry, $"restoring the recovery copy stopped ({ex.Message.TrimEnd('.')}) and could not be undone ({undo.Message.TrimEnd('.')})");
                }
            }

            if (Arrangement() != 0) return Manual(entry, "restoring the recovery copy stopped and the disks are not back in place");
            var inPlace = moved ? "the disks were put back" : "no disk was moved";
            var untouched = ex is DiskStillOpenException ? "; nothing was forced closed and no other WSL distro was stopped" : string.Empty;
            if (ex is OperationCanceledException)
            {
                Save(entry with { SwapStep = 0 }, TrixieJournalPhase.Restoring, reason);
                return Blocked(TrixieMaintenanceOutcome.Deferred, $"restoring the recovery copy was stopped while it waited for WSL to release the disks; {inPlace}, distro writes stay blocked, and it is retried on the next launch");
            }

            if (entry.RestoreAttempts >= MaxRestoreAttempts)
                return Manual(entry, $"restoring the recovery copy stopped {MaxRestoreAttempts} times ({ex.Message.TrimEnd('.')}); {inPlace}{untouched}");
            Save(entry with { SwapStep = 0, RestoreAttempts = entry.RestoreAttempts + 1 }, TrixieJournalPhase.Restoring, reason);
            return Blocked(TrixieMaintenanceOutcome.Deferred, $"restoring the recovery copy stopped ({ex.Message.TrimEnd('.')}); {inPlace}{untouched}, distro writes stay blocked, and it is retried on the next launch");
        }

        if (Arrangement() != 3) return Manual(entry, "the disks are not where the restore put them");
        entry = Save(entry with { SwapStep = 4 }, TrixieJournalPhase.Restoring, reason);
        return await ReleaseRestoredAsync(entry, reason, log).ConfigureAwait(false);
    }

    /// <summary>Boots the original registration on the recovery disk, proves it, and has Core release its fence there.</summary>
    private async Task<TrixieMaintenanceResult> ReleaseRestoredAsync(TrixieJournal entry, string reason, Action<string> log)
    {
        var token = host.Unprotect(entry.ProtectedToken, TokenPurpose(entry));
        var original = host.ReadRegistration(entry.Registration.Name);
        if (original is null || !SameRegistrationFields(entry.Registration, original) || original.DiskFileId != entry.Recovery!.DiskFileId)
            return Manual(entry, "the restored distro's registration or disk is not the recovery copy");

        var status = await StatusAsync().ConfigureAwait(false);
        if (status is null) return Blocked(TrixieMaintenanceOutcome.Deferred, "the restored distro did not answer; the restore is finished on the next launch");
        if (status.State != "fenced" || status.Fence != true || entry.FenceLifted || !IsHex(token, 64))
        {
            // The fence is gone: an earlier cancel lifted it, or the journal write after one was lost. Accept only Core's
            // own record of this token; a restart of services it left unfinished is retried with cancel alone.
            var state = await host.RunAsRootAsync(entry.Registration.Name, ["cat", CoreStatePath], CancellationToken.None).ConfigureAwait(false);
            var core = state.Succeeded ? ParseCoreState(state.StandardOutput) : null;
            var lifted = core?.Phase == "cancelled" && status.State is "needed" or "deferred"
                         || core?.Phase == "fenced" && status.State == "fenced"; // Core stopped between removing its fence and its journal
            if (status.Fence == true || !lifted || !IsHex(token, 64) || !FixedEquals(core!.Value.Token, token!))
                return Manual(entry, $"the restored distro reports '{status.State}'");
            if (core.Value.Phase == "cancelled" && !status.RestorePending) return Restored(entry, reason, servicesConfirmed: false);
            entry = Save(entry with { FenceLifted = true }, TrixieJournalPhase.Restoring, reason);
        }
        else
        {
            var proof = await ProveGuestAsync(entry.Registration.Name, entry, token!, viaCore: true).ConfigureAwait(false);
            if (proof.Problem is not null) return Manual(entry, "the restored distro did not prove itself: " + proof.Problem);
        }

        var cancel = await host.RunCoreAsync(BuildCancelArguments(), _ => { }, CancellationToken.None).ConfigureAwait(false);
        if (cancel.ExitCode == ExitBusy) return Blocked(TrixieMaintenanceOutcome.Deferred, "the restored distro is locked by another maintenance operation; its release is finished on the next launch");
        var reply = cancel.Succeeded ? ParseReply(cancel.StandardOutput) : null;
        var after = await StatusAsync().ConfigureAwait(false);
        if (after is { State: "needed" or "deferred" } && after.Fence != true && (reply is { Result: "cancelled" } || entry.FenceLifted))
        {
            if (reply?.Restore is { Pending: true } || after.RestorePending)
            {
                Save(entry with { FenceLifted = true }, TrixieJournalPhase.Restoring, reason);
                return Blocked(TrixieMaintenanceOutcome.Deferred,
                    $"the Debian Trixie upgrade failed ({reason.TrimEnd('.')}), so {LauncherConstants.DistroName} was put back on its recovery copy from before the upgrade " +
                    "and Core lifted its fence there, but it has not yet started again what ran before" + PendingClause(reply?.Restore) +
                    ". This is retried on the next launch, the disks stay as they are, and the server stays stopped until then");
            }

            log("The recovery copy is serving again and Core released its fence." + Environment.NewLine);
            return Restored(entry, reason, servicesConfirmed: reply?.Restore is not null);
        }

        if (after is null || after.Fence != true)
        {
            // The cancel may have lifted the fence without saying so: no disk moves again, and Core is asked on the next launch.
            Save(entry with { FenceLifted = true }, TrixieJournalPhase.Restoring, reason);
            return Blocked(TrixieMaintenanceOutcome.Deferred, $"Core did not confirm releasing the restored distro (exit {cancel.ExitCode}): {LastLine(cancel)}; " +
                                                              "this is retried on the next launch and the disks stay as they are");
        }

        return Manual(entry, $"Core did not release the restored distro (exit {cancel.ExitCode}): {LastLine(cancel)}");
    }

    private TrixieMaintenanceResult Restored(TrixieJournal entry, string reason, bool servicesConfirmed)
    {
        var copy = entry.Recovery!;
        // The registrations now hold each other's disks: the original runs the recovery copy, the copy holds the failed disk.
        Save(entry with
        {
            Registration = entry.Registration with { DiskFileId = copy.DiskFileId },
            Recovery = copy with { DiskFileId = entry.Registration.DiskFileId }
        }, TrixieJournalPhase.Restored, reason);
        return new(TrixieMaintenanceOutcome.Restored,
            $"The Debian Trixie upgrade failed ({reason.TrimEnd('.')}), so {LauncherConstants.DistroName} was put back on its recovery copy from before the upgrade, " +
            (servicesConfirmed ? "which started, and Core released its fence and started again what ran before. " : "which started, and Core released its fence. ") +
            $"The failed disk is kept as the WSL distro {copy.Name}, and the copy as {entry.BackupPath}; " +
            "remove them once no longer needed. Nothing was unregistered or deleted");
    }

    // --- Resume --------------------------------------------------------------------------------------------------

    /// <summary>
    /// Continues an unfinished journal under the exclusive slot. Never waits for games: the server stays fenced and
    /// blocked until the step resolves it anyway.
    /// </summary>
    private async Task<TrixieMaintenanceResult> ResumeAsync(TrixieJournal entry, Action<string> log, CancellationToken workToken)
    {
        if (entry.Phase == TrixieJournalPhase.RecoveryRequired)
        {
            // Read-only: a repair made since, by Core or by hand, is accepted once Core reports itself current.
            var report = await StatusAsync().ConfigureAwait(false);
            if (report is not { State: "current" } || report.Fence == true)
                return Blocked(TrixieMaintenanceOutcome.RecoveryRequired, "the Debian Trixie upgrade still needs recovery" +
                                                                          (entry.Note is { Length: > 0 } note ? $" ({note.TrimEnd('.')})" : string.Empty) + CopyClause(entry));
            if (!report.RestorePending)
            {
                Save(entry with { GameplayWriteBoundary = true }, TrixieJournalPhase.Completed, "Core reports current after recovery");
                return new(TrixieMaintenanceOutcome.Completed, "The Debian Trixie upgrade now reports current; distro writes are allowed again.");
            }
            // Current, with services Core has not started again: release finishes that, under the exclusive slot below.
        }

        using var cutover = await host.EnterCutoverAsync(workToken).ConfigureAwait(false);
        if (cutover is null)
            return Blocked(TrixieMaintenanceOutcome.Deferred, "another DwemerDistro operation is using the distro, so the unfinished system upgrade waits");
        deadline?.Pause();
        try
        {
            log("Finishing the Debian Trixie upgrade step an earlier launch left unfinished." + Environment.NewLine);
            if (entry.Phase == TrixieJournalPhase.RecoveryRequired) return await ReleaseAsync(entry, log).ConfigureAwait(false);
            if (entry.Phase == TrixieJournalPhase.Restoring)
                return entry.SwapStep == 4
                    ? await ReleaseRestoredAsync(entry, entry.Note ?? "an earlier restore", log).ConfigureAwait(false)
                    : await RestoreAsync(entry, entry.Note ?? "an earlier restore", log).ConfigureAwait(false);
            if (entry.Phase == TrixieJournalPhase.Releasing) return await ReleaseAsync(entry, log).ConfigureAwait(false);
            if (Revalidate(entry.Registration, LauncherConstants.DistroName) is { } moved) return Manual(entry, moved);
            switch (entry.Phase)
            {
                case TrixieJournalPhase.Preparing or TrixieJournalPhase.Fenced or TrixieJournalPhase.Exporting or TrixieJournalPhase.Exported
                    or TrixieJournalPhase.ProvingCopy or TrixieJournalPhase.Cancelling:
                    return await CancelAsync(entry, entry.Note ?? "an earlier launch stopped before the recovery copy was proven", log).ConfigureAwait(false);
                case TrixieJournalPhase.Upgraded:
                    return await VerifyAsync(entry, log).ConfigureAwait(false);
            }

            // CopyProven or Upgrading: what Core says now decides.
            var status = await StatusAsync().ConfigureAwait(false);
            switch (status?.State)
            {
                case null:
                    return Blocked(TrixieMaintenanceOutcome.Deferred,
                        "an upgrade from an earlier launch may still be running inside the distro (it did not answer, or a lock is held); distro writes stay blocked until it ends");
                case "fenced" or "interrupted":
                    if (!Corroborates(entry.InstanceId, status.InstanceId)) return Manual(entry, "Core reports another instance id");
                    return await UpgradeAsync(entry, log).ConfigureAwait(false);
                case "upgraded":
                    return await VerifyAsync(Save(entry, TrixieJournalPhase.Upgraded), log).ConfigureAwait(false);
                case "verified":
                    return await ReleaseAsync(entry, log).ConfigureAwait(false);
                case "current" when status.Fence != true:
                    // Released by an earlier run: releasing again records the boundary and only starts what is still stopped.
                    return await ReleaseAsync(entry, log).ConfigureAwait(false);
                default:
                    return Manual(entry, $"Core reports '{status.State}' after an interrupted upgrade");
            }
        }
        finally
        {
            deadline?.Resume();
        }
    }

    // --- Helpers -------------------------------------------------------------------------------------------------

    private async Task<TrixieCoreReply?> StatusAsync()
    {
        var result = await host.RunCoreAsync(BuildStatusArguments(), _ => { }, CancellationToken.None).ConfigureAwait(false);
        return result.Succeeded ? ParseReply(result.StandardOutput) : null;
    }

    /// <summary>
    /// A fresh upgrade is started only by the user's own confirmation, so it never waits for a game to close: an open
    /// game or a running server postpones it, and the user is asked to close it and confirm again.
    /// </summary>
    private TrixieMaintenanceResult? IdleProblem()
    {
        var games = host.FindRunningGames();
        if (games.Count > 0)
            return new(TrixieMaintenanceOutcome.Deferred,
                $"{string.Join(", ", games)} is running, so Upgrade DwemerDistro did not start the Debian 13 upgrade; nothing was changed. " +
                "Close the game, then choose Upgrade DwemerDistro again");
        return host.IsServerRunning()
            ? new(TrixieMaintenanceOutcome.Deferred, "the Debian Trixie upgrade waits until the server is stopped; nothing was changed")
            : null;
    }

    /// <summary>Null while the named registration is still exactly the recorded one: GUID, folder, disk name and disk file.</summary>
    private string? Revalidate(TrixieRegistration expected, string name)
    {
        var current = host.ReadRegistration(name);
        return SameRegistrationFields(expected, current) && current!.DiskFileId == expected.DiskFileId && expected.DiskFileId is not null
            ? null
            : $"the WSL registration of {name} changed";
    }

    private static bool SameRegistrationFields(TrixieRegistration expected, TrixieRegistration? current) =>
        current is not null && SameId(expected, current) && string.Equals(expected.Name, current.Name, StringComparison.OrdinalIgnoreCase)
        && SamePath(expected.BasePath, current.BasePath) && string.Equals(expected.VhdFileName, current.VhdFileName, StringComparison.OrdinalIgnoreCase);

    private static bool SameId(TrixieRegistration a, TrixieRegistration b) =>
        a.Id.Length > 0 && string.Equals(a.Id, b.Id, StringComparison.OrdinalIgnoreCase);

    private static bool SamePath(string a, string b) =>
        string.Equals(Path.TrimEndingDirectorySeparator(a), Path.TrimEndingDirectorySeparator(b), StringComparison.OrdinalIgnoreCase);

    /// <summary>Instance ids only corroborate: both present and different refuses; one missing proves nothing either way.</summary>
    private static bool Corroborates(string? recorded, string? reported) =>
        string.IsNullOrEmpty(recorded) || string.IsNullOrEmpty(reported) || string.Equals(recorded, reported, StringComparison.Ordinal);

    private static bool FixedEquals(string a, string b) =>
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));

    private static string TokenPurpose(TrixieJournal entry) => "DwemerDistro.TrixieCaptureToken:" + entry.Registration.Id.ToLowerInvariant();

    private static string? NullIfEmpty(string? value) => string.IsNullOrEmpty(value) ? null : value;

    private TrixieJournal Save(TrixieJournal entry, TrixieJournalPhase phase, string? note = null)
    {
        var saved = entry with { Phase = phase, UpdatedAt = host.Now(), Note = note ?? (phase == entry.Phase ? entry.Note : null) };
        journal.Write(saved);
        return saved;
    }

    /// <summary>No automatic action: the state is past the gameplay-write boundary, unknown, or not what a restore needs.</summary>
    private TrixieMaintenanceResult Manual(TrixieJournal entry, string reason)
    {
        Save(entry, TrixieJournalPhase.RecoveryRequired, reason);
        return Blocked(TrixieMaintenanceOutcome.RecoveryRequired, reason + CopyClause(entry) + ". Nothing was imported, unregistered or deleted automatically");
    }

    /// <summary>Deletes only the launcher's own unverified partial export inside the backup folder.</summary>
    private void TryDeleteOwnedPartialExport(string path)
    {
        try
        {
            var full = Path.GetFullPath(path);
            var folder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(host.BackupDirectory));
            if (string.Equals(Path.GetDirectoryName(full), folder, StringComparison.OrdinalIgnoreCase)
                && Path.GetFileName(full).StartsWith(BackupFilePrefix, StringComparison.OrdinalIgnoreCase)
                && full.EndsWith(".tar", StringComparison.OrdinalIgnoreCase)
                && File.Exists(full))
                File.Delete(full);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A leftover partial copy only costs space.
        }
    }

    private TrixieMaintenanceResult Blocked(TrixieMaintenanceOutcome outcome, string message) =>
        new(outcome, message, LeavesWritesBlocked: TrixieJournalStore.BlocksWrites(journal.Read(), host.ReadRegistration(LauncherConstants.DistroName)?.Id)
                                                   || outcome == TrixieMaintenanceOutcome.RecoveryRequired);

    private TrixieMaintenanceResult Busy(string tail) =>
        Blocked(TrixieMaintenanceOutcome.Deferred, "another maintenance operation holds the distro's lock, so the Debian Trixie upgrade waits; " + tail);

    private static string CopyClause(TrixieJournal entry) =>
        (entry.BackupPath is { } path && entry.BackupSha256 is not null
            ? $". The offline recovery copy made inside Core's fence is {path} (SHA-256 {entry.BackupSha256})"
            : string.Empty)
        + (entry.Recovery is { Id.Length: > 0 } copy ? $"; its checked import is the WSL distro {copy.Name}" : string.Empty);

    private static string LeftoverCopyClause(TrixieJournal entry) =>
        entry.Recovery is { Id.Length: > 0 } copy
            ? $". The recovery copy's check left the WSL distro {copy.Name}" + (entry.BackupSha256 is not null ? $" and {entry.BackupPath}" : string.Empty) + "; remove them once no longer needed"
            : entry.BackupSha256 is not null ? $". The unused recovery copy {entry.BackupPath} can be deleted" : string.Empty;

    private static string KeptCopyClause(TrixieJournal entry) =>
        entry.BackupPath is { } path
            ? $" The Bookworm recovery copy is kept at {path}" + (entry.Recovery is { Id.Length: > 0 } copy ? $" and as the WSL distro {copy.Name}" : string.Empty) +
              "; neither matches once the distro is used, so remove them when no longer needed."
            : string.Empty;

    /// <summary>What Core's restore record says did not start, and why; empty from a Core that reports none.</summary>
    private static string PendingClause(TrixieCoreRestore? restore) =>
        restore is null ? string.Empty
        : (restore.Failed.Length > 0 ? " (" + DiagnosticEvidenceService.Sanitize(string.Join(", ", restore.Failed)) + " did not start)" : string.Empty)
          + (restore.Problems.Length > 0 ? ": " + DiagnosticEvidenceService.Sanitize(restore.Problems) : string.Empty);

    private static string ReasonClause(TrixieCoreReply? reply) =>
        reply is { Reasons.Length: > 0 } ? ": " + DiagnosticEvidenceService.Sanitize(reply.Reasons) : string.Empty;

    private static string LastLine(CommandResult result)
    {
        var text = string.IsNullOrWhiteSpace(result.StandardError) ? result.StandardOutput : result.StandardError;
        var line = (text ?? string.Empty).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault();
        return string.IsNullOrEmpty(line) ? "no output" : DiagnosticEvidenceService.Sanitize(line);
    }

    /// <summary>
    /// Proves a finished <c>wsl --export</c>: a tar header first, the two zero end-of-archive blocks last, at least
    /// <paramref name="minimumBytes"/>, and its SHA-256 for the journal and Core's record.
    /// </summary>
    internal static async Task<(bool Ok, long Bytes, string? Sha256, string Reason)> VerifyTarArchiveAsync(
        string path, long minimumBytes, CancellationToken token)
    {
        if (!File.Exists(path)) return (false, 0, null, "the file is missing");
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.SequentialScan | FileOptions.Asynchronous);
        var length = stream.Length;
        if (length < Math.Max(minimumBytes, 2048) || length % 512 != 0) return (false, length, null, $"it is {length:N0} bytes, which is too small or not whole tar blocks");
        var header = new byte[512];
        await stream.ReadExactlyAsync(header, token).ConfigureAwait(false);
        if (!header.AsSpan(257, 5).SequenceEqual("ustar"u8)) return (false, length, null, "it does not start with a tar header");
        var tail = new byte[1024];
        stream.Seek(-1024, SeekOrigin.End);
        await stream.ReadExactlyAsync(tail, token).ConfigureAwait(false);
        if (tail.AsSpan().ContainsAnyExcept((byte)0)) return (false, length, null, "it does not end with the tar end-of-archive blocks");
        stream.Seek(0, SeekOrigin.Begin);
        var hash = await SHA256.HashDataAsync(stream, token).ConfigureAwait(false);
        return (true, length, Convert.ToHexString(hash).ToLowerInvariant(), string.Empty);
    }
}

/// <summary>A disk the restore must rename was still held by another process when its bounded wait ran out.</summary>
internal sealed class DiskStillOpenException(string message) : IOException(message);
