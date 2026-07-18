namespace DataService.Core.Authentication;

public interface IAuthenticationPolicy
{
    AuthenticationDecision AuthenticatePassword(string? username, string? password);

    AuthenticationDecision AuthenticatePublicKey(string? username, string? publicKeyFingerprint);
}

