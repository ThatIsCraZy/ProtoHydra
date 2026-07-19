namespace DataService.Core.Authentication;

public sealed class AcceptAnyAuthenticationPolicy : IAuthenticationPolicy
{
    public bool RequiresCredentials => false;

    public AuthenticationDecision AuthenticatePassword(string? username, string? password)
        => new(true, username);

    public AuthenticationDecision AuthenticatePublicKey(string? username, string? publicKeyFingerprint)
        => new(!string.IsNullOrWhiteSpace(publicKeyFingerprint), username);
}

