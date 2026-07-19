using DataService.Core.Authentication;

namespace DataService.Core.Tests.Authentication;

public sealed class DefinedUsersAuthenticationPolicyTests
{
    private static DefinedUsersAuthenticationPolicy CreatePolicy()
        => new([new UserAccount("Alice", Argon2PasswordHasher.Hash("s3cret"))]);

    [Fact]
    public void AuthenticatePassword_AcceptsCorrectCredentials()
    {
        var decision = CreatePolicy().AuthenticatePassword("Alice", "s3cret");

        Assert.True(decision.Accepted);
        Assert.Equal("Alice", decision.Username);
    }

    [Fact]
    public void AuthenticatePassword_UsernameIsCaseInsensitive_AndCanonicalized()
    {
        var decision = CreatePolicy().AuthenticatePassword("ALICE", "s3cret");

        Assert.True(decision.Accepted);
        Assert.Equal("Alice", decision.Username);
    }

    [Theory]
    [InlineData("Alice", "wrong")]
    [InlineData("Alice", "")]
    [InlineData("Alice", null)]
    [InlineData("mallory", "s3cret")]
    [InlineData("", "s3cret")]
    [InlineData(null, "s3cret")]
    public void AuthenticatePassword_RejectsInvalidCredentials(string? username, string? password)
    {
        Assert.False(CreatePolicy().AuthenticatePassword(username, password).Accepted);
    }

    [Fact]
    public void AuthenticatePublicKey_IsAlwaysRejected()
    {
        Assert.False(CreatePolicy().AuthenticatePublicKey("Alice", "SHA256:abcdef").Accepted);
    }

    [Fact]
    public void RequiresCredentials_IsTrue()
    {
        Assert.True(CreatePolicy().RequiresCredentials);
    }

    [Fact]
    public void EmptyUserList_RejectsEverything()
    {
        var policy = new DefinedUsersAuthenticationPolicy([]);

        Assert.False(policy.AuthenticatePassword("anyone", "anything").Accepted);
    }
}
