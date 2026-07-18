namespace DataService.Core.Configuration;

public sealed record LogSettings
{
    public int RingBufferCapacity { get; init; } = 10_000;

    public string DirectoryPath { get; init; } = "";
}

