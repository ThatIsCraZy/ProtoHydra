using System.Text;
using DataService.Core.Terminal;

namespace DataService.Core.Tests.Terminal;

/// <summary>
/// What a console device does when it misbehaves: half-written sequences, parameter
/// floods, binary dumps. The parser has to stay usable through all of it, because the
/// operator cannot restart a switch that is mid-flash just to fix their terminal.
/// Modelled on how esctest judges a terminal, by reading the screen back.
/// </summary>
public sealed class TerminalScreenRobustnessTests
{
    private static string Text(TerminalScreen screen, int line)
        => screen.GetLine(line).GetText(0, screen.Columns, trimEnd: true);

    [Fact]
    public void ParameterFloodDoesNotGrowWithoutBound()
    {
        var screen = new TerminalScreen(40, 10);
        var flood = new StringBuilder("\x1b[");
        for (var index = 0; index < 20_000; index++)
        {
            flood.Append("1;");
        }

        flood.Append("31mred");

        screen.Write(flood.ToString());

        // The sequence is still dispatched, just with the parameters that fit.
        Assert.Equal("red", Text(screen, 0));
        Assert.Equal(0, screen.CursorRow);
    }

    [Fact]
    public void CancelAbortsAHalfWrittenSequence()
    {
        var screen = new TerminalScreen(40, 10);

        screen.Write("\x1b[12\x18X");

        Assert.Equal("X", Text(screen, 0));
        Assert.Equal(TerminalColor.Default, screen.GetLine(0)[0].Style.Foreground);
    }

    [Fact]
    public void SubstituteAbortsAHalfWrittenSequence()
    {
        var screen = new TerminalScreen(40, 10);

        screen.Write("\x1b[1;2\x1aY");

        Assert.Equal("Y", Text(screen, 0));
    }

    [Fact]
    public void CancelAlsoAbortsAnUnterminatedOperatingSystemCommand()
    {
        var screen = new TerminalScreen(40, 10);
        var titles = new List<string>();
        screen.TitleChanged += (_, title) => titles.Add(title);

        screen.Write("\x1b]0;half-written\x18visible");

        Assert.Empty(titles);
        Assert.Equal("visible", Text(screen, 0));
    }

    [Fact]
    public void DeleteInsideASequenceIsIgnoredRatherThanEndingIt()
    {
        var screen = new TerminalScreen(40, 10);

        screen.Write("\x1b[3\u007f1mred");

        Assert.Equal(1, screen.GetLine(0)[0].Style.Foreground);
        Assert.Equal("red", Text(screen, 0));
    }

    [Fact]
    public void EscapeInsideASequenceStartsTheNextOne()
    {
        var screen = new TerminalScreen(40, 10);

        screen.Write("\x1b[12\x1b[31mred");

        Assert.Equal(1, screen.GetLine(0)[0].Style.Foreground);
        Assert.Equal("red", Text(screen, 0));
    }

    [Fact]
    public void OutOfRangeCursorAddressLandsInsideTheGrid()
    {
        var screen = new TerminalScreen(40, 10);

        screen.Write("\x1b[999999999;999999999HX");

        Assert.Equal(9, screen.CursorRow);
        Assert.Equal(39, screen.CursorColumn);
        Assert.Equal("X", Text(screen, 9).TrimStart());
    }

    [Fact]
    public void OmittedParametersFallBackToTheirDefaults()
    {
        var screen = new TerminalScreen(40, 10);

        screen.Write("\x1b[;5HX");

        Assert.Equal(0, screen.CursorRow);
        Assert.Equal("    X", Text(screen, 0));
    }

    [Fact]
    public void UnknownFinalBytesAreSwallowedWithoutSideEffects()
    {
        var screen = new TerminalScreen(40, 10);

        screen.Write("before\x1b[1;2;3\x7ekeep");

        Assert.Equal("beforekeep", Text(screen, 0));
    }

    [Fact]
    public void AstralCharacterTakesOneCell()
    {
        var screen = new TerminalScreen(40, 10);

        screen.Write("a\U0001F600b");

        Assert.Equal('a', screen.GetLine(0)[0].Character);
        Assert.Equal('�', screen.GetLine(0)[1].Character);
        Assert.Equal('b', screen.GetLine(0)[2].Character);
        Assert.Equal(3, screen.CursorColumn);
    }

    [Fact]
    public void DeviceCarriesOnAfterAnUnterminatedDeviceControlString()
    {
        var screen = new TerminalScreen(40, 10);

        screen.Write("\x1bPsomething\x1b\\after");

        Assert.Equal("after", Text(screen, 0));
    }

    [Fact]
    public void BinaryDumpKeepsEveryInvariant()
    {
        var screen = new TerminalScreen(80, 24, scrollbackLines: 200);
        var random = new Random(20260913);
        var buffer = new char[4_096];

        for (var round = 0; round < 32; round++)
        {
            for (var index = 0; index < buffer.Length; index++)
            {
                buffer[index] = (char)random.Next(0, 256);
            }

            screen.Write(buffer);
            AssertInvariants(screen);
        }
    }

    [Fact]
    public void SequenceShapedNoiseKeepsEveryInvariant()
    {
        var screen = new TerminalScreen(80, 24, scrollbackLines: 200);
        var random = new Random(4711);
        string[] fragments =
        [
            "\x1b[", "\x1b]", "\x1bP", "\x1b", ";", ":", "?", "0", "1", "9", "99999",
            "m", "H", "J", "K", "r", "h", "l", "n", "c", "@", "P", "X", "L", "M",
            "\r", "\n", "\b", "\t", "\a", "\x18", "\x1a", "\x7f", "\x1b\\",
            "text", "\x1b[38;5;", "\x1b[?1049", "\x1b[1;1", "\U0001F600"
        ];

        var builder = new StringBuilder();
        for (var round = 0; round < 64; round++)
        {
            builder.Clear();
            for (var index = 0; index < 500; index++)
            {
                builder.Append(fragments[random.Next(fragments.Length)]);
            }

            screen.Write(builder.ToString());
            AssertInvariants(screen);
        }
    }

    [Fact]
    public void ResetRecoversATerminalLeftInAnyState()
    {
        var screen = new TerminalScreen(40, 10);
        screen.Write("\x1b[?1049h\x1b[?25l\x1b[?1h\x1b[3;8r\x1b[?7l\x1bPstuck");

        screen.Reset();
        screen.Write("back");

        Assert.False(screen.IsAlternateScreen);
        Assert.True(screen.CursorVisible);
        Assert.False(screen.ApplicationCursorKeys);
        Assert.Equal("back", Text(screen, 0));
    }

    private static void AssertInvariants(TerminalScreen screen)
    {
        Assert.InRange(screen.CursorRow, 0, screen.Rows - 1);
        Assert.InRange(screen.CursorColumn, 0, screen.Columns - 1);
        Assert.InRange(screen.ScrollbackCount, 0, 200);
        Assert.Equal(screen.ScrollbackCount + screen.Rows, screen.TotalLines);

        for (var index = 0; index < screen.TotalLines; index++)
        {
            var line = screen.GetLine(index);
            Assert.Equal(screen.Columns, line.Length);
            foreach (var cell in line.Cells)
            {
                Assert.False(char.IsControl(cell.Character), "a control character reached the grid");
                Assert.False(char.IsSurrogate(cell.Character), "half a surrogate pair reached the grid");
            }
        }
    }
}
