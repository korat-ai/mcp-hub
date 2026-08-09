using Grpc.Core;
using Korat.Cloud.Web.Auth.Services;

namespace Korat.Cloud.Gateways;

/// <summary>
/// Outcome of a Bearer header resolution attempt in <see cref="GrpcAuthHelper.TryResolveBearerAsync"/>.
/// </summary>
public enum BearerOutcome
{
    /// <summary>No <c>Authorization</c> header present (or header uses a non-Bearer scheme).
    /// Caller may fall through to the legacy owner-token path.</summary>
    Absent,

    /// <summary>A Bearer header was present but the token is invalid, expired, or revoked.
    /// Caller MUST reject the request — do NOT fall through to the owner-token path.</summary>
    Invalid,

    /// <summary>Bearer token is valid. <see cref="GrpcBearerResult.UserId"/> carries the resolved user id.</summary>
    Valid,
}

/// <summary>
/// Result of <see cref="GrpcAuthHelper.TryResolveBearerAsync"/>.
/// </summary>
public readonly record struct GrpcBearerResult(BearerOutcome Outcome, Guid? UserId);

/// <summary>
/// Pure helper for resolving call-level authentication from gRPC call metadata.
///
/// Extracted from <c>NodeGatewayService</c> so the Bearer-vs-owner resolution logic
/// can be unit-tested without spinning up a gRPC server.
///
/// Precedence:
/// <list type="bullet">
///   <item><description><see cref="BearerOutcome.Valid"/> — token resolved; caller uses the UserId.</description></item>
///   <item><description><see cref="BearerOutcome.Invalid"/> — header present but token bad/expired/revoked;
///   caller MUST reject (fail closed — do NOT fall through to owner-token path).</description></item>
///   <item><description><see cref="BearerOutcome.Absent"/> — no Bearer header; caller falls through to
///   the legacy owner-token HMAC path (unchanged for migration-window compatibility).</description></item>
/// </list>
/// </summary>
public static class GrpcAuthHelper
{
    private const string BearerPrefix = "Bearer ";

    /// <summary>
    /// Inspects <paramref name="metadata"/> for an <c>authorization</c> entry of the
    /// form <c>Bearer &lt;cli-token&gt;</c>. Returns a tri-state result:
    /// <see cref="BearerOutcome.Valid"/> (token valid, UserId set),
    /// <see cref="BearerOutcome.Invalid"/> (header present but token rejected — fail closed),
    /// or <see cref="BearerOutcome.Absent"/> (no Bearer header — fall through to owner path).
    /// </summary>
    public static async Task<GrpcBearerResult> TryResolveBearerAsync(
        Metadata metadata,
        ICliTokenService cliTokens,
        ISsoTokenValidator ssoTokens,
        ISsoIdentityResolver ssoIdentities,
        CancellationToken ct,
        ILogger? logger = null)
    {
        // gRPC metadata keys are case-insensitive; Grpc.Core normalises to lower-case.
        var entry = metadata.FirstOrDefault(
            e => string.Equals(e.Key, "authorization", StringComparison.OrdinalIgnoreCase));

        if (entry is null) return new GrpcBearerResult(BearerOutcome.Absent, null);

        var value = entry.Value;
        if (!value.StartsWith(BearerPrefix, StringComparison.OrdinalIgnoreCase))
            return new GrpcBearerResult(BearerOutcome.Absent, null);

        var token = value[BearerPrefix.Length..].Trim();
        // An empty token string after "Bearer " is treated as absent, not invalid, because
        // some libraries emit a bare "Bearer" with no token — treat it like a missing header.
        if (string.IsNullOrEmpty(token)) return new GrpcBearerResult(BearerOutcome.Absent, null);

        // N-f (adversarial review, Space-MCP increment 1): use ValidateWithScopeAsync (not the
        // scope-less ValidateAsync) so the token's scope can be checked here, at node Hello.
        var validated = await cliTokens.ValidateWithScopeAsync(token, ct);
        if (validated is null)
            return await TryResolveSsoAsync(token, ssoTokens, ssoIdentities, logger, ct);

        // N-f (adversarial review, second pass): ALLOWLIST, not a denylist. Before this, only the
        // "space-mcp:*" prefix was rejected — so ANY OTHER scope, including one that doesn't
        // exist yet, was accepted as a full relay-node Hello credential by default. A node Hello
        // credential must be exactly "full" or "bridge-only" — the two scopes that actually
        // represent a real relay-node/agent-bridge identity today. Flipping to an allowlist means
        // a future restricted scope (whatever it's named) is node-invalid BY DEFAULT, without
        // relying on someone remembering to add it to a denylist here. Fail closed (Invalid) for
        // anything else — the SAME path as an invalid/expired/revoked token — never falling
        // through to the legacy owner-token path.
        if (validated.Value.Scope is "full" or "bridge-only")
            return new GrpcBearerResult(BearerOutcome.Valid, validated.Value.UserId);

        return new GrpcBearerResult(BearerOutcome.Invalid, null);
    }

    /// <summary>
    /// The same question for a token from the Korat sign-in provider.
    ///
    /// This port needed its own branch, and missing it would not have been subtle-but-harmless:
    /// the REST surface already accepts these tokens, so `korat login` would succeed and then
    /// `korat up`, `korat service run` and the bridge would all be turned away at Hello. Failing
    /// closed made that invisible here — the node simply looks unauthorised.
    ///
    /// No scope check to match the CLI token's "full" / "bridge-only": that axis never had a
    /// second value in production — the only place that ever issued a credential passed "full".
    /// What replaces it is the client the token was issued to, which the validator checks
    /// against a configured list before this code ever sees the token.
    /// </summary>
    private static async Task<GrpcBearerResult> TryResolveSsoAsync(
        string token,
        ISsoTokenValidator ssoTokens,
        ISsoIdentityResolver ssoIdentities,
        ILogger? logger,
        CancellationToken ct)
    {
        var principal = await ssoTokens.ValidateAsync(token, ct);
        if (principal is null) return new GrpcBearerResult(BearerOutcome.Invalid, null);

        var userId = await ssoIdentities.FindAsync(principal.Subject, ct);
        if (userId is not null) return new GrpcBearerResult(BearerOutcome.Valid, userId.Value.Value);

        // Said out loud on purpose. This path fails closed, so an unlinked subject looks
        // exactly like a bad token from the outside — and the fix is entirely different:
        // sign in through the browser once. The REST branch already says this; a node
        // operator deserves the same sentence rather than a silent refusal.
        logger?.LogInformation(
            "SSO token accepted at node Hello but subject {Subject} is not linked to any account here",
            principal.Subject);

        return new GrpcBearerResult(BearerOutcome.Invalid, null);
    }
}
