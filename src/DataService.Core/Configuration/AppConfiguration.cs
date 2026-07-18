namespace DataService.Core.Configuration;

public sealed record AppConfiguration
{
    public int SchemaVersion { get; init; } = 1;

    public string RootPath { get; init; } = "";

    public LogSettings LogSettings { get; init; } = new();

    public ProtocolSettings Protocols { get; init; } = ProtocolSettings.CreateDefaults();

    public CertificatePathSettings CertificateSettings { get; init; } = new();

    public SshHostKeySettings SshHostKeySettings { get; init; } = new();
}

