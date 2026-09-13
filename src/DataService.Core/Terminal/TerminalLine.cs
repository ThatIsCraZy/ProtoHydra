namespace DataService.Core.Terminal;

/// <summary>
/// A single row of the screen grid or of the scrollback buffer.
/// </summary>
public sealed class TerminalLine
{
    private TerminalCell[] _cells;

    public TerminalLine(int columns)
    {
        _cells = new TerminalCell[Math.Max(1, columns)];
        Clear(TerminalCell.Blank);
    }

    public int Length => _cells.Length;

    public TerminalCell this[int column]
    {
        get => _cells[column];
        set => _cells[column] = value;
    }

    public ReadOnlySpan<TerminalCell> Cells => _cells;

    public void Clear(TerminalCell fill) => Array.Fill(_cells, fill);

    public void ClearRange(int start, int endExclusive, TerminalCell fill)
    {
        start = Math.Clamp(start, 0, _cells.Length);
        endExclusive = Math.Clamp(endExclusive, 0, _cells.Length);
        for (var column = start; column < endExclusive; column++)
        {
            _cells[column] = fill;
        }
    }

    public void InsertCells(int column, int count, TerminalCell fill)
    {
        if (count <= 0 || column >= _cells.Length)
        {
            return;
        }

        count = Math.Min(count, _cells.Length - column);
        Array.Copy(_cells, column, _cells, column + count, _cells.Length - column - count);
        ClearRange(column, column + count, fill);
    }

    public void DeleteCells(int column, int count, TerminalCell fill)
    {
        if (count <= 0 || column >= _cells.Length)
        {
            return;
        }

        count = Math.Min(count, _cells.Length - column);
        Array.Copy(_cells, column + count, _cells, column, _cells.Length - column - count);
        ClearRange(_cells.Length - count, _cells.Length, fill);
    }

    /// <summary>
    /// Grows or clips the row to a new width. Existing content is kept; new columns
    /// are filled with <paramref name="fill"/>. Rows are never re-wrapped, which
    /// matches how PuTTY and xterm treat a width change.
    /// </summary>
    public void Resize(int columns, TerminalCell fill)
    {
        columns = Math.Max(1, columns);
        if (columns == _cells.Length)
        {
            return;
        }

        var resized = new TerminalCell[columns];
        var copy = Math.Min(columns, _cells.Length);
        Array.Copy(_cells, resized, copy);
        for (var column = copy; column < columns; column++)
        {
            resized[column] = fill;
        }

        _cells = resized;
    }

    /// <summary>
    /// Row text with trailing blanks removed, used for selection and export.
    /// </summary>
    public string GetText(int start, int endExclusive, bool trimEnd)
    {
        start = Math.Clamp(start, 0, _cells.Length);
        endExclusive = Math.Clamp(endExclusive, 0, _cells.Length);
        if (trimEnd)
        {
            while (endExclusive > start && _cells[endExclusive - 1].Character is ' ' or '\0')
            {
                endExclusive--;
            }
        }

        if (endExclusive <= start)
        {
            return string.Empty;
        }

        var buffer = new char[endExclusive - start];
        for (var column = start; column < endExclusive; column++)
        {
            var character = _cells[column].Character;
            buffer[column - start] = character == '\0' ? ' ' : character;
        }

        return new string(buffer);
    }
}
