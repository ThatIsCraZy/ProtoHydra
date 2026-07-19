namespace DataService.Core.Authentication;

public sealed record AuthenticationSettings(AuthenticationMode Mode, IReadOnlyList<UserAccount> Users)
{
    public static AuthenticationSettings Default { get; } = new(AuthenticationMode.AcceptAny, []);
}
