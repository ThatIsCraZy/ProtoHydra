namespace DataService.Core.Authentication;

/// <summary>A defined user. Only the Argon2id hash is ever persisted, never the password.</summary>
public sealed record UserAccount(string Username, string PasswordHash);
