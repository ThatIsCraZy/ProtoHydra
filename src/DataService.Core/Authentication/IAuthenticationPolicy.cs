namespace DataService.Core.Authentication;

public interface IAuthenticationPolicy
{
    /// <summary>False for Accept-Any; protocols may then skip credential prompts (HTTP Basic).</summary>
    bool RequiresCredentials { get; }

    AuthenticationDecision AuthenticatePassword(string? username, string? password);

    AuthenticationDecision AuthenticatePublicKey(string? username, string? publicKeyFingerprint);
}

