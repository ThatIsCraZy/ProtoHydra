using System.Globalization;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.VisualTree;
using DataService.Core.Terminal;

namespace DataService.App.Controls;

/// <summary>
/// Renders a <see cref="TerminalScreen"/> and turns keyboard, mouse and clipboard input
/// into the byte stream a serial device expects. Mouse behaviour follows PuTTY: dragging
/// selects and copies, the right button pastes.
/// </summary>
public sealed class TerminalControl : Control
{
    private const double PaddingLeft = 10;
    private const double PaddingTop = 8;
    private const int WheelLinesPerNotch = 3;

    public static readonly StyledProperty<TerminalScreen?> ScreenProperty =
        AvaloniaProperty.Register<TerminalControl, TerminalScreen?>(nameof(Screen));

    public static readonly StyledProperty<FontFamily> TerminalFontFamilyProperty =
        AvaloniaProperty.Register<TerminalControl, FontFamily>(
            nameof(TerminalFontFamily),
            new FontFamily("Cascadia Mono, Consolas, Courier New, monospace"));

    public static readonly StyledProperty<double> TerminalFontSizeProperty =
        AvaloniaProperty.Register<TerminalControl, double>(nameof(TerminalFontSize), 13d);

    public static readonly StyledProperty<IBrush?> TerminalBackgroundProperty =
        AvaloniaProperty.Register<TerminalControl, IBrush?>(nameof(TerminalBackground));

    public static readonly StyledProperty<IBrush?> TerminalForegroundProperty =
        AvaloniaProperty.Register<TerminalControl, IBrush?>(nameof(TerminalForeground));

    public static readonly StyledProperty<IBrush?> SelectionBrushProperty =
        AvaloniaProperty.Register<TerminalControl, IBrush?>(nameof(SelectionBrush));

    public static readonly StyledProperty<IBrush?> CursorBrushProperty =
        AvaloniaProperty.Register<TerminalControl, IBrush?>(nameof(CursorBrush));

    /// <summary>What the Enter key and pasted line breaks put on the wire.</summary>
    public static readonly StyledProperty<string> NewLineSequenceProperty =
        AvaloniaProperty.Register<TerminalControl, string>(nameof(NewLineSequence), "\r");

    public static readonly DirectProperty<TerminalControl, double> ScrollOffsetProperty =
        AvaloniaProperty.RegisterDirect<TerminalControl, double>(
            nameof(ScrollOffset),
            control => control.ScrollOffset,
            (control, value) => control.ScrollOffset = value,
            defaultBindingMode: BindingMode.TwoWay);

    public static readonly DirectProperty<TerminalControl, double> TotalLinesProperty =
        AvaloniaProperty.RegisterDirect<TerminalControl, double>(
            nameof(TotalLines),
            control => control.TotalLines);

    public static readonly DirectProperty<TerminalControl, double> ViewportLinesProperty =
        AvaloniaProperty.RegisterDirect<TerminalControl, double>(
            nameof(ViewportLines),
            control => control.ViewportLines);

    /// <summary>Largest valid <see cref="ScrollOffset"/>; drives the scroll bar range.</summary>
    public static readonly DirectProperty<TerminalControl, double> ScrollMaximumProperty =
        AvaloniaProperty.RegisterDirect<TerminalControl, double>(
            nameof(ScrollMaximum),
            control => control.ScrollMaximum);

    private readonly Dictionary<int, IBrush> _brushCache = new();
    private readonly StringBuilder _runBuilder = new();

    private TerminalScreen? _attachedScreen;
    private Size _viewportSize;
    private double _cellWidth = 8;
    private double _cellHeight = 16;
    private double _baseline;
    private double _scrollOffset;
    private double _totalLines;
    private double _viewportLines;
    private double _scrollMaximum;
    private long _lastDroppedLines;
    private bool _stickToBottom = true;
    private bool _isSelecting;
    private bool _hasSelection;
    private int _selectionAnchorLine;
    private int _selectionAnchorColumn;
    private int _selectionFocusLine;
    private int _selectionFocusColumn;

    public TerminalControl()
    {
        Focusable = true;
        ClipToBounds = true;
        Cursor = new Cursor(StandardCursorType.Ibeam);
        ActualThemeVariantChanged += (_, _) =>
        {
            _brushCache.Clear();
            InvalidateVisual();
        };
    }

    /// <summary>Raised with the text the user produced; the host encodes and sends it.</summary>
    public event EventHandler<string>? TextSubmitted;

    public TerminalScreen? Screen
    {
        get => GetValue(ScreenProperty);
        set => SetValue(ScreenProperty, value);
    }

    public FontFamily TerminalFontFamily
    {
        get => GetValue(TerminalFontFamilyProperty);
        set => SetValue(TerminalFontFamilyProperty, value);
    }

    public double TerminalFontSize
    {
        get => GetValue(TerminalFontSizeProperty);
        set => SetValue(TerminalFontSizeProperty, value);
    }

    public IBrush? TerminalBackground
    {
        get => GetValue(TerminalBackgroundProperty);
        set => SetValue(TerminalBackgroundProperty, value);
    }

    public IBrush? TerminalForeground
    {
        get => GetValue(TerminalForegroundProperty);
        set => SetValue(TerminalForegroundProperty, value);
    }

    public IBrush? SelectionBrush
    {
        get => GetValue(SelectionBrushProperty);
        set => SetValue(SelectionBrushProperty, value);
    }

    public IBrush? CursorBrush
    {
        get => GetValue(CursorBrushProperty);
        set => SetValue(CursorBrushProperty, value);
    }

    public string NewLineSequence
    {
        get => GetValue(NewLineSequenceProperty);
        set => SetValue(NewLineSequenceProperty, value);
    }

    public double ScrollOffset
    {
        get => _scrollOffset;
        set
        {
            var clamped = Math.Clamp(Math.Round(value), 0, Math.Max(0, _totalLines - _viewportLines));
            if (SetAndRaise(ScrollOffsetProperty, ref _scrollOffset, clamped))
            {
                _stickToBottom = Math.Abs(_scrollOffset - Math.Max(0, _totalLines - _viewportLines)) < 0.5;
                InvalidateVisual();
            }
        }
    }

    public double TotalLines => _totalLines;

    public double ViewportLines => _viewportLines;

    public double ScrollMaximum => _scrollMaximum;

    public bool HasSelection => _hasSelection;

    public void ScrollToBottom() => ScrollOffset = Math.Max(0, _totalLines - _viewportLines);

    public void ScrollByLines(int lines) => ScrollOffset = _scrollOffset + lines;

    public void ClearSelection()
    {
        if (!_hasSelection)
        {
            return;
        }

        _hasSelection = false;
        InvalidateVisual();
    }

    public void SelectAll()
    {
        var screen = Screen;
        if (screen is null || screen.TotalLines == 0)
        {
            return;
        }

        _selectionAnchorLine = 0;
        _selectionAnchorColumn = 0;
        _selectionFocusLine = screen.TotalLines - 1;
        _selectionFocusColumn = screen.Columns;
        _hasSelection = true;
        InvalidateVisual();
    }

    public string GetSelectedText()
    {
        var screen = Screen;
        if (screen is null || !_hasSelection)
        {
            return string.Empty;
        }

        Normalize(out var startLine, out var startColumn, out var endLine, out var endColumn);
        return screen.GetText(startLine, startColumn, endLine, endColumn);
    }

    public async Task CopySelectionAsync()
    {
        var text = GetSelectedText();
        if (text.Length == 0)
        {
            return;
        }

        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is not null)
        {
            await clipboard.SetTextAsync(text);
        }
    }

    public async Task PasteAsync()
    {
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is null)
        {
            return;
        }

        var text = await clipboard.TryGetTextAsync();
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        Submit(PreparePaste(text));
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == ScreenProperty)
        {
            DetachScreen();
            AttachScreen(change.GetNewValue<TerminalScreen?>());
            ApplyViewportToScreen();
            InvalidateVisual();
            return;
        }

        if (change.Property == TerminalFontFamilyProperty || change.Property == TerminalFontSizeProperty)
        {
            UpdateMetrics();
            ApplyViewportToScreen();
            InvalidateVisual();
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        UpdateMetrics();
        AttachScreen(Screen);
        _viewportSize = Bounds.Size;
        ApplyViewportToScreen();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        DetachScreen();
        base.OnDetachedFromVisualTree(e);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var size = base.ArrangeOverride(finalSize);

        // Bounds still holds the previous arrangement at this point, so the grid is sized
        // from finalSize; reading Bounds here would size the screen from stale numbers.
        _viewportSize = size;
        ApplyViewportToScreen();
        return size;
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);

        var bounds = new Rect(Bounds.Size);
        var background = TerminalBackground ?? Brushes.Black;
        context.FillRectangle(background, bounds);

        var screen = Screen;
        if (screen is null || _cellHeight <= 0)
        {
            return;
        }

        var isDark = ActualThemeVariant == ThemeVariant.Dark;
        var defaultForeground = (TerminalForeground as ISolidColorBrush)?.Color ?? Colors.White;
        var defaultBackground = (background as ISolidColorBrush)?.Color ?? Colors.Black;
        var first = (int)_scrollOffset;
        var visibleRows = Math.Min(VisibleRows, Math.Max(0, screen.TotalLines - first));

        Normalize(out var selectionStartLine, out var selectionStartColumn, out var selectionEndLine, out var selectionEndColumn);

        for (var row = 0; row < visibleRows; row++)
        {
            var absoluteLine = first + row;
            var line = screen.GetLine(absoluteLine);
            var y = PaddingTop + (row * _cellHeight);
            RenderLine(
                context,
                line,
                absoluteLine,
                y,
                isDark,
                defaultForeground,
                defaultBackground,
                selectionStartLine,
                selectionStartColumn,
                selectionEndLine,
                selectionEndColumn);
        }

        RenderCursor(context, screen, first, defaultBackground);
    }

    protected override void OnGotFocus(FocusChangedEventArgs e)
    {
        base.OnGotFocus(e);
        InvalidateVisual();
    }

    protected override void OnLostFocus(FocusChangedEventArgs e)
    {
        base.OnLostFocus(e);
        InvalidateVisual();
    }

    protected override void OnTextInput(TextInputEventArgs e)
    {
        base.OnTextInput(e);
        if (string.IsNullOrEmpty(e.Text))
        {
            return;
        }

        // Control characters arrive through OnKeyDown; letting them through here would
        // send every Ctrl chord twice.
        var filtered = new string(e.Text.Where(character => !char.IsControl(character)).ToArray());
        if (filtered.Length == 0)
        {
            return;
        }

        Submit(filtered);
        e.Handled = true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled)
        {
            return;
        }

        var control = e.KeyModifiers.HasFlag(KeyModifiers.Control);
        var shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        var alt = e.KeyModifiers.HasFlag(KeyModifiers.Alt);

        if (control && shift)
        {
            switch (e.Key)
            {
                case Key.C:
                    _ = CopySelectionAsync();
                    e.Handled = true;
                    return;
                case Key.V:
                    _ = PasteAsync();
                    e.Handled = true;
                    return;
                case Key.A:
                    SelectAll();
                    e.Handled = true;
                    return;
            }
        }

        if (shift)
        {
            switch (e.Key)
            {
                case Key.Insert:
                    _ = PasteAsync();
                    e.Handled = true;
                    return;
                case Key.PageUp:
                    ScrollByLines(-Math.Max(1, VisibleRows - 1));
                    e.Handled = true;
                    return;
                case Key.PageDown:
                    ScrollByLines(Math.Max(1, VisibleRows - 1));
                    e.Handled = true;
                    return;
            }
        }

        var sequence = ResolveKeySequence(e.Key, control, shift, alt);
        if (sequence is null)
        {
            return;
        }

        Submit(sequence);
        e.Handled = true;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();

        var point = e.GetCurrentPoint(this);
        if (point.Properties.IsRightButtonPressed || point.Properties.IsMiddleButtonPressed)
        {
            _ = PasteAsync();
            e.Handled = true;
            return;
        }

        if (!point.Properties.IsLeftButtonPressed)
        {
            return;
        }

        var (line, column) = HitTest(point.Position);
        switch (e.ClickCount)
        {
            case 2:
                SelectWord(line, column);
                break;
            case >= 3:
                SelectLine(line);
                break;
            default:
                _selectionAnchorLine = line;
                _selectionAnchorColumn = column;
                _selectionFocusLine = line;
                _selectionFocusColumn = column;
                _hasSelection = false;
                _isSelecting = true;
                e.Pointer.Capture(this);
                break;
        }

        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (!_isSelecting)
        {
            return;
        }

        var position = e.GetPosition(this);
        if (position.Y < PaddingTop)
        {
            ScrollByLines(-1);
        }
        else if (position.Y > _viewportSize.Height - PaddingTop)
        {
            ScrollByLines(1);
        }

        var (line, column) = HitTest(position);
        _selectionFocusLine = line;
        _selectionFocusColumn = column;
        _hasSelection = _selectionFocusLine != _selectionAnchorLine || _selectionFocusColumn != _selectionAnchorColumn;
        InvalidateVisual();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (!_isSelecting)
        {
            return;
        }

        _isSelecting = false;
        e.Pointer.Capture(null);
        if (_hasSelection)
        {
            _ = CopySelectionAsync();
        }
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        ScrollByLines((int)Math.Round(-e.Delta.Y * WheelLinesPerNotch));
        e.Handled = true;
    }

    private int VisibleRows => Math.Max(1, (int)Math.Floor((_viewportSize.Height - (PaddingTop * 2)) / _cellHeight));

    private int VisibleColumns => Math.Max(1, (int)Math.Floor((_viewportSize.Width - (PaddingLeft * 2)) / _cellWidth));

    private void AttachScreen(TerminalScreen? screen)
    {
        if (screen is null || ReferenceEquals(screen, _attachedScreen))
        {
            return;
        }

        _attachedScreen = screen;
        _lastDroppedLines = screen.DroppedLines;
        screen.Changed += OnScreenChanged;
        UpdateScrollMetrics();
    }

    private void DetachScreen()
    {
        if (_attachedScreen is null)
        {
            return;
        }

        _attachedScreen.Changed -= OnScreenChanged;
        _attachedScreen = null;
    }

    private void OnScreenChanged(object? sender, EventArgs e)
    {
        var screen = _attachedScreen;
        if (screen is not null && !_stickToBottom)
        {
            // Lines falling out of the scrollback shift every absolute index down by one;
            // without this correction a parked viewport would creep forward.
            var dropped = screen.DroppedLines;
            var delta = dropped - _lastDroppedLines;
            if (delta > 0)
            {
                SetAndRaise(ScrollOffsetProperty, ref _scrollOffset, Math.Max(0, _scrollOffset - delta));
                if (_hasSelection)
                {
                    _selectionAnchorLine -= (int)delta;
                    _selectionFocusLine -= (int)delta;
                    _hasSelection = _selectionAnchorLine >= 0 && _selectionFocusLine >= 0;
                }
            }

            _lastDroppedLines = dropped;
        }
        else if (screen is not null)
        {
            _lastDroppedLines = screen.DroppedLines;
        }

        UpdateScrollMetrics();
        if (_stickToBottom)
        {
            ScrollToBottom();
        }

        InvalidateVisual();
    }

    private void UpdateMetrics()
    {
        var typeface = new Typeface(TerminalFontFamily);
        var probe = new FormattedText(
            new string('0', 32),
            CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            typeface,
            TerminalFontSize,
            Brushes.Black);

        _cellWidth = Math.Max(1, probe.WidthIncludingTrailingWhitespace / 32);
        _cellHeight = Math.Max(1, Math.Ceiling(probe.Height));
        _baseline = probe.Baseline;
    }

    private void ApplyViewportToScreen()
    {
        if (_viewportSize.Width <= 0 || _viewportSize.Height <= 0)
        {
            return;
        }

        Screen?.Resize(VisibleColumns, VisibleRows);
        UpdateScrollMetrics();
        if (_stickToBottom)
        {
            ScrollToBottom();
        }
    }

    private void UpdateScrollMetrics()
    {
        var total = (double)(Screen?.TotalLines ?? 0);
        SetAndRaise(TotalLinesProperty, ref _totalLines, total);
        SetAndRaise(ViewportLinesProperty, ref _viewportLines, VisibleRows);

        var maximum = Math.Max(0, _totalLines - _viewportLines);
        SetAndRaise(ScrollMaximumProperty, ref _scrollMaximum, maximum);
        if (_scrollOffset > maximum)
        {
            SetAndRaise(ScrollOffsetProperty, ref _scrollOffset, maximum);
        }
    }

    private (int Line, int Column) HitTest(Point position)
    {
        var screen = Screen;
        var row = (int)Math.Floor((position.Y - PaddingTop) / _cellHeight);
        var line = Math.Clamp((int)_scrollOffset + row, 0, Math.Max(0, (screen?.TotalLines ?? 1) - 1));
        var column = (int)Math.Round((position.X - PaddingLeft) / _cellWidth);
        column = Math.Clamp(column, 0, screen?.Columns ?? 0);
        return (line, column);
    }

    private void SelectWord(int line, int column)
    {
        var screen = Screen;
        if (screen is null)
        {
            return;
        }

        var cells = screen.GetLine(line);
        column = Math.Clamp(column, 0, cells.Length - 1);
        if (!IsWordCharacter(cells[column].Character))
        {
            SelectLine(line);
            return;
        }

        var start = column;
        while (start > 0 && IsWordCharacter(cells[start - 1].Character))
        {
            start--;
        }

        var end = column;
        while (end + 1 < cells.Length && IsWordCharacter(cells[end + 1].Character))
        {
            end++;
        }

        _selectionAnchorLine = line;
        _selectionAnchorColumn = start;
        _selectionFocusLine = line;
        _selectionFocusColumn = end + 1;
        _hasSelection = true;
    }

    private void SelectLine(int line)
    {
        var screen = Screen;
        if (screen is null)
        {
            return;
        }

        _selectionAnchorLine = line;
        _selectionAnchorColumn = 0;
        _selectionFocusLine = line;
        _selectionFocusColumn = screen.Columns;
        _hasSelection = true;
    }

    private static bool IsWordCharacter(char character)
        => char.IsLetterOrDigit(character) || character is '_' or '-' or '.' or '/' or '\\' or ':' or '@';

    private void Normalize(out int startLine, out int startColumn, out int endLine, out int endColumn)
    {
        if (_selectionAnchorLine < _selectionFocusLine
            || (_selectionAnchorLine == _selectionFocusLine && _selectionAnchorColumn <= _selectionFocusColumn))
        {
            startLine = _selectionAnchorLine;
            startColumn = _selectionAnchorColumn;
            endLine = _selectionFocusLine;
            endColumn = _selectionFocusColumn;
            return;
        }

        startLine = _selectionFocusLine;
        startColumn = _selectionFocusColumn;
        endLine = _selectionAnchorLine;
        endColumn = _selectionAnchorColumn;
    }

    private bool IsSelected(int line, int column, int startLine, int startColumn, int endLine, int endColumn)
    {
        if (!_hasSelection || line < startLine || line > endLine)
        {
            return false;
        }

        if (line == startLine && column < startColumn)
        {
            return false;
        }

        return line != endLine || column < endColumn;
    }

    private void RenderLine(
        DrawingContext context,
        TerminalLine line,
        int absoluteLine,
        double y,
        bool isDark,
        Color defaultForeground,
        Color defaultBackground,
        int selectionStartLine,
        int selectionStartColumn,
        int selectionEndLine,
        int selectionEndColumn)
    {
        var columns = Math.Min(line.Length, VisibleColumns);
        var column = 0;
        while (column < columns)
        {
            var cell = line[column];
            var selected = IsSelected(
                absoluteLine, column, selectionStartLine, selectionStartColumn, selectionEndLine, selectionEndColumn);

            var runLength = 1;
            while (column + runLength < columns
                && line[column + runLength].Style == cell.Style
                && IsSelected(
                    absoluteLine,
                    column + runLength,
                    selectionStartLine,
                    selectionStartColumn,
                    selectionEndLine,
                    selectionEndColumn) == selected)
            {
                runLength++;
            }

            RenderRun(context, line, column, runLength, cell.Style, selected, y, isDark, defaultForeground, defaultBackground);
            column += runLength;
        }
    }

    private void RenderRun(
        DrawingContext context,
        TerminalLine line,
        int column,
        int length,
        TerminalStyle style,
        bool selected,
        double y,
        bool isDark,
        Color defaultForeground,
        Color defaultBackground)
    {
        var inverse = style.HasFlag(TerminalGlyphFlags.Inverse);
        var foregroundColor = TerminalPalette.Resolve(
            inverse ? style.Background : style.Foreground,
            isDark,
            inverse ? defaultBackground : defaultForeground);
        var backgroundColor = TerminalPalette.Resolve(
            inverse ? style.Foreground : style.Background,
            isDark,
            inverse ? defaultForeground : defaultBackground);

        var x = PaddingLeft + (column * _cellWidth);
        var width = length * _cellWidth;

        if (selected)
        {
            context.FillRectangle(
                SelectionBrush ?? new SolidColorBrush(Color.FromArgb(0x66, 0x4C, 0x8F, 0xE0)),
                new Rect(x, y, width, _cellHeight));
        }
        else if (backgroundColor != defaultBackground)
        {
            context.FillRectangle(GetBrush(backgroundColor), new Rect(x, y, width, _cellHeight));
        }

        if (style.HasFlag(TerminalGlyphFlags.Hidden))
        {
            return;
        }

        _runBuilder.Clear();
        var hasGlyphs = false;
        for (var index = 0; index < length; index++)
        {
            var character = line[column + index].Character;
            if (character is '\0')
            {
                character = ' ';
            }

            if (character != ' ')
            {
                hasGlyphs = true;
            }

            _runBuilder.Append(character);
        }

        var underline = style.HasFlag(TerminalGlyphFlags.Underline);
        var strikethrough = style.HasFlag(TerminalGlyphFlags.Strikethrough);
        if (!hasGlyphs && !underline && !strikethrough)
        {
            return;
        }

        if (style.HasFlag(TerminalGlyphFlags.Faint))
        {
            foregroundColor = Blend(foregroundColor, backgroundColor, 0.45);
        }

        var brush = GetBrush(foregroundColor);
        if (hasGlyphs)
        {
            var typeface = new Typeface(
                TerminalFontFamily,
                style.HasFlag(TerminalGlyphFlags.Italic) ? FontStyle.Italic : FontStyle.Normal,
                style.HasFlag(TerminalGlyphFlags.Bold) ? FontWeight.Bold : FontWeight.Normal);
            var text = new FormattedText(
                _runBuilder.ToString(),
                CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,
                typeface,
                TerminalFontSize,
                brush);
            context.DrawText(text, new Point(x, y));
        }

        if (underline)
        {
            var underlineY = y + _baseline + 1.5;
            context.DrawLine(new Pen(brush, 1), new Point(x, underlineY), new Point(x + width, underlineY));
        }

        if (strikethrough)
        {
            var strikeY = y + (_cellHeight / 2);
            context.DrawLine(new Pen(brush, 1), new Point(x, strikeY), new Point(x + width, strikeY));
        }
    }

    private void RenderCursor(DrawingContext context, TerminalScreen screen, int firstVisibleLine, Color defaultBackground)
    {
        if (!screen.CursorVisible)
        {
            return;
        }

        var cursorLine = screen.ScrollbackCount + screen.CursorRow;
        var row = cursorLine - firstVisibleLine;
        if (row < 0 || row >= VisibleRows || screen.CursorColumn >= VisibleColumns)
        {
            return;
        }

        var rect = new Rect(
            PaddingLeft + (screen.CursorColumn * _cellWidth),
            PaddingTop + (row * _cellHeight),
            _cellWidth,
            _cellHeight);
        var brush = CursorBrush ?? Brushes.LimeGreen;

        if (!IsFocused)
        {
            context.DrawRectangle(null, new Pen(brush, 1), rect.Deflate(0.5));
            return;
        }

        context.FillRectangle(brush, rect);

        var character = screen.GetLine(cursorLine)[screen.CursorColumn].Character;
        if (character is '\0' or ' ')
        {
            return;
        }

        var text = new FormattedText(
            character.ToString(),
            CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            new Typeface(TerminalFontFamily),
            TerminalFontSize,
            GetBrush(defaultBackground));
        context.DrawText(text, new Point(rect.X, rect.Y));
    }

    private IBrush GetBrush(Color color)
    {
        var key = (int)color.ToUInt32();
        if (_brushCache.TryGetValue(key, out var brush))
        {
            return brush;
        }

        brush = new SolidColorBrush(color);
        _brushCache[key] = brush;
        return brush;
    }

    private static Color Blend(Color foreground, Color background, double amount)
        => Color.FromRgb(
            (byte)((foreground.R * (1 - amount)) + (background.R * amount)),
            (byte)((foreground.G * (1 - amount)) + (background.G * amount)),
            (byte)((foreground.B * (1 - amount)) + (background.B * amount)));

    private void Submit(string text)
    {
        if (text.Length == 0)
        {
            return;
        }

        ClearSelection();
        ScrollToBottom();
        TextSubmitted?.Invoke(this, text);
    }

    /// <summary>
    /// Normalises clipboard text for a console: line breaks become the configured Enter
    /// sequence and stray control characters are dropped, so a copied block does not fire
    /// escape sequences at the device.
    /// </summary>
    private string PreparePaste(string text)
    {
        var newLine = NewLineSequence;
        var builder = new StringBuilder(text.Length);
        for (var index = 0; index < text.Length; index++)
        {
            var character = text[index];
            switch (character)
            {
                case '\r':
                    builder.Append(newLine);
                    if (index + 1 < text.Length && text[index + 1] == '\n')
                    {
                        index++;
                    }

                    break;
                case '\n':
                    builder.Append(newLine);
                    break;
                case '\t':
                    builder.Append('\t');
                    break;
                default:
                    if (!char.IsControl(character))
                    {
                        builder.Append(character);
                    }

                    break;
            }
        }

        var payload = builder.ToString();
        return Screen?.BracketedPaste == true ? $"\x1b[200~{payload}\x1b[201~" : payload;
    }

    private string? ResolveKeySequence(Key key, bool control, bool shift, bool alt)
    {
        var screen = Screen;
        var cursorPrefix = screen?.ApplicationCursorKeys == true ? "\x1bO" : "\x1b[";

        if (control && !alt)
        {
            if (key is >= Key.A and <= Key.Z)
            {
                return ((char)(key - Key.A + 1)).ToString();
            }

            switch (key)
            {
                case Key.OemOpenBrackets:
                    return "\x1b";
                case Key.OemCloseBrackets:
                    return "\x1d";
                case Key.OemBackslash:
                case Key.Oem5:
                    return "\x1c";
                case Key.Space:
                    return "\0";
            }
        }

        return key switch
        {
            Key.Enter => NewLineSequence,
            Key.Back => "\x7f",
            Key.Tab => shift ? "\x1b[Z" : "\t",
            Key.Escape => "\x1b",
            Key.Up => cursorPrefix + "A",
            Key.Down => cursorPrefix + "B",
            Key.Right => cursorPrefix + "C",
            Key.Left => cursorPrefix + "D",
            Key.Home => "\x1b[H",
            Key.End => "\x1b[F",
            Key.Insert => "\x1b[2~",
            Key.Delete => "\x1b[3~",
            Key.PageUp => "\x1b[5~",
            Key.PageDown => "\x1b[6~",
            Key.F1 => "\x1bOP",
            Key.F2 => "\x1bOQ",
            Key.F3 => "\x1bOR",
            Key.F4 => "\x1bOS",
            Key.F5 => "\x1b[15~",
            Key.F6 => "\x1b[17~",
            Key.F7 => "\x1b[18~",
            Key.F8 => "\x1b[19~",
            Key.F9 => "\x1b[20~",
            Key.F10 => "\x1b[21~",
            Key.F11 => "\x1b[23~",
            Key.F12 => "\x1b[24~",
            _ => null
        };
    }
}
