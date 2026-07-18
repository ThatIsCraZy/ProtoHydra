using DataService.Core.Events;

namespace DataService.Protocols.Abstractions;

public interface IProtocolAdapter
{
    ProtocolKind Protocol { get; }

    ProtocolCapabilities Capabilities { get; }

    ProtocolRuntimeState State { get; }

    string? UnavailableReason { get; }

    Task<ProtocolValidationResult> ValidateAsync(
        ProtocolConfiguration configuration,
        CancellationToken cancellationToken);

    Task StartAsync(
        ProtocolConfiguration configuration,
        CancellationToken cancellationToken);

    Task StopAsync(CancellationToken cancellationToken);
}

