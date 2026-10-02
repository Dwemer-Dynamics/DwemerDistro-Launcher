using System.Windows;

namespace DwemerDistro.Launcher.Wpf.Models;

public sealed record GameProfile(
    string Key,
    string Name,
    string GameTitle,
    string Description,
    string HeroImageSource,
    string RailImageSource)
{
    /// <summary>
    /// Square region of <see cref="RailImageSource"/>, relative to the image, shown as the small
    /// sidebar icon. Each crop centres the artwork's most recognisable mark so it still reads at
    /// icon size.
    /// </summary>
    public Rect RailIconViewbox => Key switch
    {
        "CHIM" => new Rect(0.35, 0, 0.298, 1),
        "LORKHAN" => new Rect(0.875, 0.72, 0.11, 0.1956),
        "DIALECTIC" => new Rect(0.33, 0, 0.298, 1),
        "STOBE" => new Rect(0.48, 0, 0.265, 0.887),
        "REIGN" => new Rect(0.409, 0.162, 0.173, 0.308),
        _ => new Rect(0, 0, 1, 1)
    };

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
                "LORKHAN", "LORKHAN (Beta)", "Morrowind / OpenMW",
                "Character conversations, memories, and actions across Morrowind.",
                "pack://application:,,,/Assets/GameCenter/lorkhan-hero.jpg",
                "pack://application:,,,/Assets/GameCenter/lorkhan-rail.jpg"),
            new GameProfile(
                "DIALECTIC",
                "DIALECTIC",
                "Fallout: New Vegas / TTW",
                "Natural dialogue, durable memories, and character actions across New Vegas and the Capital Wasteland.",
                "pack://application:,,,/Assets/GameCenter/dialectic-hero.jpg",
                "pack://application:,,,/Assets/GameCenter/dialectic-rail.jpg"),
            new GameProfile(
                "STOBE",
                "STOBE",
                "Kenshi",
                "Voiced conversations and persistent character memories shaped by your squad and the world.",
                "pack://application:,,,/Assets/GameCenter/stobe-hero.jpg",
                "pack://application:,,,/Assets/GameCenter/stobe-rail.jpg"),
            new GameProfile(
                "REIGN", "REIGN (Closed Alpha)", "Mount & Blade II: Bannerlord",
                "Character conversations, relationships, and persistent memories across Calradia.",
                "pack://application:,,,/Assets/ReignLogo.png",
                "pack://application:,,,/Assets/ReignLogo.png")
        };
    }
}
