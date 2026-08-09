namespace Korat.Domain;

// Статусы и коды ошибок домена.

public enum NodeStatus
{
    Offline,
    Online
}

// 017: role a node plays. Publisher = runs `korat up`/`service`, hosts MCP servers.
// Agent = a `korat connect` consumer identity (one per agent client; many per machine).
// Default is Publisher so pre-017 nodes (all publishers) remain valid.
public enum NodeKind
{
    Publisher,
    Agent
}

public enum McpServerStatus
{
    Published,
    Disabled,
    Unavailable, // reserved for transient-failure flow; not yet produced
    /// <summary>
    /// Increment 1 (HTTP MCP direct-to-Space, Finding 16 S1): reserved for Increment 2's OAuth
    /// token-refresh-failure flow (spec §5/§6/§8) — a token refresh that fails (revoked/expired)
    /// flips an http_cloud server to this status until the owner re-authorizes. NOT produced or
    /// read by any code in Increment 1 (only `none`/`bearer`/`header` auth modes exist this
    /// increment, none of which have a refresh concept). Reserved now so Increment 2 doesn't need
    /// an admission-check migration (NodeGatewayService.cs:1000's `server.Status == Disabled`
    /// check) on top of introducing a brand-new enum value.
    /// </summary>
    NeedsReauth
}

public enum ConsumerStatus
{
    Offline,
    Online
}

public enum AccessRequestStatus
{
    Pending,   // ждёт решения владельца
    Approved,  // одобрен (grant создан отдельно)
    Denied,
    Expired,   // reserved for TTL expiry flow; not yet produced
    Canceled   // reserved for requester self-cancel flow; not yet produced
}

public enum GrantStatus
{
    Active,  // доступ разрешён
    Revoked  // отозван владельцем
}

public enum SessionStatus
{
    Opening,
    Active,
    Closing, // reserved for graceful-teardown flow; not yet produced
    Closed,
    Failed,
    Denied   // reserved for access-denied at open time; not yet produced
}

public enum GatewayStatus
{
    Offline,
    Online
}

public enum SessionCloseReason
{
    Completed,
    Revoked,
    ServerDisabled,
    ServerUnavailable,
    PublisherOffline,
    PayloadLimitExceeded,
    CryptoFailure,
    UserClosed,
    /// <summary>Relay не восстанавливается после рестарта облака.</summary>
    ServiceRestart,
    Error,
    /// <summary>Step-C (session reaper): the session's client or publisher node went offline and
    /// never returned within the grace horizon; the stored Active/Opening status was a ghost and
    /// has been reconciled to Closed. Source-agnostic (PublisherOffline only fits a publisher).</summary>
    Abandoned
}

[GenerateSerializer]
public enum InferencePointStatus
{
    Published,
    Disabled
    // Removed = row deleted (like McpServer — no enum value needed)
}

public enum KoratErrorCode
{
    PendingApproval,
    AccessDenied,
    GrantRevoked,
    ServerDisabled,
    ServerUnavailable,
    OfflineNode,
    PayloadLimitExceeded,
    CryptoFailure,
    DuplicateServerName,
    NotAuthorized,
    NotFound,
    InvalidStateTransition,
    Validation,
    GrainTimeout,
    /// <summary>
    /// 030 (push-to-wake): a silent push was sent to wake the iOS node; the node did not
    /// come online within the wake window. The agent should retry in ~30 s.
    /// Old agents surface the string via OpenOutcome(Denied, Reason); future agents can
    /// match the code and auto-retry once.
    /// </summary>
    NodeWaking,
    /// <summary>
    /// A transient failure reaching the underlying data store (Postgres reconnect, login
    /// failing, connection reset). Surfaced when a third-party data-store exception
    /// (Npgsql / DbException / EF DbUpdateException) escapes a grain: the
    /// DataExceptionTranslationFilter rewrites it to a serializable KoratDomainException so
    /// Orleans does not throw CodecNotFoundException trying to serialize the foreign type.
    /// Best-effort retry is appropriate.
    /// </summary>
    DataStoreUnavailable,
    /// <summary>
    /// A channel binding create lost the race for a bot that is already bound. Telegram allows
    /// exactly ONE webhook per bot GLOBALLY, so (Kind, BotId) is unique across all Spaces; the
    /// partial-unique index is the race-safe backstop behind the endpoint's check-then-act 409.
    /// ChannelBindingGrain translates the unique-violation DbUpdateException to this so the
    /// channels endpoint can map it to 409 instead of a generic DataStoreUnavailable.
    /// </summary>
    DuplicateChannelBotId,
    /// <summary>
    /// Review fix (§5 product, high→medium): the owner asked to re-issue a fresh verify code
    /// (<see cref="Korat.GrainInterfaces.IChannelBindingGrain.ReissueVerifyCodeAsync"/>) for a
    /// binding that is already verified — there is no code left to re-issue. The endpoint maps
    /// this to 409, mirroring <see cref="DuplicateChannelBotId"/>'s translation pattern.
    /// </summary>
    ChannelAlreadyVerified,

    /// <summary>
    /// Increment 2 (HTTP MCP OAuth): session-open denial for an oauth http_cloud server awaiting
    /// owner re-authorization (initial consent never finished, or a refresh failure). Denied at
    /// session-open only — NOT at access-request time, mirroring ServerDisabled's admission gate
    /// in NodeGatewayService.HandleRequestSessionAsync.
    /// </summary>
    ServerNeedsReauth
}
