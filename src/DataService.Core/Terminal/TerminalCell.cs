namespace DataService.Core.Terminal;

/// <summary>
/// One character position of the screen grid.
/// </summary>
public readonly record struct TerminalCell(char Character, TerminalStyle Style)
{
    public static readonly TerminalCell Blank = new(' ', TerminalStyle.Default);
}
