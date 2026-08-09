using Korat.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;
using Orleans;
using System;
using System.Data.Common;
using System.Threading.Tasks;

namespace Korat.Cloud.Orleans;

/// <summary>
/// Silo-wide <see cref="IIncomingGrainCallFilter"/> that translates third-party data-store
/// exceptions escaping a grain into a serializable <see cref="KoratDomainException"/>.
///
/// Why this exists (GlitchTip <c>Orleans.Serialization.CodecNotFoundException</c> flood):
/// when the DB blips (Fly Postgres reconnect / <c>08P01 server login failing</c>), a grain
/// calling <c>EfMetadataRepository</c> throws <see cref="NpgsqlException"/> /
/// <see cref="DbException"/> / <see cref="DbUpdateException"/>. None of those types carry an
/// Orleans serialization codec, so when Orleans tries to serialize the failed-call response
/// it throws <c>CodecNotFoundException</c> — which MASKS the real DB error and produces two
/// noise issues per blip.
///
/// This filter wraps EVERY grain invocation (registered via
/// <c>siloBuilder.AddIncomingGrainCallFilter&lt;DataExceptionTranslationFilter&gt;()</c>). On a
/// data-store exception it:
///   1. logs the ORIGINAL exception once (Error level) so the true error is still captured —
///      as itself, not as a CodecNotFound, and
///   2. rethrows a serializable <see cref="KoratDomainException"/>
///      (<see cref="KoratErrorCode.DataStoreUnavailable"/>) with a generic, sanitized message
///      (the raw Npgsql message can carry host/connection detail and is NOT echoed across the
///      grain boundary).
///
/// It does NOT double-wrap: an already-<see cref="KoratDomainException"/> (or any non-data
/// exception) is rethrown unchanged so existing domain-error contracts are preserved.
/// </summary>
public sealed class DataExceptionTranslationFilter : IIncomingGrainCallFilter
{
    private readonly ILogger<DataExceptionTranslationFilter> _logger;

    public DataExceptionTranslationFilter(ILogger<DataExceptionTranslationFilter> logger) =>
        _logger = logger;

    public async Task Invoke(IIncomingGrainCallContext context)
    {
        try
        {
            await context.Invoke();
        }
        catch (Exception ex) when (ShouldTranslate(ex))
        {
            // Log the ORIGINAL exception once, at the boundary, so the true DB error is
            // captured as itself (not as a downstream CodecNotFoundException).
            _logger.LogError(
                ex,
                "Data-store exception escaped grain {Grain}.{Method}; translating to {Code}.",
                context.InterfaceMethod?.DeclaringType?.Name ?? "<unknown>",
                context.InterfaceMethod?.Name ?? "<unknown>",
                nameof(KoratErrorCode.DataStoreUnavailable));

            // Sanitized, generic message — do NOT echo the raw Npgsql/EF message across the
            // grain boundary (it can carry host/connection detail).
            throw new KoratDomainException(KoratErrorCode.DataStoreUnavailable);
        }
    }

    /// <summary>
    /// True when <paramref name="ex"/> should be translated to a <see cref="KoratDomainException"/>:
    /// it is NOT already a <see cref="KoratDomainException"/> and its exception chain contains a
    /// data-store exception. Public + static so the classification can be unit-tested directly.
    /// </summary>
    public static bool ShouldTranslate(Exception? ex) =>
        GrainExceptionUnwrap.Find(ex) is null && IsDataStoreException(ex);

    /// <summary>
    /// Walks <paramref name="ex"/> and its <see cref="Exception.InnerException"/> chain looking
    /// for any third-party data-store exception:
    /// <see cref="NpgsqlException"/> (base of <c>Npgsql.PostgresException</c>),
    /// <see cref="DbException"/> (the ADO.NET base), or
    /// <see cref="DbUpdateException"/> (EF Core). Returns true on the first match.
    /// </summary>
    public static bool IsDataStoreException(Exception? ex)
    {
        for (var current = ex; current is not null; current = current.InnerException)
        {
            if (current is NpgsqlException or DbException or DbUpdateException)
                return true;
        }

        return false;
    }
}
