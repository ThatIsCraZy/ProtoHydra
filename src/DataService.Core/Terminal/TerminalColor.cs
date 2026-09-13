namespace DataService.Core.Terminal;

/// <summary>
/// Colour encoding used by <see cref="TerminalStyle"/>: -1 keeps the theme default,
/// 0..255 selects an xterm palette slot, anything larger carries a packed 24-bit
/// colour from an SGR 38;2 / 48;2 sequence.
/// </summary>
public static class TerminalColor
{
    public const int Default = -1;
    private const int RgbMarker = 1 << 24;

    public static bool IsPalette(int value) => value is >= 0 and <= 255;

    public static bool IsRgb(int value) => value >= RgbMarker;

    public static int FromRgb(int red, int green, int blue)
        => RgbMarker | ((red & 0xFF) << 16) | ((green & 0xFF) << 8) | (blue & 0xFF);

    public static void ToRgb(int value, out byte red, out byte green, out byte blue)
    {
        red = (byte)((value >> 16) & 0xFF);
        green = (byte)((value >> 8) & 0xFF);
        blue = (byte)(value & 0xFF);
    }
}
