using System.Diagnostics;
using Korat.Cloud.Web.Auth.Security;
using Korat.Persistence;
using Microsoft.EntityFrameworkCore;
using NATS.Client.Core;

namespace Korat.Cloud.Web;

/// <summary>
/// G1 (MCP best-practice: "comprehensive health checks"). Split by trust level so we never
/// leak component topology/state to the public:
///   • <c>GET /health</c> — ANONYMOUS, shallow readiness: a single cheap Postgres reachability
///     probe → <c>{ status }</c> + 200/503. No component names, no error types, no cluster size.
///     (Fly uses TCP checks, so this is for external/uptime monitors only.)
///   • <c>GET /health/components</c> — ADMIN-gated (<see cref="AdminScopeGate.RequireAdminScope"/>):
///     full per-component status + latency (Postgres / NATS relay / Orleans cluster) for ops.
/// Postgres is critical (failure → 503); NATS and Orleans are best-effort (→ "degraded", 200)
/// since the relay is optional and Orleans membership churn during a rolling deploy is expected.
/// </summary>
public static class HealthEndpoints
{
    public static void MapKoratHealth(this WebApplication app)
    {
        // Public, shallow — readiness only, no detail leaked.
        app.MapGet("/health", async (IDbContextFactory<KoratDbContext> dbFactory, CancellationToken ct) =>
        {
            var (dbStatus, _, _) = await ProbeAsync(async token =>
            {
                await using var db = await dbFactory.CreateDbContextAsync(token);
                return await db.Database.CanConnectAsync(token);
            }, TimeSpan.FromSeconds(3), ct);

            var ok = dbStatus == "up";
            return Results.Json(
                new { status = ok ? "healthy" : "unhealthy", timestamp = DateTimeOffset.UtcNow.ToString("O") },
                statusCode: ok ? StatusCodes.Status200OK : StatusCodes.Status503ServiceUnavailable);
        }).AllowAnonymous()
          .RequireRateLimiting(RateLimiterRegistration.HealthPolicy)
          .WithName("Health");

        // Admin-only — full per-component detail.
        app.MapGet("/health/components", async (
            IDbContextFactory<KoratDbContext> dbFactory,
            IServiceProvider sp,
            CancellationToken ct) =>
        {
            var components = new Dictionary<string, object>();
            var criticalDown = false;
            var anyDegraded = false;

            // --- Postgres (critical) ---
            var (dbStatus, dbMs, dbErr) = await ProbeAsync(async token =>
            {
                await using var db = await dbFactory.CreateDbContextAsync(token);
                return await db.Database.CanConnectAsync(token);
            }, TimeSpan.FromSeconds(3), ct);
            components["postgres"] = new { status = dbStatus, latency_ms = dbMs, error = dbErr };
            if (dbStatus != "up") criticalDown = true;

            // --- NATS relay (optional / best-effort) ---
            var nats = sp.GetService<INatsConnection>();
            if (nats is null)
            {
                components["nats"] = new { status = "not_configured" };
            }
            else
            {
                var (natsStatus, natsMs, natsErr) = await ProbeNatsAsync(nats, ct);
                components["nats"] = new
                {
                    status = natsStatus,
                    latency_ms = natsMs,
                    state = nats.ConnectionState.ToString(),
                    error = natsErr,
                };
                if (natsStatus != "up") anyDegraded = true;
            }

            // --- Orleans cluster (best-effort) ---
            var client = sp.GetService<IClusterClient>();
            if (client is null)
            {
                components["orleans"] = new { status = "not_configured" };
            }
            else
            {
                var (oStatus, oMs, oErr, oCount) = await ProbeOrleansAsync(client, ct);
                components["orleans"] = new { status = oStatus, latency_ms = oMs, active_silos = oCount, error = oErr };
                if (oStatus != "up") anyDegraded = true;
            }

            var overall = criticalDown ? "unhealthy" : anyDegraded ? "degraded" : "healthy";
            var code = criticalDown ? StatusCodes.Status503ServiceUnavailable : StatusCodes.Status200OK;
            return Results.Json(new
            {
                status = overall,
                components,
                timestamp = DateTimeOffset.UtcNow.ToString("O"),
            }, statusCode: code);
        }).RequireAdminScope().WithName("HealthComponents");
    }

    private static async Task<(string status, long ms, string? err)> ProbeAsync(
        Func<CancellationToken, Task<bool>> probe, TimeSpan timeout, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout);
            var ok = await probe(cts.Token);
            return (ok ? "up" : "down", sw.ElapsedMilliseconds, ok ? null : "probe returned false");
        }
        catch (Exception ex)
        {
            return ("down", sw.ElapsedMilliseconds, ex.GetType().Name);
        }
    }

    /// <summary>
    /// Real NATS reachability probe. <see cref="NatsConnection"/> (the concrete type behind
    /// the registered <see cref="INatsConnection"/> singleton) only opens its socket lazily on
    /// first publish/subscribe — so right after a silo restart, before any relay traffic,
    /// <c>ConnectionState</c> stays <c>Closed</c> even though NATS is perfectly reachable.
    /// Trusting <c>ConnectionState == Closed</c> as "degraded" would falsely flip a healthy,
    /// merely-idle silo to degraded. <see cref="INatsConnection.PingAsync"/> (inherited from
    /// <c>INatsClient</c>) is the correct check instead: it transparently calls
    /// <c>ConnectAsync()</c> first when not already <c>Open</c>, then round-trips a real
    /// PING/PONG — so this only reports degraded on an actual failure or timeout.
    /// </summary>
    private static async Task<(string status, long ms, string? err)> ProbeNatsAsync(
        INatsConnection nats, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(2));
            await nats.PingAsync(cts.Token);
            return ("up", sw.ElapsedMilliseconds, null);
        }
        catch (Exception ex)
        {
            // NATS relay is optional / best-effort — unreachable is "degraded", not "unhealthy".
            return ("degraded", sw.ElapsedMilliseconds, ex.GetType().Name);
        }
    }

    private static async Task<(string status, long ms, string? err, int? count)> ProbeOrleansAsync(
        IClusterClient client, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(2));
            // NOTE: IManagementGrain.GetHosts(bool) has no CancellationToken overload, so
            // WaitAsync only bounds how long we WAIT for it — it does not cancel the
            // underlying grain call. On a wedged cluster the call itself keeps running
            // server-side after we've already given up and reported "degraded"; this is a
            // known, accepted limitation (no cancellable overload exists to switch to).
            var hosts = await client.GetGrain<IManagementGrain>(0).GetHosts(onlyActive: true).WaitAsync(cts.Token);
            return ("up", sw.ElapsedMilliseconds, null, hosts.Count);
        }
        catch (Exception ex)
        {
            // Membership churn / transient query failure is expected — degraded, not unhealthy.
            return ("degraded", sw.ElapsedMilliseconds, ex.GetType().Name, null);
        }
    }
}
