namespace DataService.Core.Configuration;

public sealed record CertificatePathSettings
{
    public string DirectoryPath { get; init; } = "";
}

