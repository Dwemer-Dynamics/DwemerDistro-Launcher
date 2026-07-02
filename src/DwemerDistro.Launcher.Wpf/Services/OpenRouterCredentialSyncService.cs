using System.Text;
using System.Text.Json;

namespace DwemerDistro.Launcher.Wpf.Services;

public sealed class OpenRouterCredentialSyncService(WslService wsl)
{
    public async Task<OpenRouterSyncStatus> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        await EnsurePostgresStartedAsync(cancellationToken).ConfigureAwait(false);
        var result = await wsl.RunDistroAsUserAsync(
                LauncherConstants.DistroUser,
                new[] { "python3", "-c", StatusScript },
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        return ParseStatus(result, updated: false);
    }

    public async Task<OpenRouterSyncStatus> SaveKeyAsync(string apiKey, CancellationToken cancellationToken = default)
    {
        var normalizedKey = (apiKey ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(normalizedKey))
        {
            return OpenRouterSyncStatus.Unknown("Enter an OpenRouter API key.");
        }

        var encodedKey = Convert.ToBase64String(Encoding.UTF8.GetBytes(normalizedKey));
        await EnsurePostgresStartedAsync(cancellationToken).ConfigureAwait(false);
        var result = await wsl.RunDistroAsUserAsync(
                LauncherConstants.DistroUser,
                new[] { "python3", "-c", SaveScript, encodedKey },
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        return ParseStatus(result, updated: result.Succeeded);
    }

    private async Task EnsurePostgresStartedAsync(CancellationToken cancellationToken)
    {
        try
        {
            await wsl.RunDistroAsync(new[] { "service", "postgresql", "start" }, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
        }
        catch
        {
            // The psql probe below reports the actionable status.
        }
    }

    private static OpenRouterSyncStatus ParseStatus(Models.CommandResult result, bool updated)
    {
        if (!result.Succeeded)
        {
            return OpenRouterSyncStatus.Unknown(BuildCommandError(result));
        }

        try
        {
            var items = JsonSerializer.Deserialize<List<OpenRouterTargetProbe>>(result.StandardOutput.Trim(), JsonOptions)
                        ?? [];
            var targets = items
                .Select(item => new OpenRouterTargetStatus(
                    item.TargetName ?? item.DatabaseName ?? "Unknown",
                    item.DatabaseName ?? string.Empty,
                    item.Configured,
                    updated && item.Updated,
                    item.Skipped,
                    item.StatusText ?? BuildStatusText(item),
                    item.Error))
                .ToArray();

            return new OpenRouterSyncStatus(targets, null);
        }
        catch (JsonException)
        {
            return OpenRouterSyncStatus.Unknown("Unable to parse OpenRouter status.");
        }
    }

    private static string BuildStatusText(OpenRouterTargetProbe item)
    {
        if (item.Skipped)
        {
            return "Skipped";
        }

        if (item.Updated)
        {
            return "Updated";
        }

        return item.Configured ? "Configured" : "Needs key";
    }

    private static string BuildCommandError(Models.CommandResult result)
    {
        var text = (result.StandardError + result.StandardOutput).Trim();
        return string.IsNullOrWhiteSpace(text) ? $"Exit code {result.ExitCode}" : text;
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private const string CommonPython = """
import base64
import json
import os
import subprocess
import sys

TARGETS = [
    {"targetName": "CHIM / Skyrim", "databaseName": "dwemer"},
    {"targetName": "STOBE / Kenshi", "databaseName": "stobe"},
    {"targetName": "Dialectic / Fallout NV", "databaseName": "dialectic"},
]

def psql(db, sql):
    env = os.environ.copy()
    env["PGPASSWORD"] = env.get("PGPASSWORD") or "dwemer"
    return subprocess.run(
        ["psql", "-h", "127.0.0.1", "-U", "dwemer", "-d", db, "-At", "-c", sql],
        text=True,
        capture_output=True,
        timeout=20,
        env=env,
    )

def sql_literal(value):
    return "'" + str(value).replace("'", "''") + "'"

def table_exists(db, table_name):
    probe = psql(db, "SELECT to_regclass('public." + table_name + "') IS NOT NULL;")
    if probe.returncode != 0:
        return False, (probe.stderr or probe.stdout).strip()
    return probe.stdout.strip().lower() in ("t", "true", "1"), None

def read_status(db, target_name):
    exists, error = table_exists(db, "core_api_badge")
    if error:
        return {
            "targetName": target_name,
            "databaseName": db,
            "configured": False,
            "updated": False,
            "skipped": True,
            "statusText": "Database unavailable",
            "error": error,
        }
    if not exists:
        return {
            "targetName": target_name,
            "databaseName": db,
            "configured": False,
            "updated": False,
            "skipped": True,
            "statusText": "API key table not found",
            "error": None,
        }

    result = psql(db, "SELECT COALESCE(api_key, '') FROM core_api_badge WHERE LOWER(label) = 'openrouter' ORDER BY id LIMIT 1;")
    if result.returncode != 0:
        return {
            "targetName": target_name,
            "databaseName": db,
            "configured": False,
            "updated": False,
            "skipped": False,
            "statusText": "Unable to read key",
            "error": (result.stderr or result.stdout).strip(),
        }

    configured = bool(result.stdout.strip())
    return {
        "targetName": target_name,
        "databaseName": db,
        "configured": configured,
        "updated": False,
        "skipped": False,
        "statusText": "Configured" if configured else "Needs key",
        "error": None,
    }
""";

    private static readonly string StatusScript = CommonPython + """

statuses = [read_status(item["databaseName"], item["targetName"]) for item in TARGETS]
print(json.dumps(statuses))
""";

    private static readonly string SaveScript = CommonPython + """"

api_key = base64.b64decode(sys.argv[1]).decode("utf-8").strip()
statuses = []

for item in TARGETS:
    db = item["databaseName"]
    target_name = item["targetName"]
    exists, error = table_exists(db, "core_api_badge")
    if error:
        statuses.append({
            "targetName": target_name,
            "databaseName": db,
            "configured": False,
            "updated": False,
            "skipped": True,
            "statusText": "Database unavailable",
            "error": error,
        })
        continue
    if not exists:
        statuses.append({
            "targetName": target_name,
            "databaseName": db,
            "configured": False,
            "updated": False,
            "skipped": True,
            "statusText": "API key table not found",
            "error": None,
        })
        continue

    key_literal = sql_literal(api_key)
    sql = """
DO $$
DECLARE
    badge_id integer;
BEGIN
    SELECT id INTO badge_id
    FROM core_api_badge
    WHERE LOWER(label) = 'openrouter'
    ORDER BY id
    LIMIT 1;

    IF badge_id IS NULL THEN
        INSERT INTO core_api_badge(label, api_key)
        VALUES ('OpenRouter', __API_KEY__);
    ELSE
        UPDATE core_api_badge
        SET api_key = __API_KEY__
        WHERE id = badge_id;
    END IF;
END
$$ LANGUAGE plpgsql;
""".replace("__API_KEY__", key_literal)

    result = psql(db, sql)
    if result.returncode != 0:
        statuses.append({
            "targetName": target_name,
            "databaseName": db,
            "configured": False,
            "updated": False,
            "skipped": False,
            "statusText": "Update failed",
            "error": (result.stderr or result.stdout).strip(),
        })
        continue

    statuses.append({
        "targetName": target_name,
        "databaseName": db,
        "configured": True,
        "updated": True,
        "skipped": False,
        "statusText": "Updated",
        "error": None,
    })

print(json.dumps(statuses))
"""";

    private sealed class OpenRouterTargetProbe
    {
        public string? TargetName { get; set; }

        public string? DatabaseName { get; set; }

        public bool Configured { get; set; }

        public bool Updated { get; set; }

        public bool Skipped { get; set; }

        public string? StatusText { get; set; }

        public string? Error { get; set; }
    }
}

public sealed record OpenRouterTargetStatus(
    string TargetName,
    string DatabaseName,
    bool IsConfigured,
    bool WasUpdated,
    bool IsSkipped,
    string StatusText,
    string? Error);

public sealed record OpenRouterSyncStatus(
    IReadOnlyList<OpenRouterTargetStatus> Targets,
    string? Error)
{
    public bool HasError => !string.IsNullOrWhiteSpace(Error);

    public bool AnyConfigured => Targets.Any(target => target.IsConfigured);

    public bool AnyUpdated => Targets.Any(target => target.WasUpdated);

    public bool AllAvailableTargetsConfigured =>
        Targets.Any(target => !target.IsSkipped) &&
        Targets.Where(target => !target.IsSkipped).All(target => target.IsConfigured);

    public static OpenRouterSyncStatus Unknown(string error)
    {
        return new OpenRouterSyncStatus([], error);
    }
}
