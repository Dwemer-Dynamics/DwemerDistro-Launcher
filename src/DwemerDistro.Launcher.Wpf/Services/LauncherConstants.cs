namespace DwemerDistro.Launcher.Wpf.Services;

public static class LauncherConstants
{
    public const string LauncherVersion = "3.3.14";
    public const string LauncherRepoUrl = "https://github.com/Dwemer-Dynamics/DwemerDistro-Launcher";
    public const string LauncherLatestReleaseApiUrl = "https://api.github.com/repos/Dwemer-Dynamics/DwemerDistro-Launcher/releases/latest";
    public const string LauncherExeName = "DwemerDistro.exe";
    public const string LauncherUpdaterExeName = "DwemerDistroUpdater.exe";
    public const string LauncherPackageAssetName = "DwemerDistro-win-x64.zip";
    public const string DistroName = "DwemerAI4Skyrim3";
    public const string DistroUser = "dwemer";
    public const int SkyrimProxyPort = 7513;
    public const int LorkhanProxyPort = 7514;
    public const int DiscoveryPort = 7135;
    public const int SkyrimServerPort = 8081;
    public const int StobeServerPort = 8083;
    public const int StarfieldServerPort = 8087;
    public const int DialecticServerPort = 8088;
    public const int ReignServerPort = 8089;
    public const int LorkhanServerPort = 8090;

    public const string WikiUrl = "https://dwemerdynamics.com/index.html";
    public const string DiscordUrl = "https://discord.com/invite/NDn9qud2ug";
    public const string ChimServerUiUrl = "http://127.0.0.1:8081/HerikaServer/ui/";
    public const string StobeServerUiUrl = "http://127.0.0.1:8083/StobeServer/ui/";
    public const string DialecticServerUiUrl = "http://127.0.0.1:8088/DialecticServer/ui/";

    // Public mod pages. These are plain external links: they never probe or start WSL.
    public const string ChimNexusUrl = "https://www.nexusmods.com/skyrimspecialedition/mods/126330";
    public const string StobeNexusUrl = "https://www.nexusmods.com/kenshi/mods/1891";
    public const string DialecticNexusUrl = "https://www.nexusmods.com/newvegas/mods/99233";
}
