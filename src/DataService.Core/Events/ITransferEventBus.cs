using System.Threading.Channels;

namespace DataService.Core.Events;

public interface ITransferEventBus
{
    ChannelReader<TransferEvent> Reader { get; }

    bool TryPublish(TransferEvent transferEvent);
}

