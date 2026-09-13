using System.Text.Json;
using System.Text.Json.Serialization;
using DataService.Core.Configuration;

namespace DataService.Infrastructure.Serial;

/// <summary>
/// Persists the serial console preferences as JSON under
/// %LOCALAPPDATA%\ProtoHydra\serial-console.json. A damaged or unreadable file falls
/// back to the defaults instead of failing the console tab.
/// </summary>
public sealed class SerialConsoleSettingsStore
{
    private const int CurrentSchemaVersion = 1;

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly string _filePath;

    public SerialConsoleSettingsStore(string? filePath = null)
    {
        _filePath = filePath ?? Path.Combine(ApplicationDataPaths.Root, "serial-console.json");
    }

    public SerialConsolePreferences Load()
    {
        if (!File.Exists(_filePath))
        {
            return SerialConsolePreferences.Default;
        }

        try
        {
            var document = JsonSerializer.Deserialize<PersistedPreferences>(
                File.ReadAllText(_filePath), SerializerOptions);
            if (document is null)
            {
                return SerialConsolePreferences.Default;
            }

            var connection = new SerialConnectionSettings(
                document.PortName ?? string.Empty,
                document.BaudRate <= 0 ? 115_200 : document.BaudRate,
                document.DataBits is < 5 or > 8 ? 8 : document.DataBits,
                document.Parity,
                document.StopBits,
                document.FlowControl,
                document.DataTerminalReady,
                document.RequestToSend);

            return new SerialConsolePreferences(
                connection,
                document.NewLineMode,
                document.LocalEcho,
                Math.Clamp(document.FontSize, 8, 28));
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
            return SerialConsolePreferences.Default;
        }
    }

    public void Save(SerialConsolePreferences preferences)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
            var document = new PersistedPreferences
            {
                SchemaVersion = CurrentSchemaVersion,
                PortName = preferences.Connection.PortName,
                BaudRate = preferences.Connection.BaudRate,
                DataBits = preferences.Connection.DataBits,
                Parity = preferences.Connection.Parity,
                StopBits = preferences.Connection.StopBits,
                FlowControl = preferences.Connection.FlowControl,
                DataTerminalReady = preferences.Connection.DataTerminalReady,
                RequestToSend = preferences.Connection.RequestToSend,
                NewLineMode = preferences.NewLineMode,
                LocalEcho = preferences.LocalEcho,
                FontSize = preferences.FontSize
            };
            File.WriteAllText(_filePath, JsonSerializer.Serialize(document, SerializerOptions));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Remembering the last port is a convenience; losing it must not break the console.
        }
    }

    private sealed class PersistedPreferences
    {
        public int SchemaVersion { get; set; } = CurrentSchemaVersion;

        public string? PortName { get; set; }

        public int BaudRate { get; set; } = 115_200;

        public int DataBits { get; set; } = 8;

        public SerialParity Parity { get; set; } = SerialParity.None;

        public SerialStopBits StopBits { get; set; } = SerialStopBits.One;

        public SerialFlowControl FlowControl { get; set; } = SerialFlowControl.None;

        public bool DataTerminalReady { get; set; } = true;

        public bool RequestToSend { get; set; } = true;

        public SerialNewLineMode NewLineMode { get; set; } = SerialNewLineMode.Cr;

        public bool LocalEcho { get; set; }

        public int FontSize { get; set; } = 13;
    }
}
