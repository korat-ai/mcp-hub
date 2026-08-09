namespace Korat.GrainInterfaces;

/// <summary>
/// Increment 2 (HTTP MCP OAuth): one in-flight authorize round-trip's full context. Non-durable
/// by design (mirrors DeviceCodeState's persistence boundary — pending = in-memory, only the
/// authorized token is durable). ClientSecret is null for a public/PKCE-only DCR client or a
/// manual-fallback client with no secret.
/// </summary>
[GenerateSerializer]
public sealed record PendingOAuthState(
    [property: Id(0)] string ServerId,
    [property: Id(1)] Guid OwnerUserId,
    [property: Id(2)] string SpaceId,
    [property: Id(3)] string PkceVerifier,
    [property: Id(4)] string Issuer,
    [property: Id(5)] string AuthorizationEndpoint,
    [property: Id(6)] string TokenEndpoint,
    [property: Id(7)] string ClientId,
    [property: Id(8)] string? ClientSecret);

/// <summary>
/// Increment 2: one in-flight authorize round-trip. Keyed by the high-entropy, single-use
/// `state` value (plan-time decision (a)). Mirrors IDeviceCodeGrain exactly: non-durable,
/// burn-on-consume, lazy TTL expiry (no explicit timer — expiry is checked on access).
/// </summary>
public interface IPendingOAuthGrain : IGrainWithStringKey
{
    Task InitializeAsync(PendingOAuthState state, TimeSpan ttl);

    /// <summary>
    /// Blocker 2 fix (fable plan-review): a NON-CONSUMING read — returns the state if it exists,
    /// is unconsumed, and is within its TTL, otherwise null. Mirrors IDeviceCodeGrain.GetAsync's
    /// peek/consume split (DeviceCodeGrain.cs: GetAsync always non-mutating; ConsumeAsync snapshots
    /// then burns only from the one valid pre-terminal status). The callback handler MUST validate
    /// owner/serverId/supersession/issuer on the PEEKED value BEFORE ever burning the state — a
    /// REJECTED attempt (wrong owner, mismatched serverId, superseded, issuer mismatch) must never
    /// consume another owner's still-pending consent.
    /// </summary>
    Task<PendingOAuthState?> PeekAsync();

    /// <summary>Returns the state exactly once (single-use); returns null if never initialized,
    /// already consumed, or past its TTL deadline.</summary>
    Task<PendingOAuthState?> ConsumeAsync();
}

/// <summary>
/// Increment 2: one-slot-per-server pointer to the CURRENT (not-yet-superseded) pending
/// authorize flow's `state` value. Keyed by serverId. A new authorize/reconnect action
/// overwrites this pointer — the callback handler checks the path {serverId}'s pointer still
/// equals the incoming state BEFORE consuming the IPendingOAuthGrain, so an older, superseded
/// state is rejected even though its own pending grain is technically still valid and unconsumed
/// (plan-time decision (a): "a new consent supersedes an unfinished one for the same server").
/// </summary>
public interface IPendingOAuthPointerGrain : IGrainWithStringKey
{
    Task SetCurrentStateAsync(string state, TimeSpan ttl);

    /// <summary>Returns the current state value, or null if never set or past its TTL deadline.</summary>
    Task<string?> GetCurrentStateAsync();
}
