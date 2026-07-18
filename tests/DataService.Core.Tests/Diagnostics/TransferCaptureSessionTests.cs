using DataService.Core.Diagnostics;
using DataService.Core.Events;

namespace DataService.Core.Tests.Diagnostics;

public sealed class TransferCaptureSessionTests
{
    private static TransferEvent Event(
        TransferEventKind eventKind,
        TransferResult result,
        string? command = "OPEN",
        string? message = null)
        => new(
            DateTimeOffset.UtcNow,
            ProtocolKind.Sftp,
            eventKind,
            null,
            "test-user",
            command,
            "sub/data.txt",
            TransferDirection.Upload,
            1234,
            TimeSpan.FromMilliseconds(1500),
            result,
            message,
            null);

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "hydra-capture-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    [Fact]
    public void Capture_WritesHeaderEventsAndFooter_AsPlaintext()
    {
        var directory = CreateTempDirectory();
        string filePath;
        using (var session = TransferCaptureSession.Start(directory, ["Root folder: X"]))
        {
            filePath = session.FilePath;
            session.Record(Event(TransferEventKind.UploadStarted, TransferResult.Success));
        }

        var lines = File.ReadAllLines(filePath);
        Assert.StartsWith("INFO ", lines[0]);
        Assert.Contains("Transfer capture started.", lines[0]);
        Assert.Contains(lines, line => line.Contains("Root folder: X"));
        Assert.Contains(lines, line => line.Contains("UploadStarted") && line.Contains("command=\"OPEN\"") && line.Contains("bytes=1234"));
        Assert.Contains("Transfer capture stopped.", lines[^1]);
        Assert.All(lines, line => Assert.True(
            line.StartsWith("INFO ") || line.StartsWith("ERROR"),
            $"Line must start with a severity: {line}"));
    }

    [Fact]
    public void Capture_MarksFailuresAndRejections_AsError()
    {
        var directory = CreateTempDirectory();
        string filePath;
        using (var session = TransferCaptureSession.Start(directory, []))
        {
            filePath = session.FilePath;
            session.Record(Event(TransferEventKind.UploadFailed, TransferResult.Failed, message: "disk full"));
            session.Record(Event(TransferEventKind.RequestRejected, TransferResult.Rejected, command: "MKDIR"));
            session.Record(Event(TransferEventKind.DownloadCompleted, TransferResult.Success));
        }

        var lines = File.ReadAllLines(filePath);
        Assert.Contains(lines, line => line.StartsWith("ERROR") && line.Contains("UploadFailed") && line.Contains("disk full"));
        Assert.Contains(lines, line => line.StartsWith("ERROR") && line.Contains("RequestRejected"));
        Assert.Contains(lines, line => line.StartsWith("INFO ") && line.Contains("DownloadCompleted"));
    }

    [Fact]
    public void Capture_EscapesNewlinesInMessages_ToKeepOneEventPerLine()
    {
        var directory = CreateTempDirectory();
        string filePath;
        using (var session = TransferCaptureSession.Start(directory, []))
        {
            filePath = session.FilePath;
            session.Record(Event(TransferEventKind.RequestRejected, TransferResult.Rejected, message: "line1\r\nline2"));
        }

        var lines = File.ReadAllLines(filePath);
        Assert.Contains(lines, line => line.Contains("line1\\r\\nline2"));
    }

    [Fact]
    public void Capture_RecordAfterDispose_IsIgnored()
    {
        var directory = CreateTempDirectory();
        var session = TransferCaptureSession.Start(directory, []);
        session.Dispose();

        session.Record(Event(TransferEventKind.UploadStarted, TransferResult.Success));
        session.Dispose();

        var lines = File.ReadAllLines(session.FilePath);
        Assert.DoesNotContain(lines, line => line.Contains("UploadStarted"));
    }
}
