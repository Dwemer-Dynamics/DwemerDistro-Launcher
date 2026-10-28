using System.Text.Json;
using System.Text.Json.Nodes;
using DwemerDistro.Launcher.Wpf.Services;

namespace DwemerDistro.Launcher.Wpf.ViewModels;

/// <summary>
/// Task recovery (revisions 17 to 20): when a chat connects, the launcher reads Core's per-task journal for the chat's thread and its few
/// earlier threads through <see cref="RecoveryMethod"/>, hands the assistant a bounded summary with its next message, and shows one compact
/// line only when something needs the user's attention. Job states are the broker's records; checkpoints are the assistant's own
/// unverified notes. Nothing is replayed, reattached or offered as a rerun, and a job of another session is never shown as controllable.
/// </summary>
public sealed partial class AssistantViewModel
{
    internal const string RecoveryMethod = "ddistro/task/recovery";
    // The chat's thread plus at most this many earlier (superseded) threads of the task, newest first.
    internal const int MaxRecoveryEarlierThreads = 3;
    internal const int MaxRecoveryCheckpoints = 3;
    internal const int MaxRecoveryJobs = 8;
    internal const int MaxRecoveryContextChars = 4000;

    internal const string RecoveryContextLabel =
        "[Task recovery - read by the launcher from DwemerDistro's journal when this chat connected. Job states are DwemerDistro's " +
        "records; checkpoints are your own earlier unverified notes, not facts. Nothing was replayed or undone. Inspect the fresh state " +
        "before continuing and never rerun a write automatically.]";

    internal sealed record RecoveryCheckpoint(string Id, string RecordedAt, string Progress, string NextStep, string VerificationClaim,
        IReadOnlyList<string> BackupRefs, IReadOnlyList<string> JobIds);

    internal sealed record RecoveryJob(string Id, string State, string Recovery, int? ExitCode, string Risk, string At);

    /// <summary>
    /// One chat's recovery state. Unavailable is never empty: it carries Core's reason, so a failed read is not mistaken for nothing to
    /// recover. EarlierUnavailable counts earlier threads whose journal could not be read.
    /// </summary>
    internal sealed record RecoverySnapshot(bool Available, string Reason, IReadOnlyList<RecoveryCheckpoint> Checkpoints,
        IReadOnlyList<RecoveryJob> Jobs, int EarlierUnavailable = 0, int DroppedIncomplete = 0)
    {
        public int Count(string recovery) => Jobs.Count(job => job.Recovery == recovery);
        // IsEmpty decides whether the assistant gets a summary; routine checkpoints and finished jobs still go with the next message.
        public bool IsEmpty => Available && Checkpoints.Count == 0 && Jobs.All(job => job.Recovery == "none") && EarlierUnavailable == 0
                               && DroppedIncomplete == 0;
        // NeedsAttention decides whether the user sees a line: only an unknown outcome, another session's job, or an unreadable journal.
        // Checkpoints, finished-not-verified jobs (cancelled included) and this session's running jobs stay quiet.
        public bool NeedsAttention => !Available || Count("interrupted") > 0 || Count("other_session_active") > 0 || EarlierUnavailable > 0
                                      || DroppedIncomplete > 0;
        internal static RecoverySnapshot Unavailable(string reason) => new(false, reason, [], []);
    }

    private static readonly string[] RecoveryStates = ["none", "verify_before_continuing", "live_this_session", "interrupted", "other_session_active"];

    private RecoverySnapshot? _recovery;
    // The summary goes with the next message only, once per load; a reconnect or chat switch reads the journal again.
    private bool _recoveryContextPending;
    private int _recoveryGeneration;

    /// <summary>Shown only for a chat whose session negotiated task recovery and whose journal needs attention, or could not be read.</summary>
    public bool HasRecoveryStatus => _recovery is { NeedsAttention: true };
    public bool IsRecoveryUnavailable => _recovery is { Available: false };
    public string RecoveryStatusText => _recovery is { NeedsAttention: true } recovery ? DescribeRecovery(recovery) : string.Empty;

    private void SetRecovery(RecoverySnapshot? recovery, bool supplyContext)
    {
        _recovery = recovery;
        _recoveryContextPending = supplyContext && recovery is { IsEmpty: false };
        foreach (var name in new[] { nameof(HasRecoveryStatus), nameof(IsRecoveryUnavailable), nameof(RecoveryStatusText) })
            OnPropertyChanged(name);
    }

    /// <summary>Forgets the shown recovery state and any read still in flight: a chat switch or a closed session reads it again.</summary>
    private void ClearRecovery()
    {
        _recoveryGeneration++;
        SetRecovery(null, false);
    }

    /// <summary>
    /// Reads the journal for the chat's thread and its newest earlier threads, one request each. A failed current read makes recovery
    /// unavailable with its reason; a failed earlier read is counted. A read that finishes after the chat or session changed is dropped.
    /// </summary>
    private async Task LoadRecoveryAsync(CodexAppServerClient client, bool supplyContext)
    {
        if (!HasTaskRecovery(_toolRevision) || _threadId is not { } threadId) return;
        var generation = ++_recoveryGeneration;
        var earlier = _supersededThreads.Where(thread => thread != threadId).Reverse().Take(MaxRecoveryEarlierThreads).ToList();
        RecoverySnapshot? current = null;
        var parts = new List<RecoverySnapshot>();
        var earlierUnavailable = 0;
        foreach (var thread in earlier.Prepend(threadId))
        {
            RecoverySnapshot view;
            try
            {
                view = ReadRecoveryView(await client.RequestAsync(RecoveryMethod, new JsonObject { ["threadId"] = thread }, RpcTimeout));
            }
            catch (AppServerException ex)
            {
                view = RecoverySnapshot.Unavailable(ex.Code == 0 ? "no answer from DwemerDistro" : Shorten(ex.Message, 80));
            }
            if (generation != _recoveryGeneration || !ReferenceEquals(_client, client) || _threadId != threadId) return;
            if (thread == threadId) current = view;
            else if (!view.Available) earlierUnavailable++;
            if (view.Available) parts.Add(view);
        }
        SetRecovery(current is { Available: false } ? current : MergeRecovery(parts, earlierUnavailable), supplyContext);
    }

    /// <summary>Core's recovery view, strictly: anything but recovery_version 1 with available true is unavailable, with Core's reason.</summary>
    internal static RecoverySnapshot ReadRecoveryView(JsonElement view)
    {
        if (view.ValueKind != JsonValueKind.Object || Prop(view, "recovery_version") is not { ValueKind: JsonValueKind.Number } version
            || !version.TryGetInt32(out var number) || number != 1)
            return RecoverySnapshot.Unavailable("unsupported recovery answer");
        if (Prop(view, "available").ValueKind != JsonValueKind.True)
            return RecoverySnapshot.Unavailable(AssistantCliService.GetString(view, "reason") is { Length: > 0 } reason && ActivityToken.IsMatch(reason)
                ? reason : "journal unavailable");
        var jobs = new List<RecoveryJob>();
        if (Prop(view, "jobs") is { ValueKind: JsonValueKind.Array } jobArray)
            foreach (var job in jobArray.EnumerateArray())
            {
                var id = AssistantCliService.GetString(job, "job_id");
                if (!RunJobId.IsMatch(id)) continue;
                // An unknown recovery value is never read as harmless: it counts as an interrupted job with an unknown outcome.
                var recovery = AssistantCliService.GetString(job, "recovery") is var value && RecoveryStates.Contains(value) ? value : "interrupted";
                var state = AssistantCliService.GetString(job, "state");
                int? exit = Prop(job, "exit_code") is { ValueKind: JsonValueKind.Number } code && code.TryGetInt32(out var exitCode) ? exitCode : null;
                var risk = AssistantCliService.GetString(job, "risk");
                jobs.Add(new RecoveryJob(id, ActivityToken.IsMatch(state) ? state : "unknown", recovery, exit,
                    risk is "routine" or "destructive" ? risk : string.Empty,
                    Stamp(AssistantCliService.GetString(job, "finished_at") is { Length: > 0 } finished ? finished : AssistantCliService.GetString(job, "recorded_at"))));
            }
        var checkpoints = new List<RecoveryCheckpoint>();
        if (Prop(view, "checkpoints") is { ValueKind: JsonValueKind.Array } checkpointArray)
            foreach (var checkpoint in checkpointArray.EnumerateArray())
            {
                var progress = CleanClaim(AssistantCliService.GetString(checkpoint, "progress"), 300);
                if (progress.Length == 0) continue;
                checkpoints.Add(new RecoveryCheckpoint(CleanClaim(AssistantCliService.GetString(checkpoint, "id"), 64),
                    Stamp(AssistantCliService.GetString(checkpoint, "recorded_at")), progress,
                    CleanClaim(AssistantCliService.GetString(checkpoint, "next_step"), 300),
                    CleanClaim(AssistantCliService.GetString(checkpoint, "verification_claim"), 300),
                    ClaimList(Prop(checkpoint, "backup_refs"), 5, 256),
                    ClaimList(Prop(checkpoint, "job_ids"), 8, 32).Where(id => RunJobId.IsMatch(id)).ToList()));
            }
        var dropped = Prop(view, "dropped_incomplete") is { ValueKind: JsonValueKind.Number } drop && drop.TryGetInt32(out var count) && count > 0 ? count : 0;
        return new RecoverySnapshot(true, string.Empty, checkpoints, jobs, 0, dropped);
    }

    /// <summary>One chat's threads together: jobs once each, checkpoints oldest first, both bounded to the newest.</summary>
    internal static RecoverySnapshot MergeRecovery(IReadOnlyList<RecoverySnapshot> parts, int earlierUnavailable = 0) => new(true, string.Empty,
        parts.SelectMany(part => part.Checkpoints).DistinctBy(checkpoint => checkpoint.Id.Length > 0 ? checkpoint.Id : checkpoint.RecordedAt + checkpoint.Progress)
            .OrderBy(checkpoint => checkpoint.RecordedAt, StringComparer.Ordinal).TakeLast(MaxRecoveryCheckpoints).ToList(),
        parts.SelectMany(part => part.Jobs).DistinctBy(job => job.Id).ToList(),
        // Core's dropped_incomplete counts the whole task's journal, so every thread's view repeats it: count it once.
        earlierUnavailable, parts.Count == 0 ? 0 : parts.Max(part => part.DroppedIncomplete));

    /// <summary>
    /// The compact line, in plain words, for attention cases only: interrupted jobs, another session's job, dropped records, unreadable
    /// threads, or an unavailable journal. Checkpoints and finished jobs are left to the assistant's context. A job owned by another live
    /// session is never called running here, and a job from an earlier session is never controllable. Empty when nothing needs attention.
    /// </summary>
    internal static string DescribeRecovery(RecoverySnapshot recovery)
    {
        if (!recovery.Available) return "Recovery unavailable: " + recovery.Reason.Replace('_', ' ') + ". Ask the assistant to check the current state before continuing.";
        var issues = new List<string>();
        if (recovery.Count("interrupted") is > 0 and var interrupted)
            issues.Add(interrupted == 1 ? "A previous command was interrupted, outcome unknown" : $"{interrupted} previous commands were interrupted, outcomes unknown");
        if (recovery.Count("other_session_active") is > 0 and var elsewhere)
            issues.Add(elsewhere == 1 ? "A command belongs to another open session and can't be controlled here"
                : $"{elsewhere} commands belong to another open session and can't be controlled here");
        if (recovery.DroppedIncomplete > 0)
            issues.Add(recovery.DroppedIncomplete == 1 ? "An unfinished command was dropped from the record, outcome unknown"
                : $"{recovery.DroppedIncomplete} unfinished commands were dropped from the record, outcomes unknown");
        if (recovery.EarlierUnavailable > 0)
            issues.Add(recovery.EarlierUnavailable == 1 ? "An earlier part of this chat's record couldn't be read"
                : $"{recovery.EarlierUnavailable} earlier parts of this chat's record couldn't be read");
        return issues.Count == 0 ? string.Empty : string.Join(". ", issues) + ". Nothing was rerun; ask the assistant to check before continuing.";
    }

    /// <summary>
    /// The bounded summary sent with the first message after a load: the newest checkpoints as the assistant's own claims and the jobs
    /// that need attention, each with DwemerDistro's recorded state. Never code or output; at most <see cref="MaxRecoveryContextChars"/>.
    /// </summary>
    internal static string BuildRecoveryContext(RecoverySnapshot recovery)
    {
        var text = new System.Text.StringBuilder(RecoveryContextLabel);
        if (!recovery.Available)
            return text.Append("\nRecovery is unavailable (").Append(recovery.Reason).Append("): the journal could not be read, so earlier progress and ")
                .Append("job outcomes are unknown. Say so if it matters, and inspect the current state.").ToString();
        foreach (var checkpoint in recovery.Checkpoints)
        {
            text.Append("\nCheckpoint").Append(checkpoint.RecordedAt.Length > 0 ? " " + checkpoint.RecordedAt : string.Empty)
                .Append(" (your claim, verified: false): progress: ").Append(checkpoint.Progress);
            if (checkpoint.NextStep.Length > 0) text.Append(" | next_step: ").Append(checkpoint.NextStep);
            if (checkpoint.VerificationClaim.Length > 0) text.Append(" | verification claimed: ").Append(checkpoint.VerificationClaim);
            if (checkpoint.BackupRefs.Count > 0) text.Append(" | backup_refs: ").Append(string.Join(", ", checkpoint.BackupRefs));
            if (checkpoint.JobIds.Count > 0) text.Append(" | job_ids: ").Append(string.Join(", ", checkpoint.JobIds));
        }
        var attention = recovery.Jobs.Where(job => job.Recovery != "none").TakeLast(MaxRecoveryJobs).ToList();
        foreach (var job in attention)
            text.Append("\nJob ").Append(job.Id).Append(" (recorded by DwemerDistro): ").Append(job.Recovery switch
            {
                "interrupted" => "interrupted, outcome unknown; it may have changed things partway",
                "other_session_active" => "owned by another open session, outcome unknown; not controllable from here",
                "live_this_session" => "still running in this session",
                _ => "ended as " + job.State + (job.ExitCode is { } exit ? " with exit code " + exit : string.Empty) + "; not verified"
            }).Append(job.Risk.Length > 0 ? ", risk " + job.Risk : string.Empty);
        if (recovery.Jobs.Count(job => job.Recovery != "none") > attention.Count) text.Append("\n(Older jobs omitted; use the recovery operation.)");
        if (recovery.DroppedIncomplete > 0) text.Append($"\n{recovery.DroppedIncomplete} unfinished job(s) were dropped from the journal; their outcome is unknown.");
        if (recovery.EarlierUnavailable > 0) text.Append($"\nThe journal of {recovery.EarlierUnavailable} earlier thread(s) of this chat could not be read.");
        var result = text.ToString();
        return result.Length <= MaxRecoveryContextChars ? result : result[..MaxRecoveryContextChars] + "\n(Shortened; use the recovery operation.)";
    }

    /// <summary>The model's text as one plain line: no control characters, and no launcher labels it could pose as.</summary>
    private static string CleanClaim(string value, int max)
    {
        var line = string.Join(' ', new string(value.Select(c => char.IsControl(c) || char.GetUnicodeCategory(c) == System.Globalization.UnicodeCategory.Format ? ' ' : c).ToArray())
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)).Replace("[", "(").Replace("]", ")");
        return line.Length <= max ? line : line[..max];
    }

    private static IReadOnlyList<string> ClaimList(JsonElement array, int count, int max) => array.ValueKind == JsonValueKind.Array
        ? array.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String).Select(item => CleanClaim(item.GetString() ?? string.Empty, max))
            .Where(item => item.Length > 0).Take(count).ToList()
        : [];

    private static string Stamp(string value) =>
        DateTimeOffset.TryParse(value, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out var when)
            ? when.ToUniversalTime().ToString("yyyy-MM-dd HH:mm 'UTC'", System.Globalization.CultureInfo.InvariantCulture)
            : string.Empty;
}
