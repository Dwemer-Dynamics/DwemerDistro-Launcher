namespace DwemerDistro.Launcher.Wpf.Models;

public sealed class LauncherReleaseInfo
{
    public required Version Version { get; init; }
    public required string TagName { get; init; }
    public required string PackageUrl { get; init; }
    public required string PackageName { get; init; }
    public string ReleasePageUrl { get; init; } = string.Empty;
}
