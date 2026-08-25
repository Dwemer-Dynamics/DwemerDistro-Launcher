namespace DwemerDistro.Launcher.Wpf.Models;

public sealed record GameProfile(
    string Key,
    string Name,
    string GameTitle,
    string Description,
    string HeroImageSource,
    string RailImageSource)
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
                "pack://application:,,,/Assets/GameCenter/chim-rail.jpg"),
            new GameProfile(
                "STOBE",
                "STOBE",
                "Kenshi",
                "Voiced conversations and persistent character memories shaped by your squad and the world.",
                "pack://application:,,,/Assets/GameCenter/stobe-hero.jpg",
                "pack://application:,,,/Assets/GameCenter/stobe-rail.jpg"),
            new GameProfile(
                "DIALECTIC",
                "DIALECTIC",
                "Fallout: New Vegas / TTW",
                "Natural dialogue, durable memories, and character actions across New Vegas and the Capital Wasteland.",
                "pack://application:,,,/Assets/GameCenter/dialectic-hero.jpg",
                "pack://application:,,,/Assets/GameCenter/dialectic-rail.jpg")
        };
    }
}
