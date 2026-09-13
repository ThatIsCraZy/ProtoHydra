using DataService.Infrastructure.Serial;

namespace DataService.Core.Tests.Serial;

public sealed class SerialConsoleSettingsStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"protohydra-serial-tests-{Guid.NewGuid():N}");

    private string FilePath => Path.Combine(_directory, "serial-console.json");

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [Fact]
    public void Load_WithoutFile_ReturnsDefaults()
    {
        var preferences = new SerialConsoleSettingsStore(FilePath).Load();

        Assert.Equal(115_200, preferences.Connection.BaudRate);
        Assert.Equal(8, preferences.Connection.DataBits);
        Assert.Equal(SerialParity.None, preferences.Connection.Parity);
        Assert.Equal(SerialNewLineMode.Cr, preferences.NewLineMode);
    }

    [Fact]
    public void SaveAndLoad_RoundTripsEveryParameter()
    {
        var store = new SerialConsoleSettingsStore(FilePath);
        var connection = new SerialConnectionSettings(
            "COM7",
            9_600,
            7,
            SerialParity.Even,
            SerialStopBits.Two,
            SerialFlowControl.RtsCts);
        store.Save(new SerialConsolePreferences(connection, SerialNewLineMode.CrLf, LocalEcho: true, FontSize: 16));

        var loaded = store.Load();

        Assert.Equal(connection, loaded.Connection);
        Assert.Equal(SerialNewLineMode.CrLf, loaded.NewLineMode);
        Assert.True(loaded.LocalEcho);
        Assert.Equal(16, loaded.FontSize);
    }

    [Fact]
    public void Load_WithDamagedFile_FallsBackToDefaults()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(FilePath, "{ this is not json");

        var preferences = new SerialConsoleSettingsStore(FilePath).Load();

        Assert.Equal(SerialConsolePreferences.Default, preferences);
    }

    [Fact]
    public void Load_WithOutOfRangeValues_ClampsThem()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(
            FilePath,
            """{ "SchemaVersion": 1, "PortName": "COM1", "BaudRate": 0, "DataBits": 42, "FontSize": 900 }""");

        var preferences = new SerialConsoleSettingsStore(FilePath).Load();

        Assert.Equal(115_200, preferences.Connection.BaudRate);
        Assert.Equal(8, preferences.Connection.DataBits);
        Assert.Equal(28, preferences.FontSize);
    }

    [Theory]
    [InlineData(115_200, 8, SerialParity.None, SerialStopBits.One, "COM3 · 115200 8N1")]
    [InlineData(9_600, 7, SerialParity.Even, SerialStopBits.Two, "COM3 · 9600 7E2")]
    [InlineData(1_200, 7, SerialParity.Odd, SerialStopBits.OnePointFive, "COM3 · 1200 7O1.5")]
    public void Summary_ReadsLikeAConsoleLabel(
        int baudRate,
        int dataBits,
        SerialParity parity,
        SerialStopBits stopBits,
        string expected)
    {
        var settings = new SerialConnectionSettings("COM3", baudRate, dataBits, parity, stopBits);

        Assert.Equal(expected, settings.Summary);
    }
}
