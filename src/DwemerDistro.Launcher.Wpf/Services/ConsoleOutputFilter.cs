using System.Text.RegularExpressions;

namespace DwemerDistro.Launcher.Wpf.Services;

/// <summary>
/// Hides known routine apt, pip and git chatter from operation consoles whose full output is already
/// saved to a log file. Problems, prompts and unrecognized lines always stay visible. Never apply this
/// to server or game traffic.
/// </summary>
internal static partial class ConsoleOutputFilter
{
    private static readonly string[] RoutinePrefixes =
    [
        // apt / dpkg
        "(Reading database",
        "Selecting previously unselected package",
        "Preparing to unpack",
        "Processing triggers for",
        "Reading package lists",
        "Building dependency tree",
        "Reading state information",
        "Hit:",
        "Get:",
        "Ign:",
        "Fetched ",
        "The following NEW packages",
        "The following additional packages",
        "The following packages will be upgraded",
        "The following packages were automatically installed",
        "The following package was automatically installed",
        "Suggested packages:",
        "Recommended packages:",
        "After this operation",
        "Need to get",
        // pip
        "Requirement already satisfied:",
        "Collecting ",
        "Using cached ",
        "Obtaining ",
        "Looking in indexes:",
        "Installing collected packages:",
        "Attempting uninstall:",
        "Found existing installation:",
        "Successfully uninstalled ",
        "Building wheels for collected packages",
        "Building wheel for ",
        "Created wheel for ",
        "Stored in directory:",
        "Installing build dependencies",
        "Getting requirements to build",
        "Preparing metadata",
        // git
        "remote: Enumerating objects",
        "remote: Counting objects",
        "remote: Compressing objects",
        "remote: Total ",
        "Receiving objects:",
        "Resolving deltas:",
        "Unpacking objects:",
        "From https://",
        "From http://",
        "Already up to date.",
        "Already up-to-date.",
        "HEAD is now at ",
        "Fast-forward",
        "* branch ",
        "* [new branch]",
        "* [new tag]"
    ];

    /// <summary>True only for a recognized routine line that carries no problem or prompt.</summary>
    public static bool IsRoutineLine(string line)
    {
        var trimmed = line.Trim();
        if (trimmed.Length == 0 || IsProblemOrPrompt(trimmed))
        {
            return false;
        }

        return RoutinePrefixes.Any(prefix => trimmed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            || PackageStep().IsMatch(trimmed)
            || PipProgressBar().IsMatch(trimmed)
            || GitRefUpdate().IsMatch(trimmed)
            || GitDiffStat().IsMatch(line.TrimEnd())
            || GitChangeSummary().IsMatch(trimmed)
            || AptCounts().IsMatch(trimmed)
            || CoreNotInstalledSkip().IsMatch(trimmed);
    }

    private static bool IsProblemOrPrompt(string trimmed)
    {
        return DistroSetupService.IsInstallerProblemLine(trimmed)
            || ProblemWord().IsMatch(trimmed)
            || trimmed.EndsWith('?')
            || trimmed.Contains("[Y/n]", StringComparison.OrdinalIgnoreCase)
            || trimmed.Contains("password", StringComparison.OrdinalIgnoreCase);
    }

    [GeneratedRegex(@"\b(error|errors|warning|warnings|conflict|denied|abort|aborted|aborting)\b", RegexOptions.IgnoreCase)]
    private static partial Regex ProblemWord();

    // apt "Setting up pkg (1.0) ...", "Unpacking pkg (1.0) ...", pip "Downloading x.whl (2 MB)",
    // "Uninstalling numpy-1.26.4:". Script steps such as "Uninstalling herika..." stay visible.
    [GeneratedRegex(@"^((Setting up|Unpacking) \S+ \(|Downloading (https?://\S+|\S+\.(whl|tar\.gz|zip))( \(|$)|Uninstalling \S+-\d\S*:$)")]
    private static partial Regex PackageStep();

    [GeneratedRegex(@"^[━─╸╺\s]+[\d.]+(/[\d.]+)?\s*[kMG]?B")]
    private static partial Regex PipProgressBar();

    // "   abc1234..def5678  main       -> origin/main"
    [GeneratedRegex(@"^[+ ]?[0-9a-f]{7,}\.\.\.?[0-9a-f]{7,}\s+\S+\s+->\s+\S+$")]
    private static partial Regex GitRefUpdate();

    // " path/to/file.php | 12 ++--" or " image.png | Bin 0 -> 1 bytes"
    [GeneratedRegex(@"^ \S.*\s\|\s+(\d+ ?[+-]*|Bin .*)$")]
    private static partial Regex GitDiffStat();

    [GeneratedRegex(@"^(\d+ files? changed|(create|delete) mode \d+ |rename .+\(\d+%\)$|Updating [0-9a-f]{7,}\.\.[0-9a-f]{7,}$)")]
    private static partial Regex GitChangeSummary();

    // Only a summary that removes nothing; any package removal stays visible.
    [GeneratedRegex(@"^\d+ upgraded, \d+ newly installed, 0 to remove\b")]
    private static partial Regex AptCounts();

    // Core skips of components that are not installed: update_gws "[SKIP] CHIM XTTS repository not installed;
    // skipping update.", "[SKIP] OmniVoice TTS not installed; install it from ...", fix_ddistro_permissions
    // "[SKIP] <path> is not installed". Any other [SKIP] line stays visible.
    [GeneratedRegex(@"^\[SKIP\] [^;]+? (is )?not installed([;.].*)?$")]
    private static partial Regex CoreNotInstalledSkip();
}
