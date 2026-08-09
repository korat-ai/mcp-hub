using System.Security.Cryptography;
using System.Text;
using Korat.Cloud.Mcp.Space;
using Korat.Cloud.Web.Auth.Options;
using Korat.Cloud.Web.Auth.Services;
using Korat.Cloud.Web.Spaces;
using Korat.Domain;
using Korat.Domain.Entities;
using Korat.Domain.Persistence;
using Microsoft.Extensions.Options;
using OpenIddict.Validation;
using Korat.Mcp;

namespace Korat.Cloud.Web.Mcp.Space;

/// <summary>
/// Space-MCP (increment 1, Task 7): allow-listed Origins for the <c>/mcp/{spaceSeg}</c>
/// responder (Global Constraint "Origin header MUST be validated"). Bound from the optional
/// <c>Korat:Cloud:SpaceMcp</c> config section as a plain singleton record — mirrors
/// <see cref="Korat.Cloud.Web.Spaces.InferenceTimeouts"/>'s own binding style in
/// <c>Program.cs</c> rather than a full <c>IOptions&lt;T&gt;</c> ceremony, since this is a
/// single, rarely-changed array with no per-request reactivity requirement.
///
/// S3 (plan-review correction): an ABSENT <c>Origin</c> header is always allowed regardless of
/// this list's contents — Cursor/Codex/Claude MCP clients send no <c>Origin</c> at all, and the
/// mandatory Space-pinned bearer already defeats DNS-rebinding (a rebound browser page has no
/// token to present). Only a PRESENT-and-not-in-this-list Origin is rejected (403). Defaults to
/// an empty allow-list: inc-1 is dev-only (Global Constraint O1), so nothing needs to be
/// pre-populated until a real browser-based MCP client is expected to reach this endpoint.
/// </summary>
public sealed record SpaceMcpOptions
{
    public string[] AllowedOrigins { get; init; } = [];
}

/// <summary>
/// Space-MCP (increment 1, Task 7): the Streamable-HTTP responder driving
/// <c>POST/GET/DELETE /mcp/{spaceSeg}</c> (Global Constraint "MCP transport"). Every request —
/// regardless of verb — re-authenticates via <see cref="SpaceMcpAuth"/> (never trusting a prior
/// call), validates <c>Origin</c> (S3) and <c>MCP-Protocol-Version</c>, then derives this
/// caller's durable <see cref="SpaceMcpConsumerIdentity"/> before touching any session state.
///
/// <b>POST</b>: <c>initialize</c> mints a fresh CSPRNG <c>Mcp-Session-Id</c> (N5 — a
/// client-supplied one is never honoured — see <see cref="HandlePostAsync"/>) and wraps the
/// aggregator grain's BARE <c>InitializeResult</c> (<see cref="ISpaceMcpAggregatorGrain.InitializeAsync"/>)
/// under the request's own JSON-RPC envelope; every other method requires an existing session
/// and re-validates <see cref="ISpaceMcpAggregatorGrain.GetBindingAsync"/> against THIS caller's
/// own identity/Space before dispatching (SF-5 — the session id is a routing handle, not a
/// credential; a mismatched/unknown/terminated session is always <c>404</c>, never distinguished
/// from "never existed"). inc-1 is single-JSON-response only (open decision #3, no POST-SSE); a
/// notification/response gets <c>202</c> with no body, driven purely off
/// <see cref="ISpaceMcpAggregatorGrain.DispatchAsync"/>'s own null-means-202 contract.
///
/// <b>GET</b>: opens an SSE stream and long-polls <see cref="ISpaceMcpAggregatorGrain.NextListChangedAsync"/>
/// (Task 8, SF-6) — each iteration blocks INSIDE the grain call itself (bounded by the grain's own
/// heartbeat, well under Orleans' 30s response timeout) rather than spinning a local delay; a
/// returned cursor greater than the one this loop already knows about writes a
/// <c>notifications/tools/list_changed</c> SSE event, an unchanged return is a keep-alive no-op.
/// Every iteration re-checks <see cref="ISpaceMcpAggregatorGrain.GetBindingAsync"/> (N2) so a
/// recycled/terminated session closes the stream instead of polling a dead grain forever.
///
/// <b>DELETE</b>: re-validates the binding, then <see cref="ISpaceMcpAggregatorGrain.TerminateAsync"/>
/// (S4) and returns <c>204</c>.
///
/// O2 (plan-review correction, DEFERRED): no per-token active-session cap is enforced here. Each
/// Space-MCP session is one grain + up to N relay sessions, and nothing in this dispatcher bounds
/// how many sessions a single token can hold open concurrently. Deferred per the plan's own O2
/// escape hatch — inc-1 is dev-only (Global Constraint O1 already gates prod rollout on a
/// separate identity-migration story), and the existing per-IP <c>InferencePreAuthPolicy</c> rate
/// limit on this route already bounds the OPEN rate, just not the concurrently-held total. A real
/// cap belongs with the console-facing session list (a future task) rather than a bare in-memory
/// counter a multi-instance Fly deploy couldn't share anyway.
/// </summary>
public sealed class SpaceMcpDispatcher(
    SpaceSlugService slugService,
    ICliTokenService cliTokens,
    IMetadataRepository repository,
    IClusterClient clusterClient,
    SpaceMcpOptions options,
    IOptions<CliOptions> cliOptions,
    OpenIddictValidationService oauthValidation,
    ILogger<SpaceMcpDispatcher> logger)
{
    /// <summary>MCP protocol versions accepted on the <c>MCP-Protocol-Version</c> header (Global
    /// Constraint "MCP transport"). Independent of <see cref="SpaceMcpAggregatorGrain"/>'s OWN
    /// echo set (N4) — this one gates the transport-level header on every request, that one
    /// governs what the aggregator echoes back inside a client's <c>initialize</c> params.</summary>
    private static readonly HashSet<string> SupportedProtocolVersions =
        new(StringComparer.Ordinal) { "2025-06-18", "2025-03-26" };

    private const string McpSessionIdHeader = "Mcp-Session-Id";
    private const string McpProtocolVersionHeader = "MCP-Protocol-Version";

    public async Task HandlePostAsync(HttpContext ctx, string spaceSeg, CancellationToken ct)
    {
        var gate = await AuthenticateAndGateAsync(ctx, spaceSeg, ct);
        if (gate is null)
            return; // a failure branch already wrote the status code.
        var (principal, consumerIdentity) = gate.Value;

        // Global Constraint "MCP transport": Accept must list BOTH application/json and
        // text/event-stream on every POST (inc-1 never actually streams the POST response itself
        // — open decision #3 — but the transport spec requires the client to have declared it
        // could accept either shape).
        if (!AcceptsJsonAndEventStream(ctx.Request))
        {
            ctx.Response.StatusCode = StatusCodes.Status406NotAcceptable;
            return;
        }

        var bodyText = await ReadBodyOrNullAsync(ctx, ct);
        if (bodyText is null)
            return; // 413 already written.

        JsonRpcMessage msg;
        try
        {
            msg = JsonRpcMessage.Parse(bodyText);
        }
        catch
        {
            ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        if (msg.Method == "initialize")
        {
            // N9 (adversarial review, third pass): `initialize` MUST be sent as a JSON-RPC
            // *request* — an `id` present — never as a notification. The pre-fix code happily
            // minted a fresh session and wrapped the aggregator's bare result under a literal
            // `id:null` envelope for a malformed notification-shaped `initialize`, silently
            // accepting garbage instead of rejecting it.
            if (msg.Id is null)
            {
                ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
                return;
            }

            // N5: a client-supplied Mcp-Session-Id header on `initialize` is IGNORED outright —
            // we never even read it here. A fresh CSPRNG id (>=128-bit, per SF-5) is minted every
            // time, so a client cannot smuggle a pre-chosen/predictable session id into the
            // routing table.
            var sessionId = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
            var grain = clusterClient.GetGrain<ISpaceMcpAggregatorGrain>(sessionId);

            string bareResult;
            try
            {
                bareResult = await grain.InitializeAsync(
                    new SpaceMcpSessionContext(consumerIdentity, principal.SpaceId, principal.Owner),
                    bodyText);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Space-MCP: initialize failed unexpectedly spaceId={SpaceId}", principal.SpaceId.Value);
                ctx.Response.StatusCode = StatusCodes.Status500InternalServerError;
                return;
            }

            // The grain returns the BARE InitializeResult JSON (not a full envelope) — this is
            // the one place the dispatcher itself builds the {"jsonrpc":"2.0","id":...,"result":...}
            // wrapper, under the EXTERNAL client's own request id.
            ctx.Response.Headers[McpSessionIdHeader] = sessionId;
            ctx.Response.StatusCode = StatusCodes.Status200OK;
            ctx.Response.ContentType = "application/json";
            await ctx.Response.WriteAsync(JsonRpcMessage.Result(msg.Id, bareResult), ct);
            return;
        }

        // Every non-initialize POST requires an existing session — SF-5: the session id alone is
        // never trusted, so every one of these calls re-derives consumerIdentity from THIS
        // request's own bearer (already done in AuthenticateAndGateAsync above) and re-checks the
        // grain's own recorded binding before dispatching anything to it.
        var sessionHeader = ctx.Request.Headers[McpSessionIdHeader].ToString();
        if (string.IsNullOrEmpty(sessionHeader))
        {
            ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        var existingGrain = clusterClient.GetGrain<ISpaceMcpAggregatorGrain>(sessionHeader);
        var binding = await existingGrain.GetBindingAsync();
        if (!BindingMatches(binding, consumerIdentity, principal.SpaceId))
        {
            // Unknown, expired, terminated, or bound to a DIFFERENT (consumerIdentity, SpaceId) —
            // all indistinguishable from the caller's point of view, all 404 (SF-5).
            ctx.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        string? result;
        try
        {
            result = await existingGrain.DispatchAsync(bodyText);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Space-MCP: DispatchAsync failed unexpectedly sessionId={SessionId}", sessionHeader);
            ctx.Response.StatusCode = StatusCodes.Status500InternalServerError;
            return;
        }

        if (result is null)
        {
            // Notification or a stray response (id absent) — 202 Accepted, no body, per the
            // Streamable-HTTP transport spec.
            ctx.Response.StatusCode = StatusCodes.Status202Accepted;
            return;
        }

        ctx.Response.StatusCode = StatusCodes.Status200OK;
        ctx.Response.ContentType = "application/json";
        await ctx.Response.WriteAsync(result, ct);
    }

    public async Task HandleGetAsync(HttpContext ctx, string spaceSeg, CancellationToken ct)
    {
        var gate = await AuthenticateAndGateAsync(ctx, spaceSeg, ct);
        if (gate is null)
            return;
        var (principal, consumerIdentity) = gate.Value;

        // Global Constraint "MCP transport": GET => text/event-stream, or 405.
        if (!AcceptsEventStream(ctx.Request))
        {
            ctx.Response.StatusCode = StatusCodes.Status405MethodNotAllowed;
            return;
        }

        var sessionHeader = ctx.Request.Headers[McpSessionIdHeader].ToString();
        if (string.IsNullOrEmpty(sessionHeader))
        {
            ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        var grain = clusterClient.GetGrain<ISpaceMcpAggregatorGrain>(sessionHeader);
        var binding = await grain.GetBindingAsync();
        if (!BindingMatches(binding, consumerIdentity, principal.SpaceId))
        {
            ctx.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        SseWriter.SetSseHeaders(ctx.Response);
        await ctx.Response.StartAsync(ct);

        // Task 8 (SF-6): the grain's own NextListChangedAsync now BLOCKS (bounded by its internal
        // heartbeat, well under Orleans' 30s response timeout) until either a real bump lands or
        // the heartbeat elapses — that wait IS this loop's pacing now, so there is no separate
        // local Task.Delay spin (Task 7's stub returned instantly, which is why one was needed
        // then). Every iteration still re-checks the binding (N2) so a recycled/terminated session
        // closes the stream instead of polling a dead grain forever.
        var cursor = 0L;
        while (!ct.IsCancellationRequested)
        {
            // N7 (adversarial review, third pass): the binding re-check moved to the TOP of the
            // loop (was previously only at the bottom, AFTER the NextListChangedAsync poll). A
            // just-terminated session (TerminateAsync now also calls BumpListChanged — N7's other
            // half) would otherwise sit inside a fresh, empty activation's NextListChangedAsync
            // long-poll for up to a full ListChangedHeartbeat before this loop ever noticed
            // GetBindingAsync now returns null. Checking first means a DELETE closes an open GET
            // stream immediately: GetBindingAsync on a torn-down activation returns null instantly
            // (never blocks), so this check itself adds no latency.
            SpaceMcpBinding? currentBinding;
            try
            {
                currentBinding = await grain.GetBindingAsync();
            }
            catch (Exception)
            {
                break;
            }
            if (!BindingMatches(currentBinding, consumerIdentity, principal.SpaceId))
                break;

            long next;
            try
            {
                next = await grain.NextListChangedAsync(cursor);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Space-MCP GET-SSE: NextListChangedAsync failed sessionId={SessionId}", sessionHeader);
                break;
            }

            if (next > cursor)
            {
                cursor = next;
                try
                {
                    await WriteListChangedEventAsync(ctx.Response, ct);
                }
                catch
                {
                    // MUST-FIX 4 (adversarial review, third pass): a catch-all, not just
                    // OperationCanceledException — an abrupt client reset during WriteAsync/
                    // FlushAsync throws IOException/ConnectionResetException, which previously
                    // escaped this loop unhandled (recreating the GlitchTip-noise class fixed in
                    // b59d63d). A failed write means the client is gone; break, don't catch-and-
                    // continue.
                    break;
                }
            }
            else
            {
                // MUST-FIX 2 (adversarial review, third pass): a heartbeat return (cursor
                // unchanged) previously wrote ZERO bytes — a quiet watch stream sent nothing after
                // the initial headers, and Fly's edge proxy severs a connection idle for ~60s; the
                // client then reconnects with its cursor reset to 0, and THIS grain (whose own
                // cursor is still > 0) reports a spurious list_changed on the very next poll. An
                // SSE comment line (RFC-legal — any real SSE parser ignores a line starting with
                // `:` as a comment, never surfacing it as a data event) keeps bytes flowing every
                // ListChangedHeartbeat-ish interval (~15s, comfortably under Fly's ~60s idle
                // cutoff) without ever emitting a semantically-meaningful notification. Bonus:
                // surfaces a dead client within ≤15s instead of holding the grain-side session
                // (and its backend relay sessions) open forever for a peer that vanished without a
                // TCP RST.
                try
                {
                    await WriteKeepAliveEventAsync(ctx.Response, ct);
                }
                catch
                {
                    break;
                }
            }
        }
    }

    public async Task HandleDeleteAsync(HttpContext ctx, string spaceSeg, CancellationToken ct)
    {
        var gate = await AuthenticateAndGateAsync(ctx, spaceSeg, ct);
        if (gate is null)
            return;
        var (principal, consumerIdentity) = gate.Value;

        var sessionHeader = ctx.Request.Headers[McpSessionIdHeader].ToString();
        if (string.IsNullOrEmpty(sessionHeader))
        {
            ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        var grain = clusterClient.GetGrain<ISpaceMcpAggregatorGrain>(sessionHeader);
        var binding = await grain.GetBindingAsync();
        if (!BindingMatches(binding, consumerIdentity, principal.SpaceId))
        {
            // Terminated/unknown session — DELETE is idempotent-looking to the client either way,
            // but the transport spec calls for 404 on an already-gone session (Global Constraint).
            ctx.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        await grain.TerminateAsync();
        ctx.Response.StatusCode = StatusCodes.Status204NoContent;
    }

    // ── Shared per-request gate ─────────────────────────────────────────────────

    /// <summary>
    /// Runs the checks EVERY <c>/mcp/{spaceSeg}</c> request (POST/GET/DELETE alike) must pass
    /// before touching any session state: re-authenticate (never trusting a prior call), Origin
    /// allow-list (S3), and <c>MCP-Protocol-Version</c>. Returns <c>null</c> once any branch has
    /// already written a failure status code to <paramref name="ctx"/> — callers must return
    /// immediately in that case, mirroring <see cref="SpaceMcpAuth.AuthenticateAsync"/>'s own
    /// convention.
    /// </summary>
    private async Task<(SpaceMcpPrincipal Principal, ConsumerId ConsumerIdentity)?> AuthenticateAndGateAsync(
        HttpContext ctx, string spaceSeg, CancellationToken ct)
    {
        var principal = await SpaceMcpAuth.AuthenticateAsync(
            ctx, spaceSeg, slugService, cliTokens, repository, cliOptions.Value, oauthValidation, ct);
        if (principal is null)
            return null; // AuthenticateAsync already wrote 401/403/404.

        // S3: an ABSENT Origin is always allowed; only a PRESENT-and-not-allowlisted Origin is
        // rejected. Cursor/Codex/Claude send no Origin at all — treating "absent" as a failure
        // would break every real MCP client this endpoint targets.
        var origin = ctx.Request.Headers.Origin.ToString();
        if (!string.IsNullOrEmpty(origin) &&
            !options.AllowedOrigins.Contains(origin, StringComparer.OrdinalIgnoreCase))
        {
            ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
            return null;
        }

        var protocolVersion = ctx.Request.Headers[McpProtocolVersionHeader].ToString();
        if (!string.IsNullOrEmpty(protocolVersion) && !SupportedProtocolVersions.Contains(protocolVersion))
        {
            ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
            return null;
        }
        // Absent => treated as the default (2025-03-26) and accepted; nothing further to do here
        // — the resolved default isn't threaded any further, it only gates acceptance.

        // SF-4: the durable identity is now derived INSIDE SpaceMcpAuth (per credential kind),
        // so this layer never branches on how the caller authenticated — it just reads it off
        // the returned principal.
        return (principal, principal.ConsumerIdentity);
    }

    private static bool BindingMatches(SpaceMcpBinding? binding, ConsumerId consumerIdentity, SpaceId spaceId) =>
        binding is not null &&
        binding.ConsumerId == consumerIdentity.Value &&
        binding.SpaceId == spaceId.Value;

    private static bool AcceptsJsonAndEventStream(HttpRequest request)
    {
        var accept = request.Headers.Accept.ToString();

        // N5 (adversarial review, third pass): tolerate wildcards. `curl` (and most quick
        // manual/dev-tool probes) sends the bare default `Accept: */*` with no explicit media
        // types at all — the pre-fix strict "must literally contain both substrings" check
        // rejected that (and an absent header) even though `*/*` unambiguously accepts anything,
        // including both media types this endpoint requires. An absent/empty Accept, or one
        // containing `*/*`, satisfies BOTH halves; `application/*`/`text/*` each satisfy their
        // own half on their own. An Accept that explicitly lists only ONE concrete type (with no
        // matching wildcard covering the other) still fails — this loosens "both types must be
        // spelled out literally", it does not drop the requirement that both are covered somehow.
        if (string.IsNullOrEmpty(accept) || accept.Contains("*/*", StringComparison.OrdinalIgnoreCase))
            return true;

        var acceptsJson = accept.Contains("application/json", StringComparison.OrdinalIgnoreCase)
            || accept.Contains("application/*", StringComparison.OrdinalIgnoreCase);
        var acceptsEventStream = accept.Contains("text/event-stream", StringComparison.OrdinalIgnoreCase)
            || accept.Contains("text/*", StringComparison.OrdinalIgnoreCase);
        return acceptsJson && acceptsEventStream;
    }

    private static bool AcceptsEventStream(HttpRequest request) =>
        request.Headers.Accept.ToString().Contains("text/event-stream", StringComparison.OrdinalIgnoreCase);

    private static async Task WriteListChangedEventAsync(HttpResponse response, CancellationToken ct)
    {
        var payload = JsonRpcMessage.Notification("notifications/tools/list_changed");
        var bytes = Encoding.UTF8.GetBytes($"event: message\ndata: {payload}\n\n");
        await response.Body.WriteAsync(bytes, ct);
        await response.Body.FlushAsync(ct);
    }

    /// <summary>MUST-FIX 2 (adversarial review, third pass): an SSE comment line — RFC-legal per
    /// the Server-Sent-Events spec (any line starting with `:` is a comment, silently ignored by
    /// every real SSE parser, never dispatched as a `message` event) — written on every heartbeat
    /// (cursor-unchanged) iteration of the GET-SSE loop so bytes keep flowing on an otherwise
    /// quiet watch stream. See the loop's own call site for the full "Fly edge idle cutoff"
    /// rationale.</summary>
    private static async Task WriteKeepAliveEventAsync(HttpResponse response, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(": keepalive\n\n");
        await response.Body.WriteAsync(bytes, ct);
        await response.Body.FlushAsync(ct);
    }

    /// <summary>Reads the request body capped at <see cref="PayloadLimitPolicy.DefaultPerMessageBytes"/>
    /// (mirrors <c>InferenceDispatcher</c>'s own <c>LengthLimitedStream</c> use — same 413-on-overflow
    /// shape). Writes 413 itself and returns <c>null</c> when the body is too large.</summary>
    private static async Task<string?> ReadBodyOrNullAsync(HttpContext ctx, CancellationToken ct)
    {
        var maxBytes = PayloadLimitPolicy.DefaultPerMessageBytes;

        // Fast-path rejection on a declared Content-Length before even opening the limited
        // stream — mirrors InferenceDispatcher's own defense-in-depth pre-check.
        if (ctx.Request.ContentLength is long declaredLength && declaredLength > maxBytes)
        {
            ctx.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
            return null;
        }

        try
        {
            using var limited = new LengthLimitedStream(ctx.Request.Body, maxBytes);
            using var reader = new StreamReader(limited, Encoding.UTF8);
            return await reader.ReadToEndAsync(ct);
        }
        catch (InvalidDataException)
        {
            ctx.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
            return null;
        }
    }

    /// <summary>Length-limited wrapper to prevent unbounded memory consumption on body read —
    /// verbatim copy of <c>InferenceDispatcher.LengthLimitedStream</c> (private to that class, so
    /// duplicated here rather than shared; same precedent as this codebase's two independent
    /// JsonRpcMessage types, see <c>Korat.Cloud.Mcp.Space.JsonRpcMessage</c>'s own doc comment).</summary>
    private sealed class LengthLimitedStream(Stream inner, long max) : Stream
    {
        private long _read;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_read >= max) throw new InvalidDataException("Request body exceeds maximum allowed size.");
            var n = inner.Read(buffer, offset, (int)Math.Min(count, max - _read));
            _read += n;
            return n;
        }
        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct)
        {
            if (_read >= max) throw new InvalidDataException("Request body exceeds maximum allowed size.");
            var n = await inner.ReadAsync(buffer.AsMemory(offset, (int)Math.Min(count, max - _read)), ct);
            _read += n;
            return n;
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
