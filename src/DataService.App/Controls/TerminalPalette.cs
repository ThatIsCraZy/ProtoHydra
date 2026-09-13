using Avalonia.Media;
using DataService.Core.Terminal;

namespace DataService.App.Controls;

/// <summary>
/// xterm colour table used by <see cref="TerminalControl"/>: sixteen theme-tuned base
/// colours, the 6×6×6 colour cube and the 24-step grey ramp.
/// </summary>
internal static class TerminalPalette
{
    private static readonly uint[] DarkBase =
    [
        0xFF1B2028, 0xFFE05252, 0xFF3FBF6F, 0xFFDCB55A,
        0xFF4C8FE0, 0xFFC06BD0, 0xFF3FB7C8, 0xFFD3DCE6,
        0xFF5A6572, 0xFFFF6B6B, 0xFF5BE39A, 0xFFF2D06B,
        0xFF6FB1FF, 0xFFDB8FEA, 0xFF5FD6E8, 0xFFF2F6FA
    ];

    private static readonly uint[] LightBase =
    [
        0xFF1C2430, 0xFFC22B2B, 0xFF15803D, 0xFF9A6700,
        0xFF1D4ED8, 0xFF9333EA, 0xFF0E7490, 0xFF6B7683,
        0xFF4B5563, 0xFFDC2626, 0xFF16A34A, 0xFFB45309,
        0xFF2563EB, 0xFFA855F7, 0xFF0891B2, 0xFF111827
    ];

    private static readonly int[] CubeSteps = [0, 95, 135, 175, 215, 255];

    public static Color Resolve(int value, bool isDark, Color fallback)
    {
        if (value == TerminalColor.Default)
        {
            return fallback;
        }

        if (TerminalColor.IsRgb(value))
        {
            TerminalColor.ToRgb(value, out var red, out var green, out var blue);
            return Color.FromRgb(red, green, blue);
        }

        if (value is >= 0 and <= 15)
        {
            return Color.FromUInt32((isDark ? DarkBase : LightBase)[value]);
        }

        if (value is >= 16 and <= 231)
        {
            var index = value - 16;
            return Color.FromRgb(
                (byte)CubeSteps[index / 36],
                (byte)CubeSteps[index / 6 % 6],
                (byte)CubeSteps[index % 6]);
        }

        if (value is >= 232 and <= 255)
        {
            var level = (byte)(8 + ((value - 232) * 10));
            return Color.FromRgb(level, level, level);
        }

        return fallback;
    }
}
