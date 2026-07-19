using System.Security.Claims;
using DataService.Core.Authentication;
using DataService.Core.Events;
using FubarDev.FtpServer.AccountManagement;

namespace DataService.Protocols.Ftp;

internal sealed class PolicyFtpMembershipProvider : IMembershipProvider, IMembershipProviderAsync
{
    private readonly FtpInstrumentationContext _instrumentation;
    private readonly IAuthenticationPolicy _authenticationPolicy;

    public PolicyFtpMembershipProvider(
        FtpInstrumentationContext instrumentation,
        IAuthenticationPolicy authenticationPolicy)
    {
        _instrumentation = instrumentation;
        _authenticationPolicy = authenticationPolicy;
    }

    public Task<MemberValidationResult> ValidateUserAsync(string username, string password)
        => Task.FromResult(Validate(username, password));

    public Task<MemberValidationResult> ValidateUserAsync(
        string username,
        string password,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Validate(username, password));
    }

    public Task LogOutAsync(ClaimsPrincipal principal, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    private MemberValidationResult Validate(string username, string password)
    {
        var decision = _authenticationPolicy.AuthenticatePassword(username, password);
        if (!decision.Accepted)
        {
            PublishAuthentication(username, TransferResult.Rejected, "Invalid username or password.");
            return new MemberValidationResult(MemberValidationStatus.InvalidLogin);
        }

        var result = CreateResult(decision.Username ?? username);
        PublishAuthentication(
            result.FtpUser?.Identity?.Name ?? username,
            TransferResult.Success,
            _authenticationPolicy.RequiresCredentials ? "DefinedUsers" : "AcceptAny");
        return result;
    }

    private static MemberValidationResult CreateResult(string username)
    {
        var effectiveUsername = string.IsNullOrWhiteSpace(username) ? "anonymous" : username;
        var identity = new ClaimsIdentity(
            new[] { new Claim(ClaimTypes.Name, effectiveUsername) },
            authenticationType: "ProtoHydraFtp");

        return new MemberValidationResult(
            effectiveUsername.Equals("anonymous", StringComparison.OrdinalIgnoreCase)
                ? MemberValidationStatus.Anonymous
                : MemberValidationStatus.AuthenticatedUser,
            new ClaimsPrincipal(identity));
    }

    private void PublishAuthentication(string? username, TransferResult result, string detail)
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
            result,
            detail,
            Guid.NewGuid().ToString("N")));
}
