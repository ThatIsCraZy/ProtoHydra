namespace DataService.Core.Terminal;

/// <summary>
/// Foreground, background and rendition of one cell. Colours are encoded as
/// described on <see cref="TerminalColor"/>.
/// </summary>
public readonly record struct TerminalStyle(int Foreground, int Background, TerminalGlyphFlags Flags)
{
    public static readonly TerminalStyle Default =
        new(TerminalColor.Default, TerminalColor.Default, TerminalGlyphFlags.None);

    public bool HasFlag(TerminalGlyphFlags flag) => (Flags & flag) != 0;
}
