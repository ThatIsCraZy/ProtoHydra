using DataService.Core.Diagnostics;
using DataService.Core.Events;

namespace DataService.Core.Tests.Diagnostics;

public sealed class IoErrorLogTests
{
    private static IoErrorEntry Entry(string message)
        => new(DateTimeOffset.UtcNow, ProtocolKind.Sftp, IoErrorCategory.AccessDenied, "a/b", message);

    [Fact]
    public void Report_KeepsOnlyCapacityNewestEntries()
    {
        var log = new IoErrorLog(capacity: 3);
        for (var i = 1; i <= 5; i++)
        {
            log.Report(Entry($"e{i}"));
        }

        var snapshot = log.Snapshot();
        Assert.Equal(3, log.Count);
        Assert.Equal(5, log.TotalReported);
        Assert.Equal(["e5", "e4", "e3"], snapshot.Select(entry => entry.Message).ToArray());
    }

    [Fact]
    public void Snapshot_ReturnsNewestFirst()
    {
        var log = new IoErrorLog();
        log.Report(Entry("first"));
        log.Report(Entry("second"));

        Assert.Equal(["second", "first"], log.Snapshot().Select(entry => entry.Message).ToArray());
    }

    [Fact]
    public void Clear_ResetsBufferButKeepsTotal()
    {
        var log = new IoErrorLog(capacity: 4);
        log.Report(Entry("x"));
        log.Report(Entry("y"));

        log.Clear();

        Assert.Equal(0, log.Count);
        Assert.Empty(log.Snapshot());
        Assert.Equal(2, log.TotalReported);

        log.Report(Entry("z"));
        Assert.Equal(["z"], log.Snapshot().Select(entry => entry.Message).ToArray());
    }
}
