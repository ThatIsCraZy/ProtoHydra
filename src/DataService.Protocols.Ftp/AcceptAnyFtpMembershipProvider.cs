using System.Security.Claims;
using DataService.Core.Events;
using FubarDev.FtpServer.AccountManagement;

namespace DataService.Protocols.Ftp;

internal sealed class AcceptAnyFtpMembershipProvider : IMembershipProvider, IMembershipProviderAsync
{
    private readonly FtpInstrumentationContext _instrumentation;

    public AcceptAnyFtpMembershipProvider(FtpInstrumentationContext instrumentation)
    {
        _instrumentation = instrumentation;
    }

    public Task<MemberValidationResult> ValidateUserAsync(string username, string password)
    {
        var result = CreateResult(username);
        PublishAuthentication(result.FtpUser?.Identity?.Name ?? username);
        return Task.FromResult(result);
    }

    public Task<MemberValidationResult> ValidateUserAsync(
        string username,
        string password,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var result = CreateResult(username);
        PublishAuthentication(result.FtpUser?.Identity?.Name ?? username);
        return Task.FromResult(result);
    }

    public Task LogOutAsync(ClaimsPrincipal principal, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    private static MemberValidationResult CreateResult(string username)
    {
        var effectiveUsername = string.IsNullOrWhiteSpace(username) ? "anonymous" : username;
        var identity = new ClaimsIdentity(
            new[] { new Claim(ClaimTypes.Name, effectiveUsername) },
            authenticationType: "AcceptAnyFtp");

        return new MemberValidationResult(
            effectiveUsername.Equals("anonymous", StringComparison.OrdinalIgnoreCase)
                ? MemberValidationStatus.Anonymous
                : MemberValidationStatus.AuthenticatedUser,
            new ClaimsPrincipal(identity));
    }

    private void PublishAuthentication(string? username)
        => _instrumentation.EventBus.TryPublish(new TransferEvent(
            DateTimeOffset.UtcNow,
            _instrumentation.Protocol,
            TransferEventKind.AuthenticationAttempt,
            null,
            string.IsNullOrWhiteSpace(username) ? "anonymous" : username,
            "PASS ***",
            null,
            null,
            null,
            null,
            TransferResult.Success,
            "AcceptAny",
            Guid.NewGuid().ToString("N")));
}
