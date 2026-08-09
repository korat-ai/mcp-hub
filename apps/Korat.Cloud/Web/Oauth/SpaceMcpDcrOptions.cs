namespace Korat.Cloud.Web.Oauth;

/// <summary>
/// Space-MCP inc-2b: bounds for the open RFC 7591 DCR endpoint (section
/// <c>Korat:Cloud:SpaceMcpDcr</c>; plain singleton, same binding style as
/// <see cref="SpaceMcpOAuthOptions"/>). Open registration is unauthenticated by protocol, so
/// EVERY guard here is load-bearing:
/// <list type="bullet">
///   <item><see cref="Enabled"/> — a kill switch. Off ⇒ /connect/register 404s AND the AS
///   metadata omits registration_endpoint; the pre-registered client still works, so DCR can
///   be disabled under abuse without breaking existing clients.</item>
///   <item><see cref="MaxUnconsentedClients"/> — registration-flood-DoS hardening: the PRIMARY
///   register-cap gate, checked FIRST. Counts only UNCONSENTED DCR clients (dcr_-prefixed, zero
///   currently-VALID authorizations — <see cref="IUnconsentedDcrClientCounter"/>), so a flood of
///   junk registrations can never crowd out a real client that is mid-consent or already
///   consented: a consented row never counts toward this budget, no matter how many rows exist.
///   Default 500 — generous headroom for legitimate re-registration churn (spec open-q #3) while
///   still bounding an unauthenticated flood's total junk-row footprint between reaper sweeps.</item>
///   <item><see cref="MaxClients"/> — SECONDARY backstop: an absolute total-rows ceiling
///   (consented + unconsented + the handful of non-DCR rows), checked only after the primary gate
///   passes. Defense-in-depth against a bug in the unconsented-counting logic above — should
///   never be the gate that actually fires in practice, since <see cref="MaxUnconsentedClients"/>
///   is always reached first for an unconsented flood, and consented rows are bounded by real
///   user volume, not attacker volume.</item>
///   <item><see cref="UnconsentedTtlMinutes"/> — a DCR client with zero *valid* authorizations
///   older than this is junk and is swept (Task 6; MF-3: revoked/expired-only authorizations do
///   NOT count as consented, so a consented-then-revoked client is swept too once past TTL).
///   Bounds the DCR-re-registration churn (spec open-q #3): Claude/Cursor re-register per launch,
///   but un-consented rows self-expire. Registration-flood-DoS hardening moved the default from
///   hours to MINUTES — 15, not the earlier 2h/24h, and not the initially-tried 5: the TTL clock
///   starts at REGISTRATION, and consent requires a full interactive sign-in leg (email/password
///   or OAuth redirect, often 2FA, THEN the consent screen itself) — a sub-10-minute TTL risks
///   reaping a slow first-time user mid sign-in, who then hits an opaque <c>invalid_client</c> on
///   the authorize callback. 15 minutes comfortably covers that whole human-paced flow while
///   costing almost nothing extra on the abuse side: the real flood bound is
///   <see cref="MaxUnconsentedClients"/>, not this TTL, so 15-vs-5 minutes only adds ~10 minutes to
///   the post-burst drain time, not 10 minutes of extra exposure per row. A real client that
///   registers but delays consent past this TTL could in principle still be reaped mid-flow — its
///   consent attempt would then fail, and the MCP client simply re-registers on its next
///   retry/launch, which is the same self-healing churn this option already bounds.</item>
///   <item><see cref="SweepIntervalMinutes"/> — how often the TTL reaper sweeps (Task 6). Moved
///   from hourly to every 5 minutes alongside the minutes-scale TTL above, so a budget filled by a
///   burst still drains within roughly one TTL window (~15-20 minutes total) rather than sitting
///   pinned for up to an hour.</item>
///   <item><see cref="RegisterRateLimitPerMinute"/> — per-IP permit for the endpoint (Task 4).</item>
///   <item><see cref="RegisterSubnetRateLimitPerMinute"/> — registration-flood-DoS hardening item
///   3: a SECOND, per-/24-IPv4 (/48-IPv6) window on the same endpoint, wired into the
///   <c>RateLimiterOptions.GlobalLimiter</c> (composes by AND with the per-IP policy above — same
///   mechanism the Telegram webhook's per-IP pre-auth guard already uses). Closes the gap where
///   the per-IP limit alone is defeated by an attacker rotating source IPs WITHIN one subnet: a
///   /24 = 256 IPs x <see cref="RegisterRateLimitPerMinute"/> (5120/min at the default 20) would
///   otherwise reach the same operator's real per-IP allowance 256-fold. Framed honestly: this
///   only SLOWS one subnet's fill of the junk budget and caps IP-rotation — the hard occupancy
///   guarantee remains <see cref="MaxUnconsentedClients"/>, not this window. Trade-off: co-tenants
///   of a shared subnet share this budget. The window is acquired BEFORE the per-IP policy and a
///   fixed-window permit is NOT returned on a later-rejected request, so a SINGLE abusive source
///   at &gt;=60/min drains its whole /24's window and 429s its neighbors — it does not take a
///   synchronized burst from many real users (a large-CGNAT /24, or the ~256 residential /56
///   households an IPv6 /48 can span, is the realistic collateral). That is retriable, and 60/min
///   (3x the per-IP default) is generous versus any realistic legitimate launch cadence for one
///   subnet; the strict, budget-on-attempts posture is deliberate for an anti-DoS layer.</item>
///   <item><see cref="MaxRequestBytes"/> — request-body cap; a DCR request is tiny. Enforced by
///   reading the body through a length-capped stream (Task 4, plan-review MF-1) — NOT a
///   <c>Request.ContentLength</c> check, which is null (and so silently skipped) under
///   <c>Transfer-Encoding: chunked</c>.</item>
///   <item><see cref="MaxRedirectUris"/> — plan-review MF-1: caps how many <c>redirect_uris</c>
///   entries one registration may persist. Without this, a chunked request under
///   <see cref="MaxRequestBytes"/> could still smuggle thousands of individually-valid loopback
///   URIs into a single fat row (every later consent render + authorize exact-match pays for
///   it).</item>
///   <item><see cref="MaxClientNameLength"/> — plan-review MF-1: caps <c>client_name</c>, stored
///   verbatim as the OpenIddict application's <c>DisplayName</c> and rendered on the consent
///   page.</item>
/// </list>
/// </summary>
public sealed record SpaceMcpDcrOptions
{
    public bool Enabled { get; init; } = true;
    public int MaxClients { get; init; } = 1000;
    public int MaxUnconsentedClients { get; init; } = 500;
    public int UnconsentedTtlMinutes { get; init; } = 15;
    public int SweepIntervalMinutes { get; init; } = 5;
    public int RegisterRateLimitPerMinute { get; init; } = 20;
    public int RegisterSubnetRateLimitPerMinute { get; init; } = 60;
    public int MaxRequestBytes { get; init; } = 4096;
    public int MaxRedirectUris { get; init; } = 5;
    public int MaxClientNameLength { get; init; } = 256;
}
