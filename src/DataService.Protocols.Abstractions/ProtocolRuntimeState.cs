namespace DataService.Protocols.Abstractions;

public enum ProtocolRuntimeState
{
    Stopped,
    Starting,
    Running,
    Stopping,
    Faulted,
    Unavailable
}

