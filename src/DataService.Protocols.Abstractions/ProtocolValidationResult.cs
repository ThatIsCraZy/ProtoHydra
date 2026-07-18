namespace DataService.Protocols.Abstractions;

public sealed record ProtocolValidationResult(bool IsValid, string? Message)
{
    public static ProtocolValidationResult Success { get; } = new(true, null);

    public static ProtocolValidationResult Failure(string message) => new(false, message);
}

