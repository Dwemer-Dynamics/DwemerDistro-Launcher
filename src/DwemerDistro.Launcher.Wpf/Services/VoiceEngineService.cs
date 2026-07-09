using System.Text.Json;

namespace DwemerDistro.Launcher.Wpf.Services;

public sealed class VoiceEngineService(WslService wsl)
{
    public async Task<VoiceEngineStatus> GetStatusAsync(
        SetupPreset preset,
        CancellationToken cancellationToken = default)
    {
        var result = await wsl.RunDistroAsUserAsync(
                LauncherConstants.DistroUser,
                new[] { "python3", "-c", ProbeScript },
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        if (!result.Succeeded)
        {
            return VoiceEngineStatus.Unknown("Unable to check voice engines.");
        }

        Dictionary<string, bool>? installed;
        try
        {
            installed = JsonSerializer.Deserialize<Dictionary<string, bool>>(result.StandardOutput.Trim(), JsonOptions);
        }
        catch (JsonException)
        {
            installed = null;
        }

        if (installed is null)
        {
            return VoiceEngineStatus.Unknown("Unable to parse voice engine status.");
        }

        var preferredInstalled = installed.TryGetValue(preset.VoiceEngineKey, out var hasPreferred) && hasPreferred;
        var activeEngine = preferredInstalled
            ? preset.VoiceEngineKey
            : installed.FirstOrDefault(item => item.Value && IsClonedVoiceEngine(item.Key)).Key;

        if (string.IsNullOrWhiteSpace(activeEngine))
        {
            return new VoiceEngineStatus(
                false,
                preset.VoiceEngineKey,
                preset.VoiceEngineName,
                "No cloned voice engine detected yet.",
                installed);
        }

        return new VoiceEngineStatus(
            true,
            activeEngine,
            GetDisplayName(activeEngine),
            $"{GetDisplayName(activeEngine)} detected and ready to apply.",
            installed);
    }

    public async Task<IReadOnlyList<VoiceEngineApplyTargetStatus>> ApplyVoiceEngineAsync(
        string engineKey,
        CancellationToken cancellationToken = default)
    {
        await EnsurePostgresStartedAsync(cancellationToken).ConfigureAwait(false);
        var normalizedEngine = NormalizeEngineKey(engineKey);
        var result = await wsl.RunDistroAsUserAsync(
                LauncherConstants.DistroUser,
                new[] { "python3", "-c", ApplyScript, normalizedEngine },
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        if (!result.Succeeded)
        {
            return
            [
                new VoiceEngineApplyTargetStatus(
                    "DwemerDistro",
                    string.Empty,
                    false,
                    false,
                    "Apply failed",
                    BuildCommandError(result))
            ];
        }

        try
        {
            var targets = JsonSerializer.Deserialize<List<VoiceEngineApplyTargetProbe>>(result.StandardOutput.Trim(), JsonOptions)
                          ?? [];
            return targets
                .Select(target => new VoiceEngineApplyTargetStatus(
                    target.TargetName ?? target.DatabaseName ?? "Unknown",
                    target.DatabaseName ?? string.Empty,
                    target.Applied,
                    target.Skipped,
                    target.StatusText ?? (target.Applied ? "Applied" : "Skipped"),
                    target.Error))
                .ToArray();
        }
        catch (JsonException)
        {
            return
            [
                new VoiceEngineApplyTargetStatus(
                    "DwemerDistro",
                    string.Empty,
                    false,
                    false,
                    "Apply failed",
                    "Unable to parse voice engine apply status.")
            ];
        }
    }

    public static string GetDisplayName(string engineKey)
    {
        return NormalizeEngineKey(engineKey) switch
        {
            "chatterbox" => "Chatterbox",
            "omnivoice" => "Multilingual OmniVoice",
            "pockettts" => "Pocket-TTS",
            _ => "Cloned voice engine"
        };
    }

    public static string NormalizeEngineKey(string engineKey)
    {
        return (engineKey ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "chatterbox" => "chatterbox",
            "omnivoice" or "omni_voice" or "omni-voice" => "omnivoice",
            "pocket_tts" or "pocket-tts" or "pockettts" => "pockettts",
            _ => "pockettts"
        };
    }

    private static bool IsClonedVoiceEngine(string key)
    {
        return NormalizeEngineKey(key) is "pockettts" or "chatterbox" or "omnivoice";
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
            // The apply probe below reports the actionable status.
        }
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

    private const string ProbeScript = """
from pathlib import Path
import json

status = {
    "chatterbox": Path("/home/dwemer/chatterbox/venv").exists(),
    "omnivoice": Path("/home/dwemer/omnivoice-tts/venv").exists(),
    "pockettts": Path("/home/dwemer/pocket-tts/venv/bin/python").exists() and Path("/home/dwemer/pocket-tts/start.sh").exists(),
}

print(json.dumps(status))
""";

    private const string ApplyScript = """"
import json
import os
import subprocess
import sys
from pathlib import Path

engine = (sys.argv[1] if len(sys.argv) > 1 else "pockettts").strip().lower()
if engine not in ("pockettts", "chatterbox", "omnivoice"):
    engine = "pockettts"

def read_omnivoice_language():
    config_path = Path("/home/dwemer/omnivoice-tts/config.json")
    try:
        data = json.loads(config_path.read_text(encoding="utf-8"))
    except Exception:
        return "en"
    language = str(data.get("active_language") or "en").strip().lower()
    return language or "en"

active_language = read_omnivoice_language() if engine == "omnivoice" else "en"

if engine == "chatterbox":
    herika_driver = "chatterbox"
    herika_label = "ddistro chatterbox"
    herika_url = "http://127.0.0.1:8020"
    stobe_type = "chatterbox"
    stobe_name = "Chatterbox Default"
    stobe_url = "http://127.0.0.1:8020"
    stobe_match_provider = True
    display = "Chatterbox"
elif engine == "omnivoice":
    herika_driver = "omnivoice"
    herika_label = "OmniVoice Default"
    herika_url = "http://127.0.0.1:8021"
    stobe_type = "omnivoice"
    stobe_name = "OmniVoice Default"
    stobe_url = "http://127.0.0.1:8021"
    stobe_match_provider = False
    display = "Multilingual OmniVoice"
else:
    herika_driver = "pockettts"
    herika_label = "ddistro pockettts"
    herika_url = "http://127.0.0.1:8020"
    stobe_type = "pocket_tts"
    stobe_name = "Pocket TTS Default"
    stobe_url = "http://127.0.0.1:8020"
    stobe_match_provider = True
    display = "Pocket-TTS"

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
        timeout=25,
        env=env,
    )

def sql_literal(value):
    return "'" + str(value).replace("'", "''") + "'"

def columns_for(db, table_name):
    sql = "SELECT column_name FROM information_schema.columns WHERE table_schema = 'public' AND table_name = " + sql_literal(table_name) + ";"
    result = psql(db, sql)
    if result.returncode != 0:
        return None, (result.stderr or result.stdout).strip()
    return set(line.strip() for line in result.stdout.splitlines() if line.strip()), None

def apply_herika_style(db):
    driver = sql_literal(herika_driver)
    label = sql_literal(herika_label)
    url = sql_literal(herika_url)
    fallback_male = "default_male" if engine == "omnivoice" and db == "dialectic" else "malenord"
    fallback_female = "default_female" if engine == "omnivoice" and db == "dialectic" else "femalenord"
    if engine == "omnivoice":
        metadata = sql_literal(json.dumps({
            "language": active_language,
            "voicelogic": "voicetype",
            "fallback_male": fallback_male,
            "fallback_female": fallback_female,
        }))
    else:
        metadata = sql_literal(json.dumps({
            "_title": display + " (DwemerDistro quickstart)",
            "voiceid": {"type": "string"},
            "language": {"type": "select", "values": [active_language]},
            "voicelogic": {"type": "select", "values": ["voicetype", "name"]},
            "fallback_male": {"type": "string", "default": fallback_male},
            "fallback_female": {"type": "string", "default": fallback_female},
        }))
    connector_id = f"(SELECT id FROM core_tts_connector WHERE driver = {driver} AND label = {label} ORDER BY id LIMIT 1)"
    statements = [f"""
INSERT INTO core_tts_connector(driver, label, metadata, api_badge_id, url, voice_field)
SELECT {driver}, {label}, {metadata}::jsonb, NULL, {url}, 'voiceid'
WHERE NOT EXISTS (
    SELECT 1 FROM core_tts_connector WHERE driver = {driver} AND label = {label}
);

UPDATE core_tts_connector
SET label = {label},
    metadata = {metadata}::jsonb,
    url = {url},
    voice_field = 'voiceid'
WHERE id = {connector_id};
"""]

    profile_columns, _ = columns_for(db, "core_profiles")
    if profile_columns and "tts_connector_id" in profile_columns:
        profile_conditions = ["tts_connector_id IS NULL"]
        if "default_npc" in profile_columns:
            profile_conditions.append("COALESCE(default_npc, '') = '1'")
        if "default_narrator" in profile_columns:
            profile_conditions.append("COALESCE(default_narrator, '') = '1'")
        statements.append(f"""
UPDATE core_profiles
SET tts_connector_id = {connector_id}
WHERE {" OR ".join(profile_conditions)};
""")

    player_columns, _ = columns_for(db, "core_player")
    if player_columns and "tts_connector_id" in player_columns:
        player_conditions = ["tts_connector_id IS NULL"]
        if "id" in player_columns:
            player_conditions.append("id = 1")
        statements.append(f"""
UPDATE core_player
SET tts_connector_id = {connector_id}
WHERE {" OR ".join(player_conditions)};
""")

    sql = "\n".join(statements)
    return psql(db, sql)

def apply_stobe_style(db):
    provider = sql_literal(stobe_type)
    name = sql_literal(stobe_name)
    url = sql_literal(stobe_url)
    fallback_male = "default_male" if engine == "omnivoice" else "male1"
    fallback_female = "default_female" if engine == "omnivoice" else "female1"
    config = sql_literal(json.dumps({
        "language": active_language,
        "fallback_male": fallback_male,
        "fallback_female": fallback_female,
        "stream_chunk_size": 20,
        "temperature": 0.9,
        "speed": 1.0,
        "length_penalty": 1.0,
        "repetition_penalty": 5.0,
        "top_p": 0.85,
        "top_k": 50,
        "enable_text_splitting": True,
    }))
    match_clause = f"LOWER(name) = LOWER({name})"
    if stobe_match_provider:
        match_clause = f"{match_clause} OR connector_type = {provider}"
    connector_id = f"(SELECT id FROM core_tts_connector WHERE {match_clause} ORDER BY CASE WHEN LOWER(name) = LOWER({name}) THEN 0 ELSE 1 END, id LIMIT 1)"
    statements = [f"""
UPDATE core_tts_connector
SET is_default = FALSE
WHERE connector_type IN ('pocket_tts', 'xtts', 'chatterbox', 'omnivoice', 'cartesia', 'inworld');

INSERT INTO core_tts_connector(name, connector_type, base_url, is_default, config)
SELECT {name}, {provider}, {url}, TRUE, {config}::jsonb
WHERE NOT EXISTS (
    SELECT 1 FROM core_tts_connector WHERE {match_clause}
);

UPDATE core_tts_connector
SET name = {name},
    connector_type = {provider},
    base_url = {url},
    is_default = TRUE,
    config = {config}::jsonb
WHERE id = {connector_id};
"""]

    profile_columns, _ = columns_for(db, "core_profiles")
    if profile_columns and "tts_connector_id" in profile_columns:
        profile_conditions = ["tts_connector_id IS NULL"]
        if "is_default_npc" in profile_columns:
            profile_conditions.append("COALESCE(is_default_npc, FALSE)")
        if "is_player_faction_profile" in profile_columns:
            profile_conditions.append("COALESCE(is_player_faction_profile, FALSE)")
        statements.append(f"""
UPDATE core_profiles
SET tts_connector_id = {connector_id}
WHERE {" OR ".join(profile_conditions)};
""")

    sql = "\n".join(statements)
    return psql(db, sql)

statuses = []
for target in TARGETS:
    db = target["databaseName"]
    columns, error = columns_for(db, "core_tts_connector")
    if error:
        statuses.append({
            "targetName": target["targetName"],
            "databaseName": db,
            "applied": False,
            "skipped": True,
            "statusText": "Database unavailable",
            "error": error,
        })
        continue
    if not columns:
        statuses.append({
            "targetName": target["targetName"],
            "databaseName": db,
            "applied": False,
            "skipped": True,
            "statusText": "TTS table not found",
            "error": None,
        })
        continue

    if "driver" in columns and "label" in columns:
        result = apply_herika_style(db)
    elif "connector_type" in columns and "name" in columns:
        result = apply_stobe_style(db)
    else:
        statuses.append({
            "targetName": target["targetName"],
            "databaseName": db,
            "applied": False,
            "skipped": True,
            "statusText": "Unknown TTS schema",
            "error": None,
        })
        continue

    if result.returncode != 0:
        statuses.append({
            "targetName": target["targetName"],
            "databaseName": db,
            "applied": False,
            "skipped": False,
            "statusText": "Apply failed",
            "error": (result.stderr or result.stdout).strip(),
        })
    else:
        statuses.append({
            "targetName": target["targetName"],
            "databaseName": db,
            "applied": True,
            "skipped": False,
            "statusText": display + " applied",
            "error": None,
        })

print(json.dumps(statuses))
"""";

    private sealed class VoiceEngineApplyTargetProbe
    {
        public string? TargetName { get; set; }

        public string? DatabaseName { get; set; }

        public bool Applied { get; set; }

        public bool Skipped { get; set; }

        public string? StatusText { get; set; }

        public string? Error { get; set; }
    }
}

public sealed record VoiceEngineStatus(
    bool HasUsableEngine,
    string EngineKey,
    string DisplayName,
    string DetailText,
    IReadOnlyDictionary<string, bool> InstalledEngines)
{
    public static VoiceEngineStatus Unknown(string error)
    {
        return new VoiceEngineStatus(false, "pockettts", "Pocket-TTS", error, new Dictionary<string, bool>());
    }
}

public sealed record VoiceEngineApplyTargetStatus(
    string TargetName,
    string DatabaseName,
    bool Applied,
    bool Skipped,
    string StatusText,
    string? Error);
