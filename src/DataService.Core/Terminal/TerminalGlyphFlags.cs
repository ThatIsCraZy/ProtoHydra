namespace DataService.Core.Terminal;

/// <summary>
/// Rendition bits carried by a single terminal cell, filled from SGR sequences.
/// </summary>
[Flags]
public enum TerminalGlyphFlags
{
    None = 0,
    Bold = 1 << 0,
    Faint = 1 << 1,
    Italic = 1 << 2,
    Underline = 1 << 3,
    Inverse = 1 << 4,
    Hidden = 1 << 5,
    Strikethrough = 1 << 6
}
