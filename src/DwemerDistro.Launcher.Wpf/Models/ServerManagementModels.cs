namespace DwemerDistro.Launcher.Wpf.Models;

/// <summary>
/// The three optional application servers <c>ddistro_server</c> manages. The launcher never
/// forwards a caller-supplied product string; every command is built from this closed set.
/// </summary>
public enum ServerProduct
{
    Herika,
    Stobe,
    Dialectic
}

/// <summary>Install state reported by <c>ddistro_server status all --json</c>.</summary>
public enum ServerInstallState
{
    /// <summary>The status probe has not answered yet, or answered with a value this build does not know.</summary>
    Unknown,
    NotInstalled,
    Installed,
    NeedsRepair
}

/// <summary>Repository shape reported for a product.</summary>
public enum ServerRepositoryState
{
    Unknown,
    Absent,
    Legacy,
    Managed
}

/// <summary>The two branch channels the manager accepts on install/update/repair.</summary>
public enum ServerBranchChannel
{
    Main,
    Dev
}

/// <summary>
/// One server entry from the versioned status document. Values the distro omits stay null so the
/// UI can say "unknown" instead of inventing a version or a port.
/// </summary>
public sealed record ServerStatus(
    ServerProduct Product,
    ServerInstallState State,
    ServerRepositoryState RepositoryState,
    bool? DatabasePresent,
    string? Root,
    string? Database,
    string? Branch,
    string? Version,
    string? ProductionBranch,
    string? DevelopmentBranch,
    int? Port)
{
    public bool IsInstalled => State == ServerInstallState.Installed;

    public bool IsNotInstalled => State == ServerInstallState.NotInstalled;

    public bool NeedsRepair => State == ServerInstallState.NeedsRepair;

    /// <summary>Anything other than a confirmed install must not be updated or opened.</summary>
    public bool IsUsable => State == ServerInstallState.Installed;
}

/// <summary>A parsed <c>status all --json</c> document.</summary>
public sealed record ServerStatusSnapshot(int SchemaVersion, IReadOnlyList<ServerStatus> Servers)
{
    public ServerStatus? Find(ServerProduct product)
    {
        return Servers.FirstOrDefault(server => server.Product == product);
    }
}
