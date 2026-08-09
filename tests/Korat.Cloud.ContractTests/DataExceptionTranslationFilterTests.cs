using System.Data.Common;
using Korat.Cloud.Orleans;
using Korat.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Orleans.Serialization;
using Orleans.TestingHost;

namespace Korat.Cloud.ContractTests;

/// <summary>
/// Covers the GlitchTip CodecNotFoundException fix (DataExceptionTranslationFilter):
///
/// 1. Unit-level — the static classification helpers (<see cref="DataExceptionTranslationFilter.IsDataStoreException"/>
///    and <see cref="DataExceptionTranslationFilter.ShouldTranslate"/>) correctly detect a
///    data-store exception anywhere in the InnerException chain and do NOT double-wrap an
///    already-domain exception.
///
/// 2. End-to-end — a real Orleans TestingHost silo with the filter installed: a grain that
///    throws a non-serializable <see cref="PostgresException"/> (and one that throws an EF
///    <see cref="DbUpdateException"/>) causes the CALLER to receive a serializable
///    <see cref="KoratDomainException"/> — NOT an
///    <see cref="Orleans.Serialization.CodecNotFoundException"/>. This is the exact masking
///    behaviour the fix removes.
/// </summary>
public sealed class DataExceptionTranslationFilterTests
{
    // ── 1. Classification helper (unit) ──────────────────────────────────────

    [Fact]
    public void IsDataStoreException_DirectNpgsqlException_True()
    {
        var ex = MakePostgresException();
        Assert.True(DataExceptionTranslationFilter.IsDataStoreException(ex));
    }

    [Fact]
    public void IsDataStoreException_NpgsqlNestedInChain_True()
    {
        // Mirrors the real shape: EF DbUpdateException wrapping a PostgresException.
        var inner = MakePostgresException();
        var outer = new InvalidOperationException("grain wrapper", new DbUpdateException("update failed", inner));
        Assert.True(DataExceptionTranslationFilter.IsDataStoreException(outer));
    }

    [Fact]
    public void IsDataStoreException_DbExceptionBase_True()
    {
        Assert.True(DataExceptionTranslationFilter.IsDataStoreException(new FakeDbException()));
    }

    [Fact]
    public void IsDataStoreException_NonDataException_False()
    {
        var ex = new InvalidOperationException("nope", new ArgumentException("still nope"));
        Assert.False(DataExceptionTranslationFilter.IsDataStoreException(ex));
    }

    [Fact]
    public void IsDataStoreException_Null_False()
    {
        Assert.False(DataExceptionTranslationFilter.IsDataStoreException(null));
    }

    [Fact]
    public void ShouldTranslate_DataException_True()
    {
        Assert.True(DataExceptionTranslationFilter.ShouldTranslate(MakePostgresException()));
    }

    [Fact]
    public void ShouldTranslate_KoratDomainException_False_NoDoubleWrap()
    {
        // Already serializable domain exception → must NOT be re-wrapped.
        var domain = new KoratDomainException(KoratErrorCode.NotFound);
        Assert.False(DataExceptionTranslationFilter.ShouldTranslate(domain));
    }

    [Fact]
    public void ShouldTranslate_KoratDomainWrappingDataException_False_NoDoubleWrap()
    {
        // A domain exception anywhere in the chain wins: the grain already produced a
        // serializable result; do not translate (which would lose the domain Code).
        var domain = new KoratDomainException(KoratErrorCode.Validation);
        var wrapped = new InvalidOperationException("x", new AggregateException(domain, MakePostgresException()));
        Assert.False(DataExceptionTranslationFilter.ShouldTranslate(wrapped));
    }

    // ── 2. End-to-end through a real Orleans silo with the filter installed ───

    [Fact]
    public async Task GrainThrowingPostgresException_CallerGetsKoratDomainException_NotCodecNotFound()
    {
        await using var cluster = BuildCluster();
        var grain = cluster.GrainFactory.GetGrain<IThrowingTestGrain>(Guid.NewGuid().ToString("N"));

        var ex = await Assert.ThrowsAsync<KoratDomainException>(() => grain.ThrowPostgresAsync());
        Assert.Equal(KoratErrorCode.DataStoreUnavailable, ex.Code);
        // The raw Npgsql message must NOT leak across the boundary.
        Assert.DoesNotContain("server login failing", ex.Message);
    }

    [Fact]
    public async Task GrainThrowingDbUpdateException_CallerGetsKoratDomainException()
    {
        await using var cluster = BuildCluster();
        var grain = cluster.GrainFactory.GetGrain<IThrowingTestGrain>(Guid.NewGuid().ToString("N"));

        var ex = await Assert.ThrowsAsync<KoratDomainException>(() => grain.ThrowDbUpdateAsync());
        Assert.Equal(KoratErrorCode.DataStoreUnavailable, ex.Code);
    }

    [Fact]
    public async Task GrainThrowingKoratDomainException_PassesThroughUnchanged_NoDoubleWrap()
    {
        await using var cluster = BuildCluster();
        var grain = cluster.GrainFactory.GetGrain<IThrowingTestGrain>(Guid.NewGuid().ToString("N"));

        var ex = await Assert.ThrowsAsync<KoratDomainException>(() => grain.ThrowDomainAsync());
        // Original code preserved — the filter did not re-wrap it as DataStoreUnavailable.
        Assert.Equal(KoratErrorCode.DuplicateServerName, ex.Code);
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private static TestCluster BuildCluster()
    {
        var builder = new TestClusterBuilder(1);
        builder.AddSiloBuilderConfigurator<FilterSiloConfigurator>();
        builder.AddClientBuilderConfigurator<FilterClientConfigurator>();
        var cluster = builder.Build();
        cluster.Deploy();
        return cluster;
    }

    /// <summary>
    /// Constructs a real <see cref="PostgresException"/> (subclass of
    /// <see cref="NpgsqlException"/>) carrying SQLSTATE 08P01 ("server login failing") — the
    /// blip class that triggered the GlitchTip codec flood.
    /// </summary>
    private static PostgresException MakePostgresException() =>
        new(messageText: "server login failing", severity: "FATAL", invariantSeverity: "FATAL", sqlState: "08P01");

    private sealed class FilterSiloConfigurator : ISiloConfigurator
    {
        public void Configure(ISiloBuilder siloBuilder)
        {
            // Match production: JSON serializer for Korat.* types so KoratDomainException
            // round-trips, plus the data-exception translation filter on every grain call.
            siloBuilder.ConfigureServices(services =>
                services.AddSerializer(b =>
                    b.AddJsonSerializer(isSupported: t => t.Namespace?.StartsWith("Korat") == true)));
            siloBuilder.AddIncomingGrainCallFilter<DataExceptionTranslationFilter>();
        }
    }

    private sealed class FilterClientConfigurator : IClientBuilderConfigurator
    {
        public void Configure(Microsoft.Extensions.Configuration.IConfiguration configuration, IClientBuilder clientBuilder) =>
            clientBuilder.ConfigureServices(services =>
                services.AddSerializer(b =>
                    b.AddJsonSerializer(isSupported: t => t.Namespace?.StartsWith("Korat") == true)));
    }

    /// <summary>A minimal DbException so we can exercise the DbException base-type branch.</summary>
    private sealed class FakeDbException : DbException
    {
        public FakeDbException() : base("fake db error") { }
    }
}

/// <summary>Test-only grain that throws the data-store exceptions the filter must translate.</summary>
public interface IThrowingTestGrain : IGrainWithStringKey
{
    Task ThrowPostgresAsync();
    Task ThrowDbUpdateAsync();
    Task ThrowDomainAsync();
}

public sealed class ThrowingTestGrain : Grain, IThrowingTestGrain
{
    public Task ThrowPostgresAsync() =>
        throw new PostgresException(
            messageText: "server login failing",
            severity: "FATAL",
            invariantSeverity: "FATAL",
            sqlState: "08P01");

    public Task ThrowDbUpdateAsync() =>
        throw new DbUpdateException(
            "update failed",
            new PostgresException("server login failing", "FATAL", "FATAL", "08P01"));

    public Task ThrowDomainAsync() =>
        throw new KoratDomainException(KoratErrorCode.DuplicateServerName);
}
