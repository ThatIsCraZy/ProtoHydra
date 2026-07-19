using System.Text.Json;
using System.Text.Json.Serialization;
using DataService.Core.Configuration;

namespace DataService.Core.Authentication;

/// <summary>
/// Persists the authentication configuration as JSON under
/// %LOCALAPPDATA%\ProtoHydra\authentication.json. Only Argon2id hashes are
/// stored. A present-but-unreadable file fails closed (DefinedUsers with no
/// users) so a corrupted store never silently reopens the server.
/// </summary>
public sealed class AuthenticationSettingsStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly string _filePath;

    public AuthenticationSettingsStore(string? filePath = null)
    {
        _filePath = filePath ?? Path.Combine(ApplicationDataPaths.Root, "authentication.json");
    }

    public AuthenticationSettings Load()
    {
        if (!File.Exists(_filePath))
        {
            return AuthenticationSettings.Default;
        }

        try
        {
            var document = JsonSerializer.Deserialize<PersistedSettings>(
                File.ReadAllText(_filePath), SerializerOptions);
            if (document is null)
            {
                return FailClosed();
            }

            var users = (document.Users ?? [])
                .Where(user => !string.IsNullOrWhiteSpace(user.Username)
                    && !string.IsNullOrWhiteSpace(user.PasswordHash))
                .Select(user => new UserAccount(user.Username!, user.PasswordHash!))
                .ToArray();
            return new AuthenticationSettings(document.Mode, users);
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
            return FailClosed();
        }
    }

    public void Save(AuthenticationSettings settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
        var document = new PersistedSettings
        {
            Mode = settings.Mode,
            Users = settings.Users
                .Select(user => new PersistedUser { Username = user.Username, PasswordHash = user.PasswordHash })
                .ToList()
        };
        File.WriteAllText(_filePath, JsonSerializer.Serialize(document, SerializerOptions));
    }

    public static IAuthenticationPolicy CreatePolicy(AuthenticationSettings settings)
        => settings.Mode == AuthenticationMode.DefinedUsers
            ? new DefinedUsersAuthenticationPolicy(settings.Users)
            : new AcceptAnyAuthenticationPolicy();

    private static AuthenticationSettings FailClosed()
        => new(AuthenticationMode.DefinedUsers, []);

    private sealed class PersistedSettings
    {
        public AuthenticationMode Mode { get; set; }

        public List<PersistedUser>? Users { get; set; }
    }

    private sealed class PersistedUser
    {
        public string? Username { get; set; }

        public string? PasswordHash { get; set; }
    }
}
