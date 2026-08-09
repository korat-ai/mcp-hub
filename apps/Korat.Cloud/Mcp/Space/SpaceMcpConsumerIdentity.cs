using System.Security.Cryptography;
using System.Text;
using Korat.Domain;
using Korat.Domain.Auth;

namespace Korat.Cloud.Mcp.Space;

/// <summary>
/// Durable consumer identity derivation for the Space-MCP aggregator (2026-07-10 increment 1,
/// Task 3, BLOCKER-2 / Global Constraint "Durable consumer identity").
///
/// <see cref="Derive"/> is deterministic from <c>(cliTokenId, spaceId)</c> — the CliToken row's
/// own DB primary key (never client-exposed; the client only ever presents the raw bearer
/// secret, which is hashed separately for token lookup — see
/// <c>Korat.Cloud.Web.Auth.Services.ICliTokenService.GetTokenIdAsync</c>, consumed inside
/// <c>Korat.Cloud.Web.Mcp.Space.SpaceMcpAuth</c>'s CLI branch since inc-2a (SF-4)) and the
/// target Space. The SAME
/// <see cref="ConsumerId"/> is produced on every call for a given pair, so:
///   • grants survive client reconnects/restarts (the aggregator presents the same identity to
///     <c>ISessionAdmission.AdmitAsync</c> every time it opens a backend session for this token);
///   • two Spaces owned by the same token never collide on one identity/grant set (spaceId is
///     part of the hash input);
///   • a leaked/guessed <paramref name="cliTokenId"/> alone does not let an attacker forge
///     ANOTHER token's identity without also knowing that token's own DB PK.
///
/// The result is always <c>cagg_</c>-prefixed and 31 characters total — deliberately disjoint
/// from the CLI's own <see cref="ConsumerId.New"/> shape (32 lowercase-hex characters, NO
/// prefix, per <c>src/Korat.Domain/Ids.cs:35-39</c>): different length AND an underscore, which
/// is not a valid hex character, so the two namespaces can never collide by construction.
/// <see cref="Korat.Cloud.Gateways.Admission.SessionAdmission.AdmitAsync"/> additionally rejects
/// outright any <see cref="Korat.Cloud.Gateways.Admission.ConsumerBindPolicy.NodeTofu"/> (gRPC)
/// caller presenting a <c>cagg_</c>-prefixed id (S6/Task 2) — a hostile node can never present
/// this reserved namespace, guarded independently of this type's own disjointness.
///
/// HARDENING NOTE (plan-writer's "consider HMAC" callout, Task 3): this derivation is bare
/// SHA-256, not HMAC-keyed with a server secret — the same shape as the existing
/// <c>RoomId.ForOwner</c> precedent (<c>src/Korat.Domain/Ids.cs:104-106</c>). An HMAC keyed off a
/// dedicated, always-on config secret would add defense-in-depth against a hypothetical
/// <paramref name="cliTokenId"/> leak (e.g. via a logging bug), but no such secret is
/// UNCONDITIONALLY present in every deployment today: the two existing candidates —
/// <c>IKekProvider</c>'s envelope-encryption KEK and the OpenIddict signing key — are BOTH
/// optional/feature-gated (envelope mode is off until a KEK Fly secret is set; OpenIddict is
/// inactive-by-design in this increment). Making a Space-MCP session-open hot path depend on
/// either would turn an optional feature into a hard requirement just to make this endpoint
/// work. Since <paramref name="cliTokenId"/> is already an internal DB primary key the client
/// never sees on the wire, bare SHA-256 meets this increment's bar. Follow-up (not blocking):
/// introduce a small dedicated, always-on config secret for this derivation specifically and
/// switch to HMAC-SHA256(key, input) — the derivation's shape (cagg_-prefixed, 31 chars) need
/// not change, so this is a pure drop-in swap whenever that secret exists.
/// </summary>
public static class SpaceMcpConsumerIdentity
{
    private const string Prefix = "cagg_";
    private const int HexCharsAfterPrefix = 26;

    // Р25: the CLI-token derivation (cliTokenId × SpaceId) lived here and is gone with the
    // korat_cli_ branch it served. It keyed the consumer identity on the token, and a machine has
    // one token — so every agent on that machine derived the SAME cagg_ identity, and per-agent
    // grants were per-machine grants wearing a per-agent label. Only DeriveOAuth remains.

    /// <summary>
    /// Inc-2a (spec §Identity, BLOCKER-2): the OAuth-consumer derivation —
    /// (client_id × ownerUserId × SpaceId). Deterministic and reused across sessions/
    /// reconnects AND across refresh-token rotations (none of the three inputs changes when
    /// tokens rotate), so grants survive everything short of consent revocation. Same cagg_
    /// namespace/length as <see cref="Derive"/> — covered by the same SessionAdmission
    /// reserved-namespace guard — with a DOMAIN-SEPARATED hash prefix ("space-mcp-oauth:")
    /// so the two derivations can never collide. O1 (cross-increment): this intentionally
    /// differs from inc-1's (cliTokenId × SpaceId) identity — inc-1 grants are dev-only and
    /// are orphaned at OAuth cutover (accepted; the console lists dead-identity grants).
    /// </summary>
    public static ConsumerId DeriveOAuth(string clientId, UserId owner, SpaceId spaceId)
    {
        var input = $"space-mcp-oauth:{clientId}:{owner.Value:N}:{spaceId.Value}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        var hex = Convert.ToHexString(hash)[..HexCharsAfterPrefix].ToLowerInvariant();
        return new ConsumerId(Prefix + hex);
    }

    /// <summary>
    /// The synthetic <see cref="ConnectionId"/> the aggregator grain registers its in-process
    /// delivery leg under (Task 3's <c>CallbackServerStreamWriter</c> via
    /// <c>SessionRoutingTable.RegisterAgentStreamAsync</c>) — one per Mcp-Session-Id, so each
    /// grain activation gets its own routing-table slot with no risk of colliding with a real
    /// gRPC bridge's ConnectionId (those are minted as bare GUID "N" strings; this one carries
    /// the same disjoint "cagg-" marker as the ConsumerId namespace above, hyphen not
    /// underscore only because ConnectionId has no reserved-prefix guard to match against —
    /// uniqueness here comes from embedding the session id itself, not from the marker).
    /// </summary>
    public static ConnectionId SyntheticConnectionId(string mcpSessionId) =>
        new("cagg-" + mcpSessionId);
}
