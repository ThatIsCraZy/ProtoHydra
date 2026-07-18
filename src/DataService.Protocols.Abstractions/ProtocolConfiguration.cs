namespace DataService.Protocols.Abstractions;

public sealed record ProtocolConfiguration(
    string BindAddress,
    int Port,
    string RootPath,
    bool Enabled);

