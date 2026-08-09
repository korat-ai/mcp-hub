using Korat.Cloud.Security.Audit;
using OpenIddict.Abstractions;
using OpenIddict.Server;

namespace Korat.Cloud.Web.Oauth;

/// <summary>
/// Р31: refresh-token reuse DETECTION.
///
/// <para>Rotation and rejection already worked — OpenIddict rolls refresh tokens and refuses a
/// redeemed one, and both are covered by tests. What was missing is that the refusal was silent.
/// </para>
///
/// <para>A stolen refresh token produces exactly one observable event in its entire life: the
/// moment the legitimate client rotates and the thief's copy is presented second (or the reverse).
/// Preventing the theft is out of reach — the threat model says so plainly, because the credential
/// sits in a file any process of that OS user can read — so noticing it is the best outcome
/// available, and this is the only instant at which it is available at all.</para>
///
/// <para><b>What this event does NOT prove.</b> <c>invalid_grant</c> on a refresh request also
/// covers a token that simply expired, and one issued before a deployment that rotated signing
/// material. The record therefore means "a refresh token was refused" — worth an operator's
/// attention, not proof of theft. Recording it as anything stronger would produce an audit trail
/// that lies under pressure, which is worse than no record.</para>
///
/// <para>Registered as a class-based handler rather than an inline one because it needs DI: the
/// inline form runs against the OpenIddict transaction, which does not carry a service provider in
/// this version.</para>
/// </summary>
public sealed class RefreshTokenReuseDetector(
    IAuditLog auditLog,
    ILogger<RefreshTokenReuseDetector> logger)
    : IOpenIddictServerHandler<OpenIddictServerEvents.ProcessErrorContext>
{
    public static OpenIddictServerHandlerDescriptor Descriptor { get; } =
        OpenIddictServerHandlerDescriptor
            .CreateBuilder<OpenIddictServerEvents.ProcessErrorContext>()
            .UseScopedHandler<RefreshTokenReuseDetector>()
            .SetType(OpenIddictServerHandlerType.Custom)
            .Build();

    public async ValueTask HandleAsync(OpenIddictServerEvents.ProcessErrorContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var request = context.Transaction.Request;
        // ProcessError only fires when a request was refused, so reaching here on a refresh grant
        // IS the event. There is deliberately no filter on the error CODE: an earlier revision
        // filtered on invalid_grant and never fired even once, because OpenIddict fails refresh
        // validation internally with invalid_token and only maps it to the RFC-mandated
        // invalid_grant when writing the response. A condition that cannot be exercised by a test
        // is a condition that will be wrong without anyone noticing, so it is gone rather than
        // widened — "a refresh token was refused" is exactly what this record claims anyway.
        if (request is null
            || !string.Equals(request.GrantType, OpenIddictConstants.GrantTypes.RefreshToken, StringComparison.Ordinal))
        {
            return;
        }

        var clientId = request.ClientId ?? "(unknown)";

        logger.LogWarning(
            "Refresh token refused for client {ClientId}. A rotated token presented a second time is "
            + "what a stolen copy looks like — review this client's sessions and consents.",
            clientId);

        try
        {
            await auditLog.RecordAsync(new AuditEvent(
                Action: AuditActions.OAuthRefreshRejected,
                TargetType: "oauth_client",
                TargetId: clientId,
                DetailsJson: AuditDetails.Json(new
                {
                    reason = context.ErrorDescription ?? "invalid_grant",
                })),
                // Fail-open on purpose: an audit-sink problem must not turn a correct rejection
                // into a 500. The rejection itself is the security-critical part and has already
                // happened by the time this handler runs.
                required: false,
                CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogError(
                "Failed to audit a refused refresh token clientId={ClientId} errorType={ErrorType}",
                clientId, ex.GetType().Name);
        }
    }
}
