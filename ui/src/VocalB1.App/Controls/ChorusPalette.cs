using System.Windows.Media;

namespace VocalB1.App.Controls;

/// <summary>
/// Muted per-singer colors used by the stage and the curve editor. They are deterministic by singer
/// index, so the same singer keeps its color in both views and in every theme.
/// </summary>
internal static class ChorusPalette
{
    private static readonly Color[] Dark =
    [
        Color.FromRgb(0x6F, 0xA8, 0xA0),
        Color.FromRgb(0xC3, 0xA1, 0x5C),
        Color.FromRgb(0x7E, 0x9B, 0xC0),
        Color.FromRgb(0xA3, 0x8B, 0xBE),
        Color.FromRgb(0xC0, 0x84, 0x78),
        Color.FromRgb(0x8D, 0xA3, 0x6A),
        Color.FromRgb(0x9A, 0x9A, 0xA6),
        Color.FromRgb(0xB8, 0x96, 0xA2),
    ];

    private static readonly Color[] Light =
    [
        Color.FromRgb(0x3D, 0x7A, 0x72),
        Color.FromRgb(0x97, 0x74, 0x2F),
        Color.FromRgb(0x4A, 0x6E, 0x96),
        Color.FromRgb(0x6E, 0x57, 0x94),
        Color.FromRgb(0x95, 0x56, 0x4C),
        Color.FromRgb(0x5F, 0x73, 0x40),
        Color.FromRgb(0x5E, 0x5E, 0x6A),
        Color.FromRgb(0x86, 0x5C, 0x6C),
    ];

    public static Color For(int index, bool light) =>
        (light ? Light : Dark)[Math.Abs(index) % Dark.Length];
}
