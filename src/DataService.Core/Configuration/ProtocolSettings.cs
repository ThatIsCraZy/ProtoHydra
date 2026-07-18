namespace DataService.Core.Configuration;

public sealed record ProtocolSettings
{
    public ProtocolEndpointSettings Ftp { get; init; } = new() { Port = 21 };

    public ProtocolEndpointSettings Ftps { get; init; } = new() { Port = 990 };

    public ProtocolEndpointSettings Tftp { get; init; } = new() { Port = 69 };

    public ProtocolEndpointSettings Sftp { get; init; } = new() { Port = 22 };

    public ProtocolEndpointSettings Scp { get; init; } = new() { Port = 22 };

    public ProtocolEndpointSettings Http { get; init; } = new() { Port = 80 };

    public ProtocolEndpointSettings Https { get; init; } = new() { Port = 443 };

    public string FtpPassivePortRange { get; init; } = "50000-50100";

    public static ProtocolSettings CreateDefaults() => new();
}
