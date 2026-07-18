using System.Threading.Channels;

namespace DataService.Core.Events;

public sealed class TransferEventBus : ITransferEventBus
{
    private readonly Channel<TransferEvent> _channel;

    public TransferEventBus(int capacity = 10_000)
    {
        if (capacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity), "Capacity must be positive.");
        }

        _channel = Channel.CreateBounded<TransferEvent>(new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = false,
            SingleWriter = false
        });
    }

    public ChannelReader<TransferEvent> Reader => _channel.Reader;

    public bool TryPublish(TransferEvent transferEvent)
        => _channel.Writer.TryWrite(transferEvent);
}

