namespace DwemerDistro.Launcher.Wpf.Models;

public sealed class RollbackTarget
{
    public required string Ref { get; init; }
    public required string ShaShort { get; init; }
    public required string Date { get; init; }
    public required string VersionNumber { get; init; }
    public string VersionText { get; init; } = string.Empty;
    public required string Label { get; init; }
}
