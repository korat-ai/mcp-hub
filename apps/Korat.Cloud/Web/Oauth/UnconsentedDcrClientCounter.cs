using Korat.Persistence;
using Microsoft.EntityFrameworkCore;
using OpenIddict.Abstractions;
using OpenIddict.EntityFrameworkCore.Models;

namespace Korat.Cloud.Web.Oauth;

/// <summary>
/// Registration-flood-DoS hardening: counts UNCONSENTED DCR clients — <c>dcr_</c>-prefixed
/// (<see cref="KoratOAuthConstants.DcrClientIdPrefix"/>) application rows with ZERO currently-VALID
/// authorizations. This is the input to the PRIMARY register-cap gate
/// (<see cref="SpaceMcpDcrOptions.MaxUnconsentedClients"/>, checked in
/// <see cref="DcrEndpoints.MapDcrEndpoints"/>'s handler): a client that completes consent stops
/// counting the instant its authorization goes <see cref="OpenIddictConstants.Statuses.Valid"/>, so
/// a junk-registration flood can never crowd out a real client mid-consent or already consented.
/// </summary>
public interface IUnconsentedDcrClientCounter
{
    Task<int> CountAsync(CancellationToken ct);
}

/// <summary>
/// EF Core implementation over the OpenIddict entities OpenIddict itself scaffolds into
/// <see cref="KoratDbContext"/> (<c>Program.cs</c>'s <c>ef.UseDbContext&lt;KoratDbContext&gt;()</c>
/// call never invokes <c>ReplaceDefaultEntities</c>, so these are the DEFAULT, string-keyed
/// <see cref="OpenIddictEntityFrameworkCoreApplication"/> / <c>...Authorization</c> types — the
/// same ones <c>modelBuilder.UseOpenIddict()</c> in <c>KoratDbContext.OnModelCreating</c>
/// registers). One correlated-NOT-EXISTS COUNT query — deliberately NOT an O(N)
/// enumerate-with-per-client-authorization-query (the shape the TTL sweep's per-item authorization
/// lookup uses, which is fine off the hot path but would itself be a query-amplification vector if
/// run on every registration under the exact flood this cap defends against). <c>.StartsWith</c>
/// has been EF-Core-escaped for SQL LIKE wildcards since EF Core 2.2 (the "dcr_" prefix's trailing
/// underscore IS a LIKE wildcard — belt-and-suspenders here, since no non-DCR client_id happens to
/// collide with it), and the correlated <c>.Any(...)</c> over the <c>Authorizations</c> collection
/// navigation translates to a single SQL <c>NOT EXISTS</c> subquery on Postgres. Both this and the
/// plain <c>.StartsWith</c> also translate correctly on the EF Core InMemory provider used by
/// Korat.Cloud.IntegrationTests — proven by <c>DcrBoundsTests.CountAsync_CountsOnlyUnconsentedDcrClients</c>
/// running green against that provider, not merely inspected.
/// </summary>
public sealed class UnconsentedDcrClientCounter(KoratDbContext dbContext) : IUnconsentedDcrClientCounter
{
    // Deliberately keyed on the dcr_-prefixed client_id (server-stamped only by
    // DcrEndpoints.HandleRegisterAsync's mint path — never client-supplied), NOT on the
    // korat:dcr Properties marker DcrRegistrationReaperService keys on. Both keyings currently
    // identify the exact same row set, but they are NOT interchangeable: an invariant of the
    // single server-stamped mint path, not a redundancy to collapse.
    public Task<int> CountAsync(CancellationToken ct) =>
        dbContext.Set<OpenIddictEntityFrameworkCoreApplication>()
            .Where(a => a.ClientId != null
                && a.ClientId.StartsWith(KoratOAuthConstants.DcrClientIdPrefix)
                && !a.Authorizations!.Any(z => z.Status == OpenIddictConstants.Statuses.Valid))
            .CountAsync(ct);
}
