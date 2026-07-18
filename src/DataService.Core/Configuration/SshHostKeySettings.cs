namespace DataService.Core.Configuration;

public sealed record SshHostKeySettings
{
    public string DirectoryPath { get; init; } = "";
}

