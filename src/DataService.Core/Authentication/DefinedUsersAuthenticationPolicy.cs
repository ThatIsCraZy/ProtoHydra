namespace DataService.Core.Authentication;

/// <summary>
/// Validates credentials against the configured user list (Argon2id hashes).
/// Usernames match case-insensitively; public-key authentication is rejected so
/// clients fall back to password authentication.
/// </summary>
public sealed class DefinedUsersAuthenticationPolicy : IAuthenticationPolicy
{
    private readonly Dictionary<string, UserAccount> _users;

    public DefinedUsersAuthenticationPolicy(IEnumerable<UserAccount> users)
    {
        _users = new Dictionary<string, UserAccount>(StringComparer.OrdinalIgnoreCase);
        foreach (var user in users)
        {
            if (!string.IsNullOrWhiteSpace(user.Username))
            {
                _users[user.Username] = user;
            }
        }
    }

    public bool RequiresCredentials => true;

    public AuthenticationDecision AuthenticatePassword(string? username, string? password)
    {
        if (string.IsNullOrWhiteSpace(username)
            || !_users.TryGetValue(username, out var user)
            || !Argon2PasswordHasher.Verify(password ?? string.Empty, user.PasswordHash))
        {
            return new AuthenticationDecision(false, username);
        }

        return new AuthenticationDecision(true, user.Username);
    }

    public AuthenticationDecision AuthenticatePublicKey(string? username, string? publicKeyFingerprint)
        => new(false, username);
}
