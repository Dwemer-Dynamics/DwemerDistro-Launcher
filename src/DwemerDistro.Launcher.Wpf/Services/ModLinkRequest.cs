using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DwemerDistro.Launcher.Wpf.Services;

/// <summary>
/// The website's "Open in DwemerDistro" link: <c>dwemerdistro-mod:open?repo=owner/name</c> and nothing else. It only
/// names a public GitHub repository to preview on Mods &gt; Custom mods; it carries no version, verb, or install request,
/// and it is kept apart from the <c>dwemerdistro:</c> diagnostics scheme, whose registered command ignores its URI.
/// </summary>
internal static partial class ModLinkRequest
{
    internal const string Scheme = "dwemerdistro-mod";
    internal const string OpenArgument = "--open-mod";
    internal const int MaximumLinkLength = 180;
    private const string Prefix = Scheme + ":open?repo=";

    /// <summary>
    /// The <c>owner/name</c> a link asks to preview, or false for anything that is not exactly the documented form.
    /// Percent-encoding, extra parameters, fragments, paths, whitespace, and <c>.git</c> suffixes are all refused.
    /// </summary>
    public static bool TryParse(string? link, out string repository)
    {
        repository = string.Empty;
        if (string.IsNullOrEmpty(link) || link.Length > MaximumLinkLength
            || !link.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase)
            // Only the scheme is case-insensitive; the verb and parameter name are exact.
            || !link.AsSpan(Scheme.Length).StartsWith(Prefix.AsSpan(Scheme.Length), StringComparison.Ordinal))
        {
            return false;
        }

        var value = link[Prefix.Length..];
        // Windows or a browser may append one trailing slash to a bare custom-scheme link.
        if (value.EndsWith('/')) value = value[..^1];
        var match = OwnerAndName().Match(value);
        if (!match.Success) return false;

        var name = match.Groups["repo"].Value;
        if (name is "." or ".." || name.EndsWith(".git", StringComparison.OrdinalIgnoreCase)) return false;

        var candidate = match.Groups["owner"].Value + "/" + name;
        // The link must also satisfy the launcher's own custom-mod source rules, which the preview applies again.
        if (CustomModService.ValidateRepositoryUrl(ToRepositoryUrl(candidate)) is not null) return false;
        repository = candidate;
        return true;
    }

    /// <summary>True when a start came through the mod link handler, valid or not, so it never runs another startup mode.</summary>
    public static bool IsOpenRequest(IReadOnlyList<string> arguments) =>
        arguments.Any(argument => string.Equals(argument, OpenArgument, StringComparison.OrdinalIgnoreCase));

    /// <summary>The repository a start asks for, only when its arguments are exactly <c>--open-mod &lt;link&gt;</c>.</summary>
    public static bool TryGetFromArguments(IReadOnlyList<string> arguments, out string repository)
    {
        repository = string.Empty;
        return arguments.Count == 2
               && string.Equals(arguments[0], OpenArgument, StringComparison.OrdinalIgnoreCase)
               && TryParse(arguments[1], out repository);
    }

    public static string ToRepositoryUrl(string repository) => "https://github.com/" + repository;

    [GeneratedRegex(@"^(?<owner>[A-Za-z0-9](?:[A-Za-z0-9-]{0,37}[A-Za-z0-9])?)/(?<repo>[A-Za-z0-9._-]{1,100})\z", RegexOptions.CultureInvariant)]
    private static partial Regex OwnerAndName();
}

/// <summary>
/// Carries one validated repository from a second launcher start to the running instance from the same installation.
/// The activation event carries no data, so the request goes in a small per-install file next to it: written atomically
/// just before the signal, claimed by rename so it is read exactly once, and ignored once it is older than
/// <see cref="MaximumAge"/> or no longer parses.
/// </summary>
internal static class ModLinkHandoff
{
    internal static readonly TimeSpan MaximumAge = TimeSpan.FromSeconds(30);
    private const int MaximumBytes = 512;

    public static string DefaultDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DwemerDistro", "mod-links");

    /// <summary>One pending file per installation, named by the same install hash as the instance mutex.</summary>
    public static string PendingPath(string directory, string instanceMutexName)
    {
        var scope = instanceMutexName[(instanceMutexName.LastIndexOf('.') + 1)..];
        if (scope.Length == 0 || scope.Any(c => !char.IsAsciiLetterOrDigit(c)))
        {
            throw new ArgumentException("The instance name must end in its install hash.", nameof(instanceMutexName));
        }

        return Path.Combine(directory, $"pending-{scope}.json");
    }

    public static void Write(string path, string repository, DateTimeOffset now)
    {
        if (!ModLinkRequest.TryParse(ModLinkRequest.Scheme + ":open?repo=" + repository, out var valid) || valid != repository)
        {
            throw new ArgumentException("Only a validated repository can be handed over.", nameof(repository));
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var payload = JsonSerializer.SerializeToUtf8Bytes(new Payload(repository, now.UtcDateTime));
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllBytes(temporary, payload);
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    /// <summary>
    /// Takes the pending request, if any, so no later activation sees it again. Returns null when there is none, or when
    /// it is stale, oversized, or invalid; those are removed too.
    /// </summary>
    public static string? TryClaim(string path, DateTimeOffset now)
    {
        var claimed = path + "." + Guid.NewGuid().ToString("N") + ".claim";
        try
        {
            File.Move(path, claimed);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }

        try
        {
            var info = new FileInfo(claimed);
            if (info.Length is 0 or > MaximumBytes) return null;
            var payload = JsonSerializer.Deserialize<Payload>(File.ReadAllBytes(claimed));
            if (payload?.Repo is not { } repository) return null;
            var age = now.UtcDateTime - DateTime.SpecifyKind(payload.CreatedUtc, DateTimeKind.Utc);
            // A small allowance for clock skew between the two starts; anything older was not meant for this activation.
            if (age > MaximumAge || age < TimeSpan.FromSeconds(-5)) return null;
            return ModLinkRequest.TryParse(ModLinkRequest.Scheme + ":open?repo=" + repository, out var valid) && valid == repository
                ? valid
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
        finally
        {
            try { File.Delete(claimed); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    private sealed record Payload(string? Repo, DateTime CreatedUtc);
}
