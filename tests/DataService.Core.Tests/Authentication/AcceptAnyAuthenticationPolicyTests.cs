using DataService.Core.Authentication;

namespace DataService.Core.Tests.Authentication;

public sealed class AcceptAnyAuthenticationPolicyTests
{
    [Theory]
    [InlineData("alice", "secret")]
    [InlineData("anonymous", "")]
    [InlineData(null, null)]
    public void AuthenticatePassword_AcceptsAnyPassword(string? username, string? password)
    {
        var decision = new AcceptAnyAuthenticationPolicy().AuthenticatePassword(username, password);

        Assert.True(decision.Accepted);
        Assert.Equal(username, decision.Username);
    }
}

