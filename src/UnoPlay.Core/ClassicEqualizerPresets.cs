namespace UnoPlay.Core;

/// <summary>Original ten-band factory curves in Winamp slider units (0–63).</summary>
public static class ClassicEqualizerPresets
{
    public static IReadOnlyList<EqualizerPreset> All { get; } = Array.AsReadOnly<EqualizerPreset>([
        new("Classical", [31, 31, 31, 31, 31, 31, 44, 44, 44, 48, 31]),
        new("Club", [31, 31, 26, 22, 22, 22, 26, 31, 31, 31, 31]),
        new("Dance", [16, 20, 28, 32, 32, 42, 44, 44, 32, 32, 31]),
        new("Flat", [31, 31, 31, 31, 31, 31, 31, 31, 31, 31, 31]),
        new("Laptop speakers/headphones", [24, 14, 23, 38, 36, 29, 24, 16, 11, 8, 31]),
        new("Large hall", [15, 15, 22, 22, 31, 40, 40, 40, 31, 31, 31]),
        new("Party", [20, 20, 31, 31, 31, 31, 31, 31, 20, 20, 31]),
        new("Pop", [35, 24, 20, 19, 23, 34, 36, 36, 35, 35, 31]),
        new("Reggae", [31, 31, 33, 42, 31, 21, 21, 31, 31, 31, 31]),
        new("Rock", [19, 24, 41, 45, 38, 25, 17, 14, 14, 14, 31]),
        new("Soft", [24, 29, 34, 36, 34, 25, 18, 16, 14, 12, 31]),
        new("Ska", [36, 40, 39, 33, 25, 22, 17, 16, 14, 16, 31]),
        new("Full Bass", [16, 16, 16, 22, 29, 39, 46, 49, 50, 50, 31]),
        new("Soft Rock", [25, 25, 28, 33, 39, 41, 38, 33, 27, 17, 31]),
        new("Full Treble", [48, 48, 48, 39, 27, 14, 6, 6, 6, 4, 31]),
        new("Full Bass & Treble", [20, 22, 31, 44, 40, 29, 18, 14, 12, 12, 31]),
        new("Live", [40, 31, 25, 23, 22, 22, 25, 27, 27, 28, 31]),
        new("Techno", [19, 22, 31, 41, 40, 31, 19, 16, 16, 17, 31]),
    ]);
}
