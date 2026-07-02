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
            "pockettts" => "Pocket-TTS",
            _ => "Cloned voice engine"
        };
    }

    public static string NormalizeEngineKey(string engineKey)
    {
        return (engineKey ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "chatterbox" => "chatterbox",
            "pocket_tts" or "pocket-tts" or "pockettts" => "pockettts",
            _ => "pockettts"
        };
    }

    private static bool IsClonedVoiceEngine(string key)
    {
        return NormalizeEngineKey(key) is "pockettts" or "chatterbox";
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
    "pockettts": Path("/home/dwemer/pocket-tts/venv").exists(),
}

print(json.dumps(status))
""";

    private const string ApplyScript = """"
import json
import os
import subprocess
import sys

engine = (sys.argv[1] if len(sys.argv) > 1 else "pockettts").strip().lower()
if engine not in ("pockettts", "chatterbox"):
    engine = "pockettts"

herika_driver = "chatterbox" if engine == "chatterbox" else "pockettts"
herika_label = "ddistro chatterbox" if engine == "chatterbox" else "ddistro pockettts"
stobe_type = "chatterbox" if engine == "chatterbox" else "pocket_tts"
stobe_name = "Chatterbox Default" if engine == "chatterbox" else "Pocket TTS Default"
display = "Chatterbox" if engine == "chatterbox" else "Pocket-TTS"

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
    metadata = sql_literal('{"_title":"' + display + ' (DwemerDistro quickstart)","voiceid":{"type":"string"},"language":{"type":"select","values":["en"]},"voicelogic":{"type":"select","values":["voicetype","name"]}}')
    sql = """
DO $$
DECLARE
    connector_id integer;
BEGIN
    SELECT id INTO connector_id
    FROM core_tts_connector
    WHERE driver = __DRIVER__
    ORDER BY id
    LIMIT 1;

    IF connector_id IS NULL THEN
        INSERT INTO core_tts_connector(driver, label, metadata, api_badge_id, url, voice_field)
        VALUES (__DRIVER__, __LABEL__, __METADATA__::jsonb, NULL, 'http://127.0.0.1:8020', 'voiceid')
        RETURNING id INTO connector_id;
    ELSE
        UPDATE core_tts_connector
        SET label = __LABEL__,
            metadata = COALESCE(metadata, __METADATA__::jsonb),
            url = 'http://127.0.0.1:8020',
            voice_field = 'voiceid'
        WHERE id = connector_id;
    END IF;

    IF to_regclass('public.core_profiles') IS NOT NULL THEN
        UPDATE core_profiles
        SET tts_connector_id = connector_id
        WHERE tts_connector_id IS NULL
           OR COALESCE(default_npc, '') = '1'
           OR COALESCE(default_narrator, '') = '1';
    END IF;

    IF to_regclass('public.core_player') IS NOT NULL THEN
        UPDATE core_player
        SET tts_connector_id = connector_id
        WHERE tts_connector_id IS NULL OR id = 1;
    END IF;
END
$$ LANGUAGE plpgsql;
"""
    sql = sql.replace("__DRIVER__", driver).replace("__LABEL__", label).replace("__METADATA__", metadata)
    return psql(db, sql)

def apply_stobe_style(db):
    provider = sql_literal(stobe_type)
    name = sql_literal(stobe_name)
    config = sql_literal('{"language":"en","fallback_male":"male1","fallback_female":"female1","stream_chunk_size":20,"temperature":0.9,"speed":1.0,"length_penalty":1.0,"repetition_penalty":5.0,"top_p":0.85,"top_k":50,"enable_text_splitting":true}')
    sql = """
DO $$
DECLARE
    connector_id integer;
BEGIN
    UPDATE core_tts_connector
    SET is_default = FALSE
    WHERE connector_type IN ('pocket_tts', 'xtts', 'chatterbox', 'cartesia', 'inworld');

    SELECT id INTO connector_id
    FROM core_tts_connector
    WHERE LOWER(name) = LOWER(__NAME__) OR connector_type = __PROVIDER__
    ORDER BY CASE WHEN LOWER(name) = LOWER(__NAME__) THEN 0 ELSE 1 END, id
    LIMIT 1;

    IF connector_id IS NULL THEN
        INSERT INTO core_tts_connector(name, connector_type, base_url, is_default, config)
        VALUES (__NAME__, __PROVIDER__, 'http://127.0.0.1:8020', TRUE, __CONFIG__::jsonb)
        RETURNING id INTO connector_id;
    ELSE
        UPDATE core_tts_connector
        SET name = __NAME__,
            connector_type = __PROVIDER__,
            base_url = 'http://127.0.0.1:8020',
            is_default = TRUE,
            config = COALESCE(config, __CONFIG__::jsonb)
        WHERE id = connector_id;
    END IF;

    IF to_regclass('public.core_profiles') IS NOT NULL THEN
        UPDATE core_profiles
        SET tts_connector_id = connector_id
        WHERE tts_connector_id IS NULL
           OR COALESCE(is_default_npc, FALSE)
           OR COALESCE(is_player_faction_profile, FALSE);
    END IF;
END
$$ LANGUAGE plpgsql;
"""
    sql = sql.replace("__NAME__", name).replace("__PROVIDER__", provider).replace("__CONFIG__", config)
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
