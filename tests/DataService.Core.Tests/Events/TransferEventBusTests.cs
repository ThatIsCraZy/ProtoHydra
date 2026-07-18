using DataService.Core.Events;

namespace DataService.Core.Tests.Events;

public sealed class TransferEventBusTests
{
    [Fact]
    public async Task TryPublish_MakesEventAvailableToReader()
    {
        var bus = new TransferEventBus(capacity: 1);
        var transferEvent = new TransferEvent(
            DateTimeOffset.UtcNow,
            ProtocolKind.Http,
            TransferEventKind.ListenerStarted,
            null,
            null,
            "GET",
            "image.iso",
            TransferDirection.Download,
            42,
            TimeSpan.FromMilliseconds(5),
            TransferResult.Success,
            null,
            "correlation");

        Assert.True(bus.TryPublish(transferEvent));
        Assert.True(await bus.Reader.WaitToReadAsync());
        Assert.True(bus.Reader.TryRead(out var read));
        Assert.Equal(transferEvent, read);
    }
}

