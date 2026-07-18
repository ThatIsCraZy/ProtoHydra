namespace DataService.Core.Authentication;

public sealed record AuthenticationDecision(bool Accepted, string? Username);

