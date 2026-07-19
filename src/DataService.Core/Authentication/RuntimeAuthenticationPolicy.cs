namespace DataService.Core.Authentication;

/// <summary>
/// Singleton facade handed to all protocol adapters; the active policy can be
/// swapped at runtime from the configuration UI without restarting listeners.
/// </summary>
public sealed class RuntimeAuthenticationPolicy : IAuthenticationPolicy
{
    private volatile IAuthenticationPolicy _current;

    public RuntimeAuthenticationPolicy(IAuthenticationPolicy initial)
    {
        _current = initial;
    }

    public event EventHandler? PolicyChanged;

    public bool RequiresCredentials => _current.RequiresCredentials;

    public void Replace(IAuthenticationPolicy policy)
    {
        _current = policy;
        PolicyChanged?.Invoke(this, EventArgs.Empty);
    }

    public AuthenticationDecision AuthenticatePassword(string? username, string? password)
        => _current.AuthenticatePassword(username, password);

    public AuthenticationDecision AuthenticatePublicKey(string? username, string? publicKeyFingerprint)
        => _current.AuthenticatePublicKey(username, publicKeyFingerprint);
}
