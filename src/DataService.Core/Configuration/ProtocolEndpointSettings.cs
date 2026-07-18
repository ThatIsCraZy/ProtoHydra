namespace DataService.Core.Configuration;

public sealed record ProtocolEndpointSettings
{
    public bool Enabled { get; init; }

    public string BindAddress { get; init; } = "0.0.0.0";

    public int Port { get; init; }
}

