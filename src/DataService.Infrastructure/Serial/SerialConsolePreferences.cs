namespace DataService.Infrastructure.Serial;

/// <summary>What the Enter key puts on the wire.</summary>
public enum SerialNewLineMode
{
    Cr,
    Lf,
    CrLf
}

/// <summary>
/// Connection parameters plus console behaviour, remembered between runs so the next
/// session on the same bench starts on the right port and speed.
/// </summary>
public sealed record SerialConsolePreferences(
    SerialConnectionSettings Connection,
    SerialNewLineMode NewLineMode = SerialNewLineMode.Cr,
    bool LocalEcho = false,
    int FontSize = 13)
{
    public static SerialConsolePreferences Default { get; } = new(new SerialConnectionSettings(string.Empty));
}
