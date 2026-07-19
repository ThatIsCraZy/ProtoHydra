using DataService.Core.Authentication;

namespace DataService.Core.Tests.Authentication;

public sealed class AuthenticationSettingsStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"protohydra-auth-tests-{Guid.NewGuid():N}");

    private string FilePath => Path.Combine(_directory, "authentication.json");

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [Fact]
    public void Load_WithoutFile_ReturnsAcceptAnyDefault()
    {
        var settings = new AuthenticationSettingsStore(FilePath).Load();

        Assert.Equal(AuthenticationMode.AcceptAny, settings.Mode);
        Assert.Empty(settings.Users);
    }

    [Fact]
    public void SaveAndLoad_RoundTripsUsersAndMode()
    {
        var store = new AuthenticationSettingsStore(FilePath);
        var hash = Argon2PasswordHasher.Hash("pw");
        store.Save(new AuthenticationSettings(
            AuthenticationMode.DefinedUsers,
            [new UserAccount("alice", hash), new UserAccount("bob", hash)]));

        var loaded = store.Load();

        Assert.Equal(AuthenticationMode.DefinedUsers, loaded.Mode);
        Assert.Equal(["alice", "bob"], loaded.Users.Select(user => user.Username));
        Assert.All(loaded.Users, user => Assert.Equal(hash, user.PasswordHash));
    }

    [Fact]
    public void SavedFile_ContainsNoPlaintextPassword()
    {
        var store = new AuthenticationSettingsStore(FilePath);
        store.Save(new AuthenticationSettings(
            AuthenticationMode.DefinedUsers,
            [new UserAccount("alice", Argon2PasswordHasher.Hash("hunter2"))]));

        Assert.DoesNotContain("hunter2", File.ReadAllText(FilePath));
    }

    [Fact]
    public void Load_CorruptFile_FailsClosed()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(FilePath, "{ not json");

        var settings = new AuthenticationSettingsStore(FilePath).Load();

        Assert.Equal(AuthenticationMode.DefinedUsers, settings.Mode);
        Assert.Empty(settings.Users);
        Assert.False(AuthenticationSettingsStore.CreatePolicy(settings)
            .AuthenticatePassword("anyone", "anything").Accepted);
    }

    [Fact]
    public void CreatePolicy_MapsModeToPolicyType()
    {
        Assert.IsType<AcceptAnyAuthenticationPolicy>(
            AuthenticationSettingsStore.CreatePolicy(AuthenticationSettings.Default));
        Assert.IsType<DefinedUsersAuthenticationPolicy>(
            AuthenticationSettingsStore.CreatePolicy(new AuthenticationSettings(AuthenticationMode.DefinedUsers, [])));
    }
}
