using System.IO;
using Microsoft.Win32;

namespace DwemerDistro.Launcher.Wpf.Services;

/// <summary>
/// Registers <c>dwemerdistro-mod:</c> for the website's "Open in DwemerDistro" link, separately from the diagnostics
/// scheme. Unlike the diagnostics handler it does not follow whichever launcher ran last: a launcher already registered
/// from another folder keeps the link while its executable exists, so running a candidate build elsewhere for testing
/// does not take it over. Set <see cref="RegistrationVariable"/> to <c>takeover</c> to claim it from such a build on
/// purpose, or to <c>off</c> to leave the registry untouched.
/// </summary>
internal static class ModLinkProtocolRegistrationService
{
    internal const string RegistrationVariable = "DWEMERDISTRO_MOD_LINK_REGISTRATION";
    private const string CommandKeyPath = $@"Software\Classes\{ModLinkRequest.Scheme}\shell\open\command";

    internal enum Decision
    {
        Register,
        KeepExisting,
        Skip
    }

    public static void EnsureRegistered()
    {
        var executablePath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executablePath)
            || !Path.GetFileName(executablePath).Equals("DwemerDistro.exe", StringComparison.OrdinalIgnoreCase))
        {
            LauncherLogService.Startup("Mod link protocol registration skipped outside the published launcher.");
            return;
        }

        string? existingCommand;
        using (var existing = Registry.CurrentUser.OpenSubKey(CommandKeyPath))
        {
            existingCommand = existing?.GetValue(null) as string;
        }

        var decision = Decide(executablePath, existingCommand, Environment.GetEnvironmentVariable(RegistrationVariable), File.Exists);
        if (decision == Decision.Skip)
        {
            LauncherLogService.Startup($"Mod link protocol registration skipped by {RegistrationVariable}.");
            return;
        }

        if (decision == Decision.KeepExisting)
        {
            LauncherLogService.Startup("Mod link protocol left with the launcher already registered from another folder.");
            return;
        }

        using var protocolKey = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{ModLinkRequest.Scheme}");
        protocolKey.SetValue(null, "URL:DwemerDistro Mod Link");
        protocolKey.SetValue("URL Protocol", string.Empty);

        using var iconKey = protocolKey.CreateSubKey("DefaultIcon");
        iconKey.SetValue(null, $"\"{executablePath}\",0");

        using var commandKey = protocolKey.CreateSubKey(@"shell\open\command");
        commandKey.SetValue(null, BuildOpenCommand(executablePath));
    }

    /// <summary>
    /// Registers when nothing is registered, when this launcher already is, or when the registered launcher is gone;
    /// keeps another existing launcher's registration unless asked to take it over.
    /// </summary>
    internal static Decision Decide(string executablePath, string? existingCommand, string? setting, Func<string, bool> fileExists)
    {
        if (string.Equals(setting, "off", StringComparison.OrdinalIgnoreCase)) return Decision.Skip;
        if (string.Equals(setting, "takeover", StringComparison.OrdinalIgnoreCase)) return Decision.Register;
        if (string.IsNullOrWhiteSpace(existingCommand)) return Decision.Register;

        var registered = RegisteredExecutable(existingCommand);
        if (registered is null) return Decision.Register;
        if (string.Equals(NormalizePath(registered), NormalizePath(executablePath), StringComparison.OrdinalIgnoreCase))
        {
            return Decision.Register;
        }

        return fileExists(registered) ? Decision.KeepExisting : Decision.Register;
    }

    internal static string BuildOpenCommand(string executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath) || executablePath.Contains('"'))
        {
            throw new ArgumentException("A valid launcher executable path is required.", nameof(executablePath));
        }

        return $"\"{executablePath}\" {ModLinkRequest.OpenArgument} \"%1\"";
    }

    /// <summary>The quoted executable at the start of a registered command, or null when it is not in that form.</summary>
    internal static string? RegisteredExecutable(string command)
    {
        if (command.Length < 2 || command[0] != '"') return null;
        var end = command.IndexOf('"', 1);
        return end > 1 ? command[1..end] : null;
    }

    private static string NormalizePath(string path)
    {
        try { return Path.GetFullPath(path); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return path; }
    }
}
