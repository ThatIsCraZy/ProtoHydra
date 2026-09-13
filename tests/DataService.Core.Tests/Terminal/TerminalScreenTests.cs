using DataService.Core.Terminal;

namespace DataService.Core.Tests.Terminal;

public sealed class TerminalScreenTests
{
    [Fact]
    public void PlainTextLandsOnTheFirstRow()
    {
        var screen = new TerminalScreen(40, 10);

        screen.Write("hello");

        Assert.Equal("hello", screen.GetLine(0).GetText(0, screen.Columns, trimEnd: true));
        Assert.Equal(0, screen.CursorRow);
        Assert.Equal(5, screen.CursorColumn);
    }

    [Fact]
    public void CarriageReturnOverwritesTheSameRow()
    {
        var screen = new TerminalScreen(40, 10);

        screen.Write("Progress 10%\rProgress 90%");

        Assert.Equal("Progress 90%", screen.GetLine(0).GetText(0, screen.Columns, trimEnd: true));
    }

    [Fact]
    public void BackspaceMovesTheCursorWithoutErasing()
    {
        var screen = new TerminalScreen(40, 10);

        screen.Write("abc\b\bX");

        Assert.Equal("aXc", screen.GetLine(0).GetText(0, screen.Columns, trimEnd: true));
    }

    [Fact]
    public void LineFeedsBeyondTheLastRowFillTheScrollback()
    {
        var screen = new TerminalScreen(40, 4);

        for (var index = 0; index < 6; index++)
        {
            screen.Write($"line{index}\r\n");
        }

        Assert.Equal(3, screen.ScrollbackCount);
        Assert.Equal(7, screen.TotalLines);
        Assert.Equal("line0", screen.GetLine(0).GetText(0, screen.Columns, trimEnd: true));
        Assert.Equal("line5", screen.GetLine(5).GetText(0, screen.Columns, trimEnd: true));
    }

    [Fact]
    public void ScrollbackRingDropsTheOldestLines()
    {
        var screen = new TerminalScreen(40, 4, scrollbackLines: 2);

        for (var index = 0; index < 8; index++)
        {
            screen.Write($"line{index}\r\n");
        }

        Assert.Equal(2, screen.ScrollbackCount);
        Assert.Equal(3, screen.DroppedLines);
        Assert.Equal("line3", screen.GetLine(0).GetText(0, screen.Columns, trimEnd: true));
    }

    [Fact]
    public void GraphicRenditionColoursTheFollowingCells()
    {
        var screen = new TerminalScreen(40, 10);

        screen.Write("\x1b[31mred\x1b[0mplain");

        Assert.Equal(1, screen.GetLine(0)[0].Style.Foreground);
        Assert.True(screen.GetLine(0)[0].Style.Flags == TerminalGlyphFlags.None);
        Assert.Equal(TerminalColor.Default, screen.GetLine(0)[3].Style.Foreground);
    }

    [Fact]
    public void ExtendedColourSequencesAreUnderstood()
    {
        var screen = new TerminalScreen(40, 10);

        screen.Write("\x1b[38;5;244mgrey\x1b[0m");

        Assert.Equal(244, screen.GetLine(0)[0].Style.Foreground);
        Assert.Equal("grey", screen.GetLine(0).GetText(0, screen.Columns, trimEnd: true));
    }

    [Fact]
    public void BoldAndUnderlineAccumulateAndReset()
    {
        var screen = new TerminalScreen(40, 10);

        screen.Write("\x1b[1;4mX\x1b[24mY\x1b[0mZ");

        var line = screen.GetLine(0);
        Assert.True(line[0].Style.HasFlag(TerminalGlyphFlags.Bold));
        Assert.True(line[0].Style.HasFlag(TerminalGlyphFlags.Underline));
        Assert.True(line[1].Style.HasFlag(TerminalGlyphFlags.Bold));
        Assert.False(line[1].Style.HasFlag(TerminalGlyphFlags.Underline));
        Assert.Equal(TerminalStyle.Default, line[2].Style);
    }

    [Fact]
    public void CursorPositioningAddressesAbsoluteCells()
    {
        var screen = new TerminalScreen(40, 10);

        screen.Write("\x1b[3;5HX");

        Assert.Equal("    X", screen.GetLine(2).GetText(0, screen.Columns, trimEnd: true));
    }

    [Fact]
    public void EraseInDisplayClearsFromTheCursorDown()
    {
        var screen = new TerminalScreen(40, 4);

        screen.Write("one\r\ntwo\r\nthree");
        screen.Write("\x1b[2;1H\x1b[J");

        Assert.Equal("one", screen.GetLine(0).GetText(0, screen.Columns, trimEnd: true));
        Assert.Equal("", screen.GetLine(1).GetText(0, screen.Columns, trimEnd: true));
        Assert.Equal("", screen.GetLine(2).GetText(0, screen.Columns, trimEnd: true));
    }

    [Fact]
    public void EraseInLineClearsToTheRightOfTheCursor()
    {
        var screen = new TerminalScreen(40, 4);

        screen.Write("abcdef\x1b[1;4H\x1b[K");

        Assert.Equal("abc", screen.GetLine(0).GetText(0, screen.Columns, trimEnd: true));
    }

    [Fact]
    public void AutoWrapContinuesOnTheNextRow()
    {
        var screen = new TerminalScreen(20, 6);

        screen.Write(new string('x', 22));

        Assert.Equal(20, screen.GetLine(0).GetText(0, screen.Columns, trimEnd: true).Length);
        Assert.Equal("xx", screen.GetLine(1).GetText(0, screen.Columns, trimEnd: true));
    }

    [Fact]
    public void ScrollRegionKeepsTheHeaderStill()
    {
        var screen = new TerminalScreen(20, 5);

        screen.Write("header\r\n");
        screen.Write("\x1b[2;5r");
        screen.Write("\x1b[2;1Ha\r\nb\r\nc\r\nd\r\ne");

        Assert.Equal("header", screen.GetLine(0).GetText(0, screen.Columns, trimEnd: true));
        Assert.Equal("e", screen.GetLine(4).GetText(0, screen.Columns, trimEnd: true));
    }

    [Fact]
    public void AlternateScreenIsRestoredWithItsPredecessor()
    {
        var screen = new TerminalScreen(20, 5);

        screen.Write("main content");
        screen.Write("\x1b[?1049h");
        screen.Write("menu");
        Assert.True(screen.IsAlternateScreen);
        Assert.Equal("menu", screen.GetLine(0).GetText(0, screen.Columns, trimEnd: true));

        screen.Write("\x1b[?1049l");

        Assert.False(screen.IsAlternateScreen);
        Assert.Equal("main content", screen.GetLine(0).GetText(0, screen.Columns, trimEnd: true));
    }

    [Fact]
    public void DeviceStatusReportIsAnswered()
    {
        var screen = new TerminalScreen(20, 5);
        var responses = new List<string>();
        screen.ResponseRequested += (_, response) => responses.Add(response);

        screen.Write("\x1b[3;7H\x1b[6n");

        Assert.Equal(["\x1b[3;7R"], responses);
    }

    [Fact]
    public void WindowTitleSequenceIsReported()
    {
        var screen = new TerminalScreen(20, 5);
        var titles = new List<string>();
        screen.TitleChanged += (_, title) => titles.Add(title);

        screen.Write("\x1b]0;switch-01\x07ready");

        Assert.Equal(["switch-01"], titles);
        Assert.Equal("ready", screen.GetLine(0).GetText(0, screen.Columns, trimEnd: true));
    }

    [Fact]
    public void CursorVisibilityAndApplicationKeysFollowPrivateModes()
    {
        var screen = new TerminalScreen(20, 5);

        screen.Write("\x1b[?25l\x1b[?1h");

        Assert.False(screen.CursorVisible);
        Assert.True(screen.ApplicationCursorKeys);

        screen.Write("\x1b[?25h\x1b[?1l");

        Assert.True(screen.CursorVisible);
        Assert.False(screen.ApplicationCursorKeys);
    }

    [Fact]
    public void ResizeKeepsExistingContent()
    {
        var screen = new TerminalScreen(20, 5);
        screen.Write("kept across resize");

        screen.Resize(60, 20);

        Assert.Equal(60, screen.Columns);
        Assert.Equal(20, screen.Rows);
        Assert.Equal("kept across resize", screen.GetLine(0).GetText(0, screen.Columns, trimEnd: true));
    }

    [Fact]
    public void SelectionTextSpansRowsWithoutTrailingBlanks()
    {
        var screen = new TerminalScreen(20, 5);
        screen.Write("first\r\nsecond\r\nthird");

        Assert.Equal("first\nsecond\nthi", screen.GetText(0, 0, 2, 3));
    }

    [Fact]
    public void SequencesSplitAcrossWritesStillParse()
    {
        var screen = new TerminalScreen(20, 5);

        screen.Write("\x1b[3");
        screen.Write("1mred");

        Assert.Equal(1, screen.GetLine(0)[0].Style.Foreground);
        Assert.Equal("red", screen.GetLine(0).GetText(0, screen.Columns, trimEnd: true));
    }
}
