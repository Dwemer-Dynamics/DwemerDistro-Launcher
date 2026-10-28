namespace DwemerDistro.Launcher.Wpf.Models;

/// <param name="IconImageSource">The mod's transparent emblem, as used on the Dwemer Dashboard's
/// mod buttons, shown in the sidebar game list.</param>
public sealed record GameProfile(
    string Key,
    string Name,
    string GameTitle,
    string Description,
    string HeroImageSource,
    string RailImageSource,
    string IconImageSource)
{
    public static IReadOnlyList<GameProfile> CreateCatalog()
    {
        return new[]
        {
            new GameProfile(
                "CHIM",
                "CHIM",
                "Skyrim Special Edition / Skyrim VR",
                "Meaningful conversations, memories, relationships, and unscripted life across Skyrim.",
                "pack://application:,,,/Assets/GameCenter/chim-hero.jpg",
                "pack://application:,,,/Assets/GameCenter/chim-rail.jpg",
                "pack://application:,,,/Assets/GameCenter/chim-icon.png"),
            new GameProfile(
                "LORKHAN", "LORKHAN (Beta)", "Morrowind / OpenMW",
                "Character conversations, memories, and actions across Morrowind.",
                "pack://application:,,,/Assets/GameCenter/lorkhan-hero.jpg",
                "pack://application:,,,/Assets/GameCenter/lorkhan-rail.jpg",
                "pack://application:,,,/Assets/GameCenter/lorkhan-icon.png"),
            new GameProfile(
                "DIALECTIC",
                "DIALECTIC",
                "Fallout: New Vegas / TTW",
                "Natural dialogue, durable memories, and character actions across New Vegas and the Capital Wasteland.",
                "pack://application:,,,/Assets/GameCenter/dialectic-hero.jpg",
                "pack://application:,,,/Assets/GameCenter/dialectic-rail.jpg",
                "pack://application:,,,/Assets/GameCenter/dialectic-icon.png"),
            new GameProfile(
                "STOBE",
                "STOBE",
                "Kenshi",
                "Voiced conversations and persistent character memories shaped by your squad and the world.",
                "pack://application:,,,/Assets/GameCenter/stobe-hero.jpg",
                "pack://application:,,,/Assets/GameCenter/stobe-rail.jpg",
                "pack://application:,,,/Assets/GameCenter/stobe-icon.png"),
            new GameProfile(
                "REIGN", "REIGN (Closed Alpha)", "Mount & Blade II: Bannerlord",
                "Character conversations, relationships, and persistent memories across Calradia.",
                "pack://application:,,,/Assets/ReignLogo.png",
                "pack://application:,,,/Assets/ReignLogo.png",
                "pack://application:,,,/Assets/GameCenter/reign-icon.png")
        };
    }
}
