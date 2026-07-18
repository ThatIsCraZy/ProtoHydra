using System.Globalization;
using System.Text;
using DataService.Core.Events;

namespace DataService.Core.Diagnostics;

/// <summary>
/// Plaintext capture of all transfer protocol activity for diagnosing handshake
/// problems and missing commands. Every line starts with its severity (INFO/ERROR)
/// so the file can be scanned for failures with a simple text search.
/// </summary>
public sealed class TransferCaptureSession : IDisposable
{
    private readonly object _lock = new();
    private readonly StreamWriter _writer;
    private bool _disposed;

    private TransferCaptureSession(string filePath, StreamWriter writer)
    {
        FilePath = filePath;
        _writer = writer;
    }

    public string FilePath { get; }

    public static TransferCaptureSession Start(string directory, IEnumerable<string> headerLines)
    {
        var filePath = Path.Combine(
            directory,
            $"transfer-capture-{DateTimeOffset.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)}.log");
        var stream = new FileStream(filePath, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
        {
            AutoFlush = true
        };

        var session = new TransferCaptureSession(filePath, writer);
        session.RecordInfo("Transfer capture started.");
        foreach (var line in headerLines)
        {
            session.RecordInfo(line);
        }

        session.RecordInfo(
            "Line format: severity | timestamp | protocol | event | source | user | command | path | direction | bytes | duration | result | message | ioerror");
        return session;
    }

    public void Record(TransferEvent transferEvent)
        => WriteLine(GetSeverity(transferEvent), FormatEvent(transferEvent));

    public void RecordInfo(string message)
        => WriteLine("INFO ", Escape(message) ?? "");

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            try
            {
                _writer.WriteLine($"INFO  {FormatTimestamp(DateTimeOffset.Now)} | Transfer capture stopped.");
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
            }

            _disposed = true;
            _writer.Dispose();
        }
    }

    private void WriteLine(string severity, string content)
    {
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            try
            {
                _writer.WriteLine($"{severity} {FormatTimestamp(DateTimeOffset.Now)} | {content}");
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
            }
        }
    }

    private static string GetSeverity(TransferEvent transferEvent)
        => transferEvent.Result is TransferResult.Failed or TransferResult.Rejected
            || transferEvent.EventKind is TransferEventKind.ListenerFaulted
                or TransferEventKind.DownloadFailed
                or TransferEventKind.UploadFailed
                or TransferEventKind.RequestRejected
            ? "ERROR"
            : "INFO ";

    private static string FormatEvent(TransferEvent transferEvent)
        => string.Join(
            " | ",
            transferEvent.Protocol.ToString().ToUpperInvariant(),
            transferEvent.EventKind.ToString(),
            $"source={transferEvent.SourceAddress?.ToString() ?? "-"}",
            $"user={Escape(transferEvent.Username) ?? "-"}",
            $"command={QuoteOrDash(transferEvent.Command)}",
            $"path={QuoteOrDash(transferEvent.RelativePath)}",
            $"direction={transferEvent.Direction?.ToString() ?? "-"}",
            $"bytes={transferEvent.ByteCount?.ToString(CultureInfo.InvariantCulture) ?? "-"}",
            $"duration={FormatDuration(transferEvent.Duration)}",
            $"result={transferEvent.Result}",
            $"message={QuoteOrDash(transferEvent.Message)}",
            $"ioerror={transferEvent.IoError?.ToString() ?? "-"}");

    private static string FormatTimestamp(DateTimeOffset timestamp)
        => timestamp.ToString("yyyy-MM-dd HH:mm:ss.fff zzz", CultureInfo.InvariantCulture);

    private static string FormatDuration(TimeSpan? duration)
        => duration is null
            ? "-"
            : duration.Value.TotalSeconds.ToString("0.000", CultureInfo.InvariantCulture) + "s";

    private static string QuoteOrDash(string? value)
        => string.IsNullOrEmpty(value) ? "-" : "\"" + Escape(value) + "\"";

    private static string? Escape(string? value)
        => value?
            .Replace("\r", "\\r", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal);
}
