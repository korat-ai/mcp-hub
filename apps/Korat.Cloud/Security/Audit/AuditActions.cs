namespace Korat.Cloud.Security.Audit;

/// <summary>
/// 032 (#57 Leg 3 C1): the catalogued audit action names. Centralized so the action vocabulary
/// stays greppable and the verify/ops tooling can rely on exact strings.
/// </summary>
public static class AuditActions
{
    // ── Secret / KEK surface (#55 envelope) ───────────────────────────────────
    public const string SecretSet          = "secret.set";
    public const string SecretClear        = "secret.clear";
    /// <summary>Lazy re-encrypt of a legacy DP-format secret into envelope format.</summary>
    public const string SecretMigrate      = "secret.migrate";
    /// <summary>Hot-path read at inference dispatch. Fail-OPEN + alarm (availability wins).</summary>
    public const string SecretDecrypt      = "secret.decrypt";
    public const string DekCreate          = "dek.create";
    public const string DekUnwrapFailure   = "dek.unwrap_failure";
    public const string KekRewrap          = "kek.rewrap";
    public const string DekShred           = "dek.shred";

    // ── Space-owner privileged ops ─────────────────────────────────────────────
    public const string AccessRequestApprove = "access_request.approve";
    public const string AccessRequestDeny    = "access_request.deny";
    public const string GrantRevoke          = "grant.revoke";
    public const string McpServerDisable     = "mcp_server.disable";
    public const string McpServerEnable      = "mcp_server.enable";
    public const string McpServerDelete      = "mcp_server.delete";
    /// <summary>Increment 1 (HTTP MCP direct-to-Space): owner registers a new http_cloud server.</summary>
    public const string McpServerCreate      = "mcp_server.create";
    /// <summary>Increment 1 (HTTP MCP direct-to-Space): owner edits an http_cloud server's config/secret.</summary>
    public const string McpServerPatch       = "mcp_server.patch";
    /// <summary>
    /// Р27: an already-published server was re-published under the same name with a DIFFERENT
    /// launch definition. Distinct from <see cref="McpServerPatch"/> (an owner edit) — this one is
    /// performed by whoever holds the publisher node's credential, and it is the event that
    /// suspends permissions under Р26. Details carry the before/after command pair so the record
    /// answers "what changed", not merely "something did".
    /// </summary>
    public const string McpServerRedefine    = "mcp_server.redefine";
    /// <summary>Increment 2 (HTTP MCP OAuth): the callback endpoint successfully exchanged a code
    /// for tokens and stored them (server transitioned NeedsReauth → Published).</summary>
    public const string McpServerOAuthConnected           = "mcp_server.oauth_connected";
    /// <summary>Increment 2 (HTTP MCP OAuth): owner requested a fresh authorize action via
    /// POST /api/mcp-servers/{id}/reconnect.</summary>
    public const string McpServerOAuthReconnectRequested   = "mcp_server.oauth_reconnect_requested";
    public const string NodeNoteSet          = "node.note_set";
    public const string NodesPrune           = "node.prune";
    /// <summary>PR-5 (design-review HIGH-3): owner-initiated agent delete, including its
    /// bridge-client cascade (grant revoke + pending-request deny + bridge Node removal).</summary>
    public const string AgentDelete          = "agent.delete";
    /// <summary>Space-MCP inc-2a: owner granted OAuth consent (client × Space) at /connect/authorize.</summary>
    public const string OAuthConsentGranted  = "oauth.consent_granted";
    /// <summary>Space-MCP inc-2a (Task 8): owner revoked an OAuth consent from the console —
    /// tokens revoked + live aggregator sessions torn down (SF-6).</summary>
    public const string OAuthConsentRevoked  = "oauth.consent_revoked";
    /// <summary>Space-MCP inc-2b: an MCP client auto-registered via the open RFC 7591
    /// /connect/register endpoint. Anonymous by protocol — actor = system, details carry the
    /// assigned client_id + client_name + client IP for forensics.</summary>
    public const string OAuthClientRegistered = "oauth.client_registered";
    /// <summary>
    /// Р31: a refresh token was presented that the authorization server would not honour —
    /// overwhelmingly a REPLAY of an already-rotated token, which is what a stolen copy looks like
    /// the moment the legitimate client rotates.
    ///
    /// Rejection alone was already correct and already tested; what was missing was the signal.
    /// Preventing the theft is out of reach (see docs/security/threat-model.md, "Not protected"
    /// §1), so noticing it is the best available outcome, and it is only available at this exact
    /// moment — the collision between the thief's copy and the owner's.
    /// </summary>
    public const string OAuthRefreshRejected  = "oauth.refresh_rejected";

    // ── Admin ops ──────────────────────────────────────────────────────────────

    // ── Credential issuance / revocation ──────────────────────────────────────
    public const string CliTokenIssue      = "cli_token.issue";
    public const string CliTokenRevoke     = "cli_token.revoke";
    public const string CliTokenRevokeAll  = "cli_token.revoke_all";
    public const string InferenceKeyIssue  = "inference_key.issue";
    public const string InferenceKeyRevoke = "inference_key.revoke";

    // ── Inference point lifecycle ──────────────────────────────────────────────
    public const string InferencePointCreate  = "inference_point.create";
    public const string InferencePointDisable = "inference_point.disable";
    public const string InferencePointEnable  = "inference_point.enable";
    public const string InferencePointDelete  = "inference_point.delete";

    // ── Developer API (dev/test surface; actor = system, env flag in details) ─
    public const string DevApiAutoApprove = "dev_api.auto_approve";
    public const string DevApiGrantCreate = "dev_api.grant_create";
    public const string DevApiReset       = "dev_api.reset";

    // ── Audit-system internal ──────────────────────────────────────────────────
    public const string AuditAnchor          = "audit.anchor";
    public const string AuditPruneCheckpoint = "audit.prune_checkpoint";
}

/// <summary>Allowed <c>ActorType</c> values.</summary>
public static class AuditActorTypes
{
    public const string User         = "user";
    public const string CliToken     = "cli_token";
    public const string InferenceKey = "inference_key";
    public const string Node         = "node";
    public const string System       = "system";
}

/// <summary>Allowed <c>AuthKind</c> values.</summary>
public static class AuditAuthKinds
{
    public const string Cookie       = "cookie";
    public const string CliBearer    = "cli_bearer";
    public const string InferenceKey = "inference_key";
    public const string Internal     = "internal";
}

/// <summary>Allowed <c>Outcome</c> values.</summary>
public static class AuditOutcomes
{
    public const string Success = "success";
    public const string Failure = "failure";
    public const string Denied  = "denied";
}
