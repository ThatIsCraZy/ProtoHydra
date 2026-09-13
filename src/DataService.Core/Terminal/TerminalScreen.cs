using System.Globalization;
using System.Text;

namespace DataService.Core.Terminal;

/// <summary>
/// Screen model of a VT100/xterm compatible terminal: character grid, scrollback and
/// the escape-sequence parser that drives them. The class holds no rendering and no
/// transport code, so it can be exercised without a UI or a serial port.
/// </summary>
/// <remarks>
/// Not thread safe. Feed it from a single thread (the UI thread in this application)
/// and marshal data coming from a background reader onto that thread first.
/// </remarks>
public sealed class TerminalScreen
{
    public const int MinimumColumns = 20;
    public const int MinimumRows = 4;
    public const int DefaultScrollbackLines = 5_000;

    private const int TabWidth = 8;

    /// <summary>
    /// Upper bound on CSI parameters. ECMA-48 allows 16 and xterm keeps 30; a device stuck
    /// in a loop must not be able to grow this list until memory runs out.
    /// </summary>
    private const int MaxParameters = 32;

    private readonly TerminalLine?[] _scrollback;
    private readonly List<int> _parameters = new();
    private readonly StringBuilder _stringBuffer = new();

    private TerminalLine[] _lines;
    private TerminalLine[]? _parkedMainLines;
    private int _parkedMainRow;
    private int _parkedMainColumn;
    private TerminalStyle _parkedMainStyle = TerminalStyle.Default;

    private int _scrollbackStart;
    private int _scrollbackCount;

    private TerminalStyle _style = TerminalStyle.Default;
    private int _savedRow;
    private int _savedColumn;
    private TerminalStyle _savedStyle = TerminalStyle.Default;
    private int _scrollTop;
    private int _scrollBottom;
    private bool _pendingWrap;
    private bool _autoWrap = true;
    private bool _originMode;
    private bool _insertMode;
    private bool _alternateScreen;

    private ParserState _state = ParserState.Ground;
    private int _parameterValue = -1;
    private char _privateMarker;
    private char _pendingHighSurrogate;

    public TerminalScreen(int columns = 80, int rows = 24, int scrollbackLines = DefaultScrollbackLines)
    {
        Columns = Math.Max(MinimumColumns, columns);
        Rows = Math.Max(MinimumRows, rows);
        _scrollback = new TerminalLine?[Math.Max(0, scrollbackLines)];
        _lines = CreateLines(Rows, Columns);
        _scrollBottom = Rows - 1;
    }

    /// <summary>Raised after a batch of data changed the grid.</summary>
    public event EventHandler? Changed;

    /// <summary>Raised when the device asked a question the terminal must answer (DSR, DA).</summary>
    public event EventHandler<string>? ResponseRequested;

    /// <summary>Raised for OSC 0/2 window title changes.</summary>
    public event EventHandler<string>? TitleChanged;

    /// <summary>Raised for BEL. The view decides whether that flashes or beeps.</summary>
    public event EventHandler? BellRang;

    public int Columns { get; private set; }

    public int Rows { get; private set; }

    public int CursorRow { get; private set; }

    public int CursorColumn { get; private set; }

    public bool CursorVisible { get; private set; } = true;

    public bool ApplicationCursorKeys { get; private set; }

    public bool BracketedPaste { get; private set; }

    public bool IsAlternateScreen => _alternateScreen;

    public int ScrollbackCount => _scrollbackCount;

    public int TotalLines => _scrollbackCount + Rows;

    /// <summary>
    /// Number of lines that fell out of the scrollback ring so far. A view keeps its
    /// scroll position stable by subtracting the growth of this counter.
    /// </summary>
    public long DroppedLines { get; private set; }

    /// <summary>Incremented on every change; lets a view skip redundant redraws.</summary>
    public int Revision { get; private set; }

    public TerminalLine GetLine(int absoluteIndex)
    {
        absoluteIndex = Math.Clamp(absoluteIndex, 0, TotalLines - 1);
        if (absoluteIndex < _scrollbackCount)
        {
            return _scrollback[(_scrollbackStart + absoluteIndex) % _scrollback.Length]!;
        }

        return _lines[absoluteIndex - _scrollbackCount];
    }

    public void Write(string text) => Write(text.AsSpan());

    public void Write(ReadOnlySpan<char> text)
    {
        if (text.IsEmpty)
        {
            return;
        }

        foreach (var character in text)
        {
            Process(character);
        }

        Revision++;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Resize(int columns, int rows)
    {
        columns = Math.Max(MinimumColumns, columns);
        rows = Math.Max(MinimumRows, rows);
        if (columns == Columns && rows == Rows)
        {
            return;
        }

        if (columns != Columns)
        {
            foreach (var line in _lines)
            {
                line.Resize(columns, TerminalCell.Blank);
            }

            for (var index = 0; index < _scrollbackCount; index++)
            {
                _scrollback[(_scrollbackStart + index) % _scrollback.Length]!.Resize(columns, TerminalCell.Blank);
            }

            if (_parkedMainLines is not null)
            {
                foreach (var line in _parkedMainLines)
                {
                    line.Resize(columns, TerminalCell.Blank);
                }
            }

            Columns = columns;
        }

        if (rows != Rows)
        {
            ResizeRows(rows);
        }

        _scrollTop = 0;
        _scrollBottom = Rows - 1;
        CursorRow = Math.Clamp(CursorRow, 0, Rows - 1);
        CursorColumn = Math.Clamp(CursorColumn, 0, Columns - 1);
        _pendingWrap = false;
        Revision++;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Full reset (RIS): grid, scrollback, cursor and all modes.</summary>
    public void Reset()
    {
        Array.Clear(_scrollback);
        _scrollbackStart = 0;
        _scrollbackCount = 0;
        _alternateScreen = false;
        _parkedMainLines = null;
        _lines = CreateLines(Rows, Columns);
        _style = TerminalStyle.Default;
        _savedStyle = TerminalStyle.Default;
        CursorRow = 0;
        CursorColumn = 0;
        _savedRow = 0;
        _savedColumn = 0;
        _scrollTop = 0;
        _scrollBottom = Rows - 1;
        _pendingWrap = false;
        _autoWrap = true;
        _originMode = false;
        _insertMode = false;
        CursorVisible = true;
        ApplicationCursorKeys = false;
        BracketedPaste = false;
        _state = ParserState.Ground;
        _parameters.Clear();
        _parameterValue = -1;
        _privateMarker = '\0';
        _pendingHighSurrogate = '\0';
        _stringBuffer.Clear();
        Revision++;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Drops the scrollback and clears the visible grid, keeping all modes.</summary>
    public void ClearScreenAndScrollback()
    {
        Array.Clear(_scrollback);
        _scrollbackStart = 0;
        _scrollbackCount = 0;
        foreach (var line in _lines)
        {
            line.Clear(EraseCell());
        }

        CursorRow = 0;
        CursorColumn = 0;
        _pendingWrap = false;
        Revision++;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Text between two grid positions. <paramref name="endColumn"/> is exclusive.
    /// Rows that run to the right edge keep no trailing blanks.
    /// </summary>
    public string GetText(int startLine, int startColumn, int endLine, int endColumn)
    {
        if (startLine > endLine || (startLine == endLine && startColumn >= endColumn))
        {
            return string.Empty;
        }

        startLine = Math.Clamp(startLine, 0, TotalLines - 1);
        endLine = Math.Clamp(endLine, 0, TotalLines - 1);

        var builder = new StringBuilder();
        for (var index = startLine; index <= endLine; index++)
        {
            var line = GetLine(index);
            var from = index == startLine ? startColumn : 0;
            var to = index == endLine ? endColumn : line.Length;
            builder.Append(line.GetText(from, to, trimEnd: index != endLine || to >= line.Length));
            if (index != endLine)
            {
                builder.Append('\n');
            }
        }

        return builder.ToString();
    }

    /// <summary>Scrollback plus visible grid, trailing blank lines removed.</summary>
    public string GetAllText()
    {
        var lastContentLine = -1;
        for (var index = 0; index < TotalLines; index++)
        {
            if (GetLine(index).GetText(0, Columns, trimEnd: true).Length > 0)
            {
                lastContentLine = index;
            }
        }

        var builder = new StringBuilder();
        for (var index = 0; index <= lastContentLine; index++)
        {
            builder.Append(GetLine(index).GetText(0, Columns, trimEnd: true));
            builder.Append('\n');
        }

        return builder.ToString();
    }

    private static TerminalLine[] CreateLines(int rows, int columns)
    {
        var lines = new TerminalLine[rows];
        for (var row = 0; row < rows; row++)
        {
            lines[row] = new TerminalLine(columns);
        }

        return lines;
    }

    private void ResizeRows(int rows)
    {
        var resized = new TerminalLine[rows];
        if (rows < Rows)
        {
            // Keep the cursor on screen: move rows off the top into the scrollback only
            // as far as needed, then drop the remainder from the bottom.
            var dropFromTop = Math.Min(Rows - rows, Math.Max(0, CursorRow - rows + 1));
            for (var index = 0; index < dropFromTop; index++)
            {
                if (!_alternateScreen)
                {
                    PushScrollback(_lines[index]);
                }
            }

            Array.Copy(_lines, dropFromTop, resized, 0, rows);
            CursorRow -= dropFromTop;
        }
        else
        {
            Array.Copy(_lines, resized, Rows);
            for (var row = Rows; row < rows; row++)
            {
                resized[row] = new TerminalLine(Columns);
            }
        }

        _lines = resized;
        Rows = rows;
    }

    private void PushScrollback(TerminalLine line)
    {
        if (_scrollback.Length == 0)
        {
            return;
        }

        if (_scrollbackCount == _scrollback.Length)
        {
            _scrollback[_scrollbackStart] = line;
            _scrollbackStart = (_scrollbackStart + 1) % _scrollback.Length;
            DroppedLines++;
            return;
        }

        _scrollback[(_scrollbackStart + _scrollbackCount) % _scrollback.Length] = line;
        _scrollbackCount++;
    }

    private TerminalCell EraseCell()
        => new(' ', new TerminalStyle(TerminalColor.Default, _style.Background, TerminalGlyphFlags.None));

    private void Process(char character)
    {
        // CAN and SUB abort whatever sequence is in flight, from any state. Without this a
        // device that dies mid-sequence leaves the parser waiting for a final byte and
        // swallowing everything that follows it.
        if (character is '\x18' or '\x1a')
        {
            AbortSequence();
            return;
        }

        switch (_state)
        {
            case ParserState.Ground:
                if (character < 0x20 || character == 0x7F)
                {
                    ExecuteControl(character);
                }
                else if (character is >= '\u0080' and <= '\u009f')
                {
                    // C1 controls have no glyph. In UTF-8 mode xterm does not act on them
                    // either, and a device dumping binary produces them by the hundred, so
                    // they are dropped rather than drawn as boxes.
                }
                else
                {
                    Print(character);
                }

                break;

            case ParserState.Escape:
                HandleEscape(character);
                break;

            case ParserState.CsiParameters:
                HandleCsi(character);
                break;

            case ParserState.OperatingSystemCommand:
                HandleOsc(character);
                break;

            case ParserState.StringIgnore:
                if (character == 0x07)
                {
                    _state = ParserState.Ground;
                }
                else if (character == 0x1B)
                {
                    _state = ParserState.StringTerminator;
                }

                break;

            case ParserState.StringTerminator:
                _state = ParserState.Ground;
                if (character != '\\')
                {
                    Process(character);
                }

                break;

            case ParserState.CharacterSet:
                _state = ParserState.Ground;
                break;
        }
    }

    private void AbortSequence()
    {
        _state = ParserState.Ground;
        _parameters.Clear();
        _parameterValue = -1;
        _privateMarker = '\0';
        _stringBuffer.Clear();
    }

    private void ExecuteControl(char character)
    {
        switch (character)
        {
            case '\a':
                BellRang?.Invoke(this, EventArgs.Empty);
                break;
            case '\b':
                CursorColumn = Math.Max(0, CursorColumn - 1);
                _pendingWrap = false;
                break;
            case '\t':
                CursorColumn = Math.Min(Columns - 1, (CursorColumn / TabWidth + 1) * TabWidth);
                _pendingWrap = false;
                break;
            case '\n':
            case '\v':
            case '\f':
                LineFeed();
                break;
            case '\r':
                CursorColumn = 0;
                _pendingWrap = false;
                break;
            case '\x1b':
                _parameters.Clear();
                _parameterValue = -1;
                _privateMarker = '\0';
                _state = ParserState.Escape;
                break;
        }
    }

    private void Print(char character)
    {
        // The grid holds one UTF-16 unit per cell, so an astral character would otherwise
        // take two cells and render as two pieces of garbage. One replacement glyph per
        // code point keeps the column count honest.
        if (char.IsHighSurrogate(character))
        {
            _pendingHighSurrogate = character;
            return;
        }

        if (char.IsLowSurrogate(character))
        {
            _pendingHighSurrogate = '\0';
            character = '\ufffd';
        }
        else if (_pendingHighSurrogate != '\0')
        {
            _pendingHighSurrogate = '\0';
            Print('\ufffd');
        }

        if (_pendingWrap)
        {
            CursorColumn = 0;
            LineFeed();
            _pendingWrap = false;
        }

        var line = _lines[CursorRow];
        if (_insertMode)
        {
            line.InsertCells(CursorColumn, 1, EraseCell());
        }

        line[CursorColumn] = new TerminalCell(character, _style);

        if (CursorColumn + 1 >= Columns)
        {
            _pendingWrap = _autoWrap;
            CursorColumn = Columns - 1;
        }
        else
        {
            CursorColumn++;
        }
    }

    private void LineFeed()
    {
        if (CursorRow == _scrollBottom)
        {
            ScrollUp(1);
        }
        else if (CursorRow < Rows - 1)
        {
            CursorRow++;
        }

        _pendingWrap = false;
    }

    private void ReverseLineFeed()
    {
        if (CursorRow == _scrollTop)
        {
            ScrollDown(1);
        }
        else if (CursorRow > 0)
        {
            CursorRow--;
        }

        _pendingWrap = false;
    }

    private void ScrollUp(int count)
    {
        var regionHeight = _scrollBottom - _scrollTop + 1;
        count = Math.Clamp(count, 0, regionHeight);
        for (var step = 0; step < count; step++)
        {
            var leaving = _lines[_scrollTop];
            TerminalLine recycled;
            if (_scrollTop == 0 && !_alternateScreen)
            {
                PushScrollback(leaving);
                recycled = new TerminalLine(Columns);
            }
            else
            {
                recycled = leaving;
            }

            Array.Copy(_lines, _scrollTop + 1, _lines, _scrollTop, _scrollBottom - _scrollTop);
            recycled.Clear(EraseCell());
            _lines[_scrollBottom] = recycled;
        }
    }

    private void ScrollDown(int count)
    {
        var regionHeight = _scrollBottom - _scrollTop + 1;
        count = Math.Clamp(count, 0, regionHeight);
        for (var step = 0; step < count; step++)
        {
            var recycled = _lines[_scrollBottom];
            Array.Copy(_lines, _scrollTop, _lines, _scrollTop + 1, _scrollBottom - _scrollTop);
            recycled.Clear(EraseCell());
            _lines[_scrollTop] = recycled;
        }
    }

    private void HandleEscape(char character)
    {
        if (character == '\x7f')
        {
            return;
        }

        switch (character)
        {
            case '[':
                _state = ParserState.CsiParameters;
                return;
            case ']':
                _stringBuffer.Clear();
                _state = ParserState.OperatingSystemCommand;
                return;
            case 'P':
            case '^':
            case '_':
                _state = ParserState.StringIgnore;
                return;
            case '(':
            case ')':
            case '*':
            case '+':
            case '#':
            case '%':
                _state = ParserState.CharacterSet;
                return;
            case '7':
                SaveCursor();
                break;
            case '8':
                RestoreCursor();
                break;
            case 'D':
                LineFeed();
                break;
            case 'E':
                CursorColumn = 0;
                LineFeed();
                break;
            case 'M':
                ReverseLineFeed();
                break;
            case 'c':
                Reset();
                break;
            case 'Z':
                ResponseRequested?.Invoke(this, "\x1b[?6c");
                break;
        }

        _state = ParserState.Ground;
    }

    private void HandleCsi(char character)
    {
        if (character is >= '0' and <= '9')
        {
            _parameterValue = _parameterValue < 0 ? character - '0' : (_parameterValue * 10) + (character - '0');
            if (_parameterValue > 65_535)
            {
                _parameterValue = 65_535;
            }

            return;
        }

        // ':' separates sub-parameters (SGR 38:5:n). Treating it like ';' keeps the
        // common colour forms working without a full sub-parameter model.
        if (character is ';' or ':')
        {
            AddParameter();
            return;
        }

        if (character is '?' or '>' or '<' or '=' or '!' or '$' or '"' or '\'' or ' ')
        {
            _privateMarker = character;
            return;
        }

        if (character < 0x20)
        {
            ExecuteControl(character);
            return;
        }

        if (character == '\x7f')
        {
            return;
        }

        AddParameter();
        DispatchCsi(character);
        _parameters.Clear();
        _privateMarker = '\0';
        _state = ParserState.Ground;
    }

    private void HandleOsc(char character)
    {
        switch (character)
        {
            case '\a':
                CompleteOsc();
                _state = ParserState.Ground;
                return;
            case '\x1b':
                CompleteOsc();
                _state = ParserState.StringTerminator;
                return;
            default:
                if (_stringBuffer.Length < 512)
                {
                    _stringBuffer.Append(character);
                }

                return;
        }
    }

    private void CompleteOsc()
    {
        var payload = _stringBuffer.ToString();
        _stringBuffer.Clear();
        var separator = payload.IndexOf(';', StringComparison.Ordinal);
        if (separator <= 0)
        {
            return;
        }

        var command = payload[..separator];
        if (command is "0" or "2")
        {
            TitleChanged?.Invoke(this, payload[(separator + 1)..]);
        }
    }

    private void AddParameter()
    {
        if (_parameters.Count < MaxParameters)
        {
            _parameters.Add(_parameterValue);
        }

        _parameterValue = -1;
    }

    private int Parameter(int index, int defaultValue)
    {
        if (index >= _parameters.Count)
        {
            return defaultValue;
        }

        var value = _parameters[index];
        return value < 0 ? defaultValue : value;
    }

    private int Count(int index) => Math.Max(1, Parameter(index, 1));

    private void DispatchCsi(char final)
    {
        switch (final)
        {
            case 'A':
                CursorRow = Math.Max(TopLimit(), CursorRow - Count(0));
                _pendingWrap = false;
                break;
            case 'B':
            case 'e':
                CursorRow = Math.Min(BottomLimit(), CursorRow + Count(0));
                _pendingWrap = false;
                break;
            case 'C':
            case 'a':
                CursorColumn = Math.Min(Columns - 1, CursorColumn + Count(0));
                _pendingWrap = false;
                break;
            case 'D':
                CursorColumn = Math.Max(0, CursorColumn - Count(0));
                _pendingWrap = false;
                break;
            case 'E':
                CursorRow = Math.Min(BottomLimit(), CursorRow + Count(0));
                CursorColumn = 0;
                _pendingWrap = false;
                break;
            case 'F':
                CursorRow = Math.Max(TopLimit(), CursorRow - Count(0));
                CursorColumn = 0;
                _pendingWrap = false;
                break;
            case 'G':
            case '`':
                CursorColumn = Math.Clamp(Count(0) - 1, 0, Columns - 1);
                _pendingWrap = false;
                break;
            case 'd':
                SetCursorRow(Count(0) - 1);
                _pendingWrap = false;
                break;
            case 'H':
            case 'f':
                SetCursorRow(Count(0) - 1);
                CursorColumn = Math.Clamp(Count(1) - 1, 0, Columns - 1);
                _pendingWrap = false;
                break;
            case 'I':
                for (var step = 0; step < Count(0); step++)
                {
                    CursorColumn = Math.Min(Columns - 1, (CursorColumn / TabWidth + 1) * TabWidth);
                }

                break;
            case 'Z':
                for (var step = 0; step < Count(0); step++)
                {
                    CursorColumn = Math.Max(0, (CursorColumn - 1) / TabWidth * TabWidth);
                }

                break;
            case 'J':
                EraseInDisplay(Parameter(0, 0));
                break;
            case 'K':
                EraseInLine(Parameter(0, 0));
                break;
            case 'L':
                InsertLines(Count(0));
                break;
            case 'M':
                DeleteLines(Count(0));
                break;
            case '@':
                _lines[CursorRow].InsertCells(CursorColumn, Count(0), EraseCell());
                break;
            case 'P':
                _lines[CursorRow].DeleteCells(CursorColumn, Count(0), EraseCell());
                break;
            case 'X':
                _lines[CursorRow].ClearRange(CursorColumn, CursorColumn + Count(0), EraseCell());
                break;
            case 'S':
                ScrollUp(Count(0));
                break;
            case 'T':
                ScrollDown(Count(0));
                break;
            case 'm':
                ApplyGraphicRendition();
                break;
            case 'r':
                SetScrollRegion(Parameter(0, 1), Parameter(1, Rows));
                break;
            case 'h':
                SetMode(true);
                break;
            case 'l':
                SetMode(false);
                break;
            case 's':
                SaveCursor();
                break;
            case 'u':
                RestoreCursor();
                break;
            case 'n':
                ReportStatus(Parameter(0, 0));
                break;
            case 'c':
                if (_privateMarker != '>')
                {
                    ResponseRequested?.Invoke(this, "\x1b[?6c");
                }

                break;
        }
    }

    private int TopLimit() => _originMode ? _scrollTop : 0;

    private int BottomLimit() => _originMode ? _scrollBottom : Rows - 1;

    private void SetCursorRow(int row)
    {
        CursorRow = _originMode
            ? Math.Clamp(_scrollTop + row, _scrollTop, _scrollBottom)
            : Math.Clamp(row, 0, Rows - 1);
    }

    private void SetScrollRegion(int top, int bottom)
    {
        top = Math.Clamp(top - 1, 0, Rows - 1);
        bottom = Math.Clamp(bottom - 1, 0, Rows - 1);
        if (bottom <= top)
        {
            top = 0;
            bottom = Rows - 1;
        }

        _scrollTop = top;
        _scrollBottom = bottom;
        CursorRow = _originMode ? _scrollTop : 0;
        CursorColumn = 0;
        _pendingWrap = false;
    }

    private void EraseInDisplay(int mode)
    {
        switch (mode)
        {
            case 0:
                _lines[CursorRow].ClearRange(CursorColumn, Columns, EraseCell());
                for (var row = CursorRow + 1; row < Rows; row++)
                {
                    _lines[row].Clear(EraseCell());
                }

                break;
            case 1:
                _lines[CursorRow].ClearRange(0, CursorColumn + 1, EraseCell());
                for (var row = 0; row < CursorRow; row++)
                {
                    _lines[row].Clear(EraseCell());
                }

                break;
            case 2:
            case 3:
                foreach (var line in _lines)
                {
                    line.Clear(EraseCell());
                }

                if (mode == 3)
                {
                    Array.Clear(_scrollback);
                    _scrollbackStart = 0;
                    _scrollbackCount = 0;
                }

                break;
        }

        _pendingWrap = false;
    }

    private void EraseInLine(int mode)
    {
        var line = _lines[CursorRow];
        switch (mode)
        {
            case 0:
                line.ClearRange(CursorColumn, Columns, EraseCell());
                break;
            case 1:
                line.ClearRange(0, CursorColumn + 1, EraseCell());
                break;
            case 2:
                line.Clear(EraseCell());
                break;
        }

        _pendingWrap = false;
    }

    private void InsertLines(int count)
    {
        if (CursorRow < _scrollTop || CursorRow > _scrollBottom)
        {
            return;
        }

        count = Math.Min(count, _scrollBottom - CursorRow + 1);
        for (var step = 0; step < count; step++)
        {
            var recycled = _lines[_scrollBottom];
            Array.Copy(_lines, CursorRow, _lines, CursorRow + 1, _scrollBottom - CursorRow);
            recycled.Clear(EraseCell());
            _lines[CursorRow] = recycled;
        }
    }

    private void DeleteLines(int count)
    {
        if (CursorRow < _scrollTop || CursorRow > _scrollBottom)
        {
            return;
        }

        count = Math.Min(count, _scrollBottom - CursorRow + 1);
        for (var step = 0; step < count; step++)
        {
            var recycled = _lines[CursorRow];
            Array.Copy(_lines, CursorRow + 1, _lines, CursorRow, _scrollBottom - CursorRow);
            recycled.Clear(EraseCell());
            _lines[_scrollBottom] = recycled;
        }
    }

    private void SaveCursor()
    {
        _savedRow = CursorRow;
        _savedColumn = CursorColumn;
        _savedStyle = _style;
    }

    private void RestoreCursor()
    {
        CursorRow = Math.Clamp(_savedRow, 0, Rows - 1);
        CursorColumn = Math.Clamp(_savedColumn, 0, Columns - 1);
        _style = _savedStyle;
        _pendingWrap = false;
    }

    private void ReportStatus(int mode)
    {
        switch (mode)
        {
            case 5:
                ResponseRequested?.Invoke(this, "\x1b[0n");
                break;
            case 6:
                var row = (_originMode ? CursorRow - _scrollTop : CursorRow) + 1;
                var column = CursorColumn + 1;
                ResponseRequested?.Invoke(
                    this,
                    string.Create(CultureInfo.InvariantCulture, $"\x1b[{row};{column}R"));
                break;
        }
    }

    private void SetMode(bool enabled)
    {
        for (var index = 0; index < _parameters.Count; index++)
        {
            var mode = Parameter(index, 0);
            if (_privateMarker == '?')
            {
                switch (mode)
                {
                    case 1:
                        ApplicationCursorKeys = enabled;
                        break;
                    case 6:
                        _originMode = enabled;
                        CursorRow = enabled ? _scrollTop : 0;
                        CursorColumn = 0;
                        break;
                    case 7:
                        _autoWrap = enabled;
                        _pendingWrap = false;
                        break;
                    case 25:
                        CursorVisible = enabled;
                        break;
                    case 47:
                    case 1047:
                    case 1049:
                        SetAlternateScreen(enabled, resetCursor: mode == 1049);
                        break;
                    case 2004:
                        BracketedPaste = enabled;
                        break;
                }

                continue;
            }

            if (mode == 4)
            {
                _insertMode = enabled;
            }
        }
    }

    private void SetAlternateScreen(bool enabled, bool resetCursor)
    {
        if (enabled == _alternateScreen)
        {
            return;
        }

        if (enabled)
        {
            _parkedMainLines = _lines;
            _parkedMainRow = CursorRow;
            _parkedMainColumn = CursorColumn;
            _parkedMainStyle = _style;
            _lines = CreateLines(Rows, Columns);
            _alternateScreen = true;
            if (resetCursor)
            {
                CursorRow = 0;
                CursorColumn = 0;
            }

            _scrollTop = 0;
            _scrollBottom = Rows - 1;
            _pendingWrap = false;
            return;
        }

        var restored = _parkedMainLines ?? CreateLines(Rows, Columns);
        if (restored.Length != Rows)
        {
            var adjusted = CreateLines(Rows, Columns);
            Array.Copy(restored, adjusted, Math.Min(restored.Length, Rows));
            restored = adjusted;
        }

        _lines = restored;
        _parkedMainLines = null;
        _alternateScreen = false;
        CursorRow = Math.Clamp(_parkedMainRow, 0, Rows - 1);
        CursorColumn = Math.Clamp(_parkedMainColumn, 0, Columns - 1);
        _style = _parkedMainStyle;
        _scrollTop = 0;
        _scrollBottom = Rows - 1;
        _pendingWrap = false;
    }

    private void ApplyGraphicRendition()
    {
        if (_parameters.Count == 0)
        {
            _style = TerminalStyle.Default;
            return;
        }

        for (var index = 0; index < _parameters.Count; index++)
        {
            var code = Parameter(index, 0);
            switch (code)
            {
                case 0:
                    _style = TerminalStyle.Default;
                    break;
                case 1:
                    _style = WithFlags(_style, TerminalGlyphFlags.Bold, true);
                    break;
                case 2:
                    _style = WithFlags(_style, TerminalGlyphFlags.Faint, true);
                    break;
                case 3:
                    _style = WithFlags(_style, TerminalGlyphFlags.Italic, true);
                    break;
                case 4:
                    _style = WithFlags(_style, TerminalGlyphFlags.Underline, true);
                    break;
                case 7:
                    _style = WithFlags(_style, TerminalGlyphFlags.Inverse, true);
                    break;
                case 8:
                    _style = WithFlags(_style, TerminalGlyphFlags.Hidden, true);
                    break;
                case 9:
                    _style = WithFlags(_style, TerminalGlyphFlags.Strikethrough, true);
                    break;
                case 21:
                case 22:
                    _style = WithFlags(_style, TerminalGlyphFlags.Bold | TerminalGlyphFlags.Faint, false);
                    break;
                case 23:
                    _style = WithFlags(_style, TerminalGlyphFlags.Italic, false);
                    break;
                case 24:
                    _style = WithFlags(_style, TerminalGlyphFlags.Underline, false);
                    break;
                case 27:
                    _style = WithFlags(_style, TerminalGlyphFlags.Inverse, false);
                    break;
                case 28:
                    _style = WithFlags(_style, TerminalGlyphFlags.Hidden, false);
                    break;
                case 29:
                    _style = WithFlags(_style, TerminalGlyphFlags.Strikethrough, false);
                    break;
                case >= 30 and <= 37:
                    _style = _style with { Foreground = code - 30 };
                    break;
                case 38:
                    if (TryReadExtendedColor(ref index, out var foreground))
                    {
                        _style = _style with { Foreground = foreground };
                    }

                    break;
                case 39:
                    _style = _style with { Foreground = TerminalColor.Default };
                    break;
                case >= 40 and <= 47:
                    _style = _style with { Background = code - 40 };
                    break;
                case 48:
                    if (TryReadExtendedColor(ref index, out var background))
                    {
                        _style = _style with { Background = background };
                    }

                    break;
                case 49:
                    _style = _style with { Background = TerminalColor.Default };
                    break;
                case >= 90 and <= 97:
                    _style = _style with { Foreground = code - 90 + 8 };
                    break;
                case >= 100 and <= 107:
                    _style = _style with { Background = code - 100 + 8 };
                    break;
            }
        }
    }

    private static TerminalStyle WithFlags(TerminalStyle style, TerminalGlyphFlags flags, bool set)
        => style with { Flags = set ? style.Flags | flags : style.Flags & ~flags };

    private bool TryReadExtendedColor(ref int index, out int color)
    {
        color = TerminalColor.Default;
        var mode = Parameter(index + 1, -1);
        if (mode == 5 && index + 2 < _parameters.Count)
        {
            color = Math.Clamp(Parameter(index + 2, 0), 0, 255);
            index += 2;
            return true;
        }

        if (mode == 2 && index + 4 < _parameters.Count)
        {
            color = TerminalColor.FromRgb(Parameter(index + 2, 0), Parameter(index + 3, 0), Parameter(index + 4, 0));
            index += 4;
            return true;
        }

        return false;
    }

    private enum ParserState
    {
        Ground,
        Escape,
        CsiParameters,
        OperatingSystemCommand,
        StringIgnore,
        StringTerminator,
        CharacterSet
    }
}
