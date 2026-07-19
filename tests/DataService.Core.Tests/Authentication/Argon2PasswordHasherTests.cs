using DataService.Core.Authentication;

namespace DataService.Core.Tests.Authentication;

public sealed class Argon2PasswordHasherTests
{
    [Fact]
    public void Hash_ProducesPhcFormat_AndVerifyRoundTrips()
    {
        var hash = Argon2PasswordHasher.Hash("correct horse battery staple");

        Assert.StartsWith("$argon2id$v=19$m=", hash);
        Assert.True(Argon2PasswordHasher.Verify("correct horse battery staple", hash));
    }

    [Fact]
    public void Verify_RejectsWrongPassword()
    {
        var hash = Argon2PasswordHasher.Hash("secret");

        Assert.False(Argon2PasswordHasher.Verify("Secret", hash));
        Assert.False(Argon2PasswordHasher.Verify("", hash));
    }

    [Fact]
    public void Hash_UsesUniqueSaltPerCall()
    {
        Assert.NotEqual(Argon2PasswordHasher.Hash("same"), Argon2PasswordHasher.Hash("same"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("plaintext")]
    [InlineData("$argon2id$v=19$m=65536,t=3,p=1$notbase64!!$Zm9v")]
    [InlineData("$argon2i$v=19$m=65536,t=3,p=1$c2FsdHNhbHRzYWx0c2FsdA$Zm9vYmFyZm9vYmFyZm9vYmFyZm9vYmFyZm9vYmFyZm8")]
    [InlineData("$argon2id$v=19$m=0,t=3,p=1$c2FsdHNhbHRzYWx0c2FsdA$Zm9v")]
    public void Verify_RejectsMalformedOrForeignHashes(string encoded)
    {
        Assert.False(Argon2PasswordHasher.Verify("secret", encoded));
    }
}
