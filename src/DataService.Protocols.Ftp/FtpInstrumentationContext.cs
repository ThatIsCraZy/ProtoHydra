using System.Collections.Concurrent;
using DataService.Core.Events;

namespace DataService.Protocols.Ftp;

internal sealed class FtpInstrumentationContext
{
    private readonly ConcurrentDictionary<string, byte> _completedUploadEvents = new();

    public FtpInstrumentationContext(
        ITransferEventBus eventBus,
        ProtocolKind protocol,
        string rootPath)
    {
        EventBus = eventBus;
        Protocol = protocol;
        RootPath = Path.GetFullPath(rootPath);
    }

    public ITransferEventBus EventBus { get; }

    public ProtocolKind Protocol { get; }

    public string RootPath { get; }

    public bool TryMarkUploadCompleted(string correlationId)
        => _completedUploadEvents.TryAdd(correlationId, 0);
}
