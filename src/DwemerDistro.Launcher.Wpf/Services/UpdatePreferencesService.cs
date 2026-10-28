using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DwemerDistro.Launcher.Wpf.Services;

/// <summary>
/// Launcher-local update preferences, stored beside the other launcher state in
/// <c>%LOCALAPPDATA%\DwemerDistro</c>. Nothing here is written into WSL: these settings govern what
/// the launcher itself asks the server manager to do, so a distro shared with other tooling never
/// inherits a destructive option from this file.
///
/// Every read fails closed. A missing, unreadable, or malformed file reports the safe default rather
/// than throwing, because a corrupt preferences file must never force-overwrite anyone's edits.
/// </summary>
public sealed class UpdatePreferencesService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public UpdatePreferencesService(string? localAppDataDirectory = null)
    {
        var resolvedDirectory = localAppDataDirectory
                                ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        StatePath = Path.Combine(resolvedDirectory, "DwemerDistro", "update-preferences.json");
    }

    public string StatePath { get; }

    /// <summary>
    /// True only when the file exists and explicitly says so. Missing, unreadable, and malformed all
    /// read back as false, which is the non-destructive behaviour.
    /// </summary>
    public bool GetForceGitUpdates()
    {
        return Read().ForceGitUpdates;
    }

    /// <summary>
    /// Rewrites the file with the new value, keeping any keys a newer launcher build wrote, so
    /// toggling this option in an older build does not silently drop unrelated preferences.
    /// </summary>
    public bool TrySetForceGitUpdates(bool enabled, out string? error)
    {
        error = null;

        try
        {
            var preferences = Read();
            preferences.ForceGitUpdates = enabled;

            var directory = Path.GetDirectoryName(StatePath)!;
            Directory.CreateDirectory(directory);

            // Write to a sibling temp file and move it over the target: a crash mid-write leaves the
            // previous preferences intact instead of a half-written file that reads as the default.
            var temporaryPath = Path.Combine(directory, $"update-preferences.{Guid.NewGuid():N}.tmp");
            try
            {
                File.WriteAllText(temporaryPath, JsonSerializer.Serialize(preferences, JsonOptions));
                File.Move(temporaryPath, StatePath, overwrite: true);
            }
            finally
            {
                if (File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }
            }

            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private UpdatePreferences Read()
    {
        try
        {
            return File.Exists(StatePath)
                ? JsonSerializer.Deserialize<UpdatePreferences>(File.ReadAllText(StatePath), JsonOptions)
                  ?? new UpdatePreferences()
                : new UpdatePreferences();
        }
        catch
        {
            return new UpdatePreferences();
        }
    }
}

public sealed class UpdatePreferences
{
    /// <summary>
    /// Lets launcher-triggered mod server updates discard manual edits to Git-tracked files. Off
    /// unless the file says otherwise.
    /// </summary>
    public bool ForceGitUpdates { get; set; }

    /// <summary>Preferences this build does not know about, preserved across a save.</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalData { get; set; }
}
