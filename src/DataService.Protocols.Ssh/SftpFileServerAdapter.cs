using DataService.Core.Events;
using DataService.Protocols.Abstractions;

namespace DataService.Protocols.Ssh;

public sealed class SftpFileServerAdapter : IProtocolAdapter
{
    private readonly SharedSshServer _sharedServer;

    public SftpFileServerAdapter(SharedSshServer sharedServer)
    {
        _sharedServer = sharedServer;
        Capabilities = new ProtocolCapabilities(
            SupportsDownload: true,
            SupportsUpload: true,
            SupportsListing: true,
            SupportsAuthentication: true,
            UsesEncryption: true);
    }

    public SftpFileServerAdapter(
        ITransferEventBus eventBus,
        string hostKeyDirectory)
        : this(new SharedSshServer(eventBus, hostKeyDirectory))
    {
    }

    public ProtocolKind Protocol => ProtocolKind.Sftp;

    public ProtocolCapabilities Capabilities { get; }

    public ProtocolRuntimeState State => _sharedServer.GetState(Protocol);

    public string? UnavailableReason => null;

    public Task<ProtocolValidationResult> ValidateAsync(
        ProtocolConfiguration configuration,
        CancellationToken cancellationToken)
        => _sharedServer.ValidateAsync(Protocol, configuration, cancellationToken);

    public Task StartAsync(
        ProtocolConfiguration configuration,
        CancellationToken cancellationToken)
        => _sharedServer.StartProtocolAsync(Protocol, configuration, cancellationToken);

    public Task StopAsync(CancellationToken cancellationToken)
        => _sharedServer.StopProtocolAsync(Protocol, cancellationToken);
}
