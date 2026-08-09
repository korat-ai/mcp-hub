using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using Korat.Cloud.Security.Audit;
using Korat.Domain;
using Korat.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Korat.Cloud.Security.Envelope;

/// <summary>
/// Manages per-space DEKs: lazy creation, wrapping under the active KEK, persistence in
/// <c>SpaceEncryptionKeys</c>, and an in-memory cache of unwrapped DEKs with TTL.
///
/// The plaintext DEK is NEVER persisted — only the KEK-wrapped form is stored in Postgres.
/// The in-memory cache holds the plain DEK temporarily (≤15 min absolute / 5 min sliding).
/// Cache eviction (capacity / TTL) drops the reference and lets GC reclaim the array; it does
/// NOT zero in place. <see cref="ShredAsync"/> (intentional crypto-shred) zeros the cached
/// array before dropping it, and deletes the DB row so the DEK cannot be re-loaded.
/// Each <see cref="DekHandle"/> returned to callers carries a PRIVATE COPY of the key bytes,
/// isolating callers from concurrent eviction (torn-key fix — see fix/dek-tornkey).
///
/// 032 (C5): all KEK use goes through the <see cref="IKekProvider"/> seam (default:
/// <see cref="ConfigKekProvider"/> = KEK bytes from Fly-secret config; future: KMS-backed
/// provider as a pure DI swap). Behaviour is byte-identical to the pre-seam #55 code.
/// 032 (C1): dek.create is audited (required); dek.unwrap_failure is audited best-effort.
///
/// DI lifetime: Singleton on the web host. Uses IDbContextFactory (pooled) for DB access.
/// NOT a grain — data-plane rule: data lives outside grain persistent state.
/// </summary>
public sealed class SpaceDekProvider : IDisposable
{
    private record CacheEntry(byte[] Dek, int DekVersion, string KekId, DateTimeOffset AbsoluteExpiry)
    {
        public DateTimeOffset SlidingExpiry { get; set; } = DateTimeOffset.UtcNow.Add(SlidingTtl);
    }

    private static readonly TimeSpan SlidingTtl  = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan AbsoluteTtl = TimeSpan.FromMinutes(15);
    private const int MaxCacheEntries = 2000;

    private readonly ConcurrentDictionary<string, CacheEntry> _cache = new(StringComparer.Ordinal);
    private readonly IDbContextFactory<KoratDbContext> _dbFactory;
    private readonly IKekProvider _kekProvider;
    private readonly ILogger<SpaceDekProvider> _logger;
    private readonly IAuditLog? _auditLog;

    public SpaceDekProvider(
        IDbContextFactory<KoratDbContext> dbFactory,
        IKekProvider kekProvider,
        ILogger<SpaceDekProvider> logger,
        IAuditLog? auditLog = null)
    {
        _dbFactory   = dbFactory;
        _kekProvider = kekProvider;
        _logger      = logger;
        _auditLog    = auditLog;
    }

    /// <summary>
    /// Returns the active (highest-version) DEK for a space, creating one if none exists.
    /// Returns null ONLY when envelope is not enabled (IKekProvider.IsEnabled == false; legacy mode).
    /// Throws <see cref="InvalidOperationException"/> if an existing DEK row's KEK is missing from
    /// config or the unwrap authentication fails — hard misconfiguration, must not fall back to DP.
    /// </summary>
    public async Task<DekHandle?> GetOrCreateDekAsync(SpaceId spaceId, CancellationToken ct = default)
    {
        if (!_kekProvider.IsEnabled)
            return null;

        var cacheKey = CacheKey(spaceId.Value, "active");

        // Fast path: check cache (extend sliding TTL on hit)
        if (_cache.TryGetValue(cacheKey, out var entry) && !IsExpired(entry))
        {
            entry.SlidingExpiry = DateTimeOffset.UtcNow.Add(SlidingTtl);
            // Return a PRIVATE COPY so that concurrent cache eviction cannot zero the array
            // while AesGcm is still reading it (torn-key write-path corruption fix).
            return new DekHandle((byte[])entry.Dek.Clone(), entry.DekVersion, entry.KekId);
        }

        // Slow path: load from DB (or create)
        return await LoadOrCreateDekAsync(spaceId, cacheKey, ct);
    }

    /// <summary>
    /// Gets an unwrapped DEK by (SpaceId, DekVersion). Used at decrypt time.
    /// Returns null if the DEK row does not exist (no secret has been stored for this version).
    /// Throws <see cref="InvalidOperationException"/> if the DEK row exists but the KEK is missing
    /// from config or the unwrap fails — this is a hard misconfiguration, not a recoverable state.
    /// </summary>
    public async Task<DekHandle?> GetDekAsync(SpaceId spaceId, int dekVersion, CancellationToken ct = default)
    {
        var cacheKey = CacheKey(spaceId.Value, dekVersion.ToString());

        if (_cache.TryGetValue(cacheKey, out var entry) && !IsExpired(entry))
        {
            entry.SlidingExpiry = DateTimeOffset.UtcNow.Add(SlidingTtl);
            // Return a PRIVATE COPY — same torn-key safety as GetOrCreateDekAsync.
            return new DekHandle((byte[])entry.Dek.Clone(), entry.DekVersion, entry.KekId);
        }

        return await LoadDekFromDbAsync(spaceId, dekVersion, cacheKey, ct);
    }

    /// <summary>
    /// Deletes all DEK rows for a space (crypto-shred). Cache is invalidated immediately.
    /// After this call, all envelope-encrypted secrets for this space are unrecoverable.
    /// Returns the number of deleted DEK rows.
    /// NOTE (multi-silo): only THIS silo's cache is evicted — other machines may serve cached
    /// plain DEKs for up to 15 min. The documented v1 shred procedure ends with a machine
    /// restart (IR runbook §5); a NATS eviction broadcast is a post-#51 follow-on.
    /// </summary>
    public async Task<int> ShredAsync(SpaceId spaceId, CancellationToken ct = default)
    {
        // Invalidate cache immediately
        foreach (var key in _cache.Keys.Where(k => k.StartsWith(spaceId.Value + "|", StringComparison.Ordinal)))
            if (_cache.TryRemove(key, out var evicted))
                ZeroMemory(evicted.Dek);

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var rows = await db.SpaceEncryptionKeys
            .Where(r => r.SpaceId == spaceId.Value)
            .ToListAsync(ct);
        if (rows.Count > 0)
        {
            db.SpaceEncryptionKeys.RemoveRange(rows);
            await db.SaveChangesAsync(ct);
            _logger.LogInformation("Crypto-shred: deleted {Count} DEK row(s) for space {SpaceId}.",
                rows.Count, spaceId.Value);
        }
        return rows.Count;
    }

    /// <summary>
    /// Re-wraps all DEK rows that use an old KEK id under the current active KEK.
    /// Used during KEK rotation. Does NOT touch any secret ciphertext.
    /// Returns the number of rows processed (re-wrapped or skipped-with-warning).
    /// </summary>
    public async Task<int> RewrapAllDeksAsync(CancellationToken ct = default)
    {
        if (!_kekProvider.IsEnabled)
            throw new InvalidOperationException("No active KEK configured — cannot rewrap.");

        var activeKekId = _kekProvider.ActiveKekId!;

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var staleRows = await db.SpaceEncryptionKeys
            .Where(r => r.KekId != activeKekId)
            .ToListAsync(ct);

        foreach (var row in staleRows)
        {
            if (!_kekProvider.KnowsKek(row.KekId))
            {
                _logger.LogWarning("Rewrap: KEK '{KekId}' for space {SpaceId} v{DekVersion} not in config — skipping.",
                    row.KekId, row.SpaceId, row.DekVersion);
                continue;
            }

            // Unwrap DEK with old KEK
            byte[] plainDek;
            try
            {
                plainDek = await _kekProvider.UnwrapDekAsync(
                    row.KekId, row.WrapNonce, row.WrappedDek, row.SpaceId, row.DekVersion, ct);
            }
            catch (CryptographicException ex)
            {
                _logger.LogError(ex, "Rewrap: unwrap failed for space {SpaceId} v{DekVersion}.", row.SpaceId, row.DekVersion);
                continue;
            }

            // Re-wrap under active KEK
            var (newNonce, newWrapped) = await _kekProvider.WrapDekAsync(
                activeKekId, plainDek, row.SpaceId, row.DekVersion, ct);
            Array.Clear(plainDek, 0, plainDek.Length);

            row.KekId      = activeKekId;
            row.WrapNonce  = newNonce;
            row.WrappedDek = newWrapped;
            row.RotatedAt  = DateTimeOffset.UtcNow;
        }

        await db.SaveChangesAsync(ct);

        // Invalidate the whole cache so stale plain-DEKs are evicted
        EvictAll();
        _logger.LogInformation("Rewrap complete: processed {Count} DEK row(s).", staleRows.Count);
        return staleRows.Count;
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    private async Task<DekHandle?> LoadOrCreateDekAsync(
        SpaceId spaceId, string cacheKey, CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        // Load the highest-version DEK for this space
        var row = await db.SpaceEncryptionKeys
            .Where(r => r.SpaceId == spaceId.Value)
            .OrderByDescending(r => r.DekVersion)
            .FirstOrDefaultAsync(ct);

        if (row is not null)
        {
            // Unwrap using the row's KEK
            var handle = await UnwrapAndCacheAsync(row, cacheKey, ct);
            return handle;
        }

        // No DEK yet — create one
        return await CreateDekAsync(spaceId, db, cacheKey, ct);
    }

    private async Task<DekHandle?> CreateDekAsync(
        SpaceId spaceId, KoratDbContext db, string cacheKey, CancellationToken ct)
    {
        var activeKekId = _kekProvider.ActiveKekId;
        if (activeKekId is null)
        {
            _logger.LogWarning("Envelope: no active KEK — cannot create DEK for space {SpaceId}.", spaceId.Value);
            return null;
        }

        var plainDek = new byte[EnvelopeCipher.KeySize];
        RandomNumberGenerator.Fill(plainDek);
        const int version = 1;

        byte[] nonce, wrapped;
        try
        {
            (nonce, wrapped) = await _kekProvider.WrapDekAsync(activeKekId, plainDek, spaceId.Value, version, ct);
        }
        catch (InvalidOperationException)
        {
            // Active KEK invalid despite IsEnabled (should not happen after startup validation).
            Array.Clear(plainDek, 0, plainDek.Length);
            _logger.LogWarning("Envelope: active KEK '{KekId}' is invalid — cannot create DEK for space {SpaceId}.",
                activeKekId, spaceId.Value);
            return null;
        }

        var record = new SpaceEncryptionKeyRecord
        {
            SpaceId    = spaceId.Value,
            DekVersion = version,
            KekId      = activeKekId,
            WrapNonce  = nonce,
            WrappedDek = wrapped,
            CreatedAt  = DateTimeOffset.UtcNow
        };

        try
        {
            db.SpaceEncryptionKeys.Add(record);
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // PK race: another silo won — zero our plain DEK and load the winner's row
            Array.Clear(plainDek, 0, plainDek.Length);
            _logger.LogDebug("Envelope: DEK create race for space {SpaceId}; loading winner's row.", spaceId.Value);
            return await LoadOrCreateDekAsync(new SpaceId(spaceId.Value), cacheKey, ct);
        }

        // 032 C1: dek.create is a privileged mutation — audited fail-closed. If the audit write
        // fails the surrounding secret-set operation fails (the orphan DEK row is harmless:
        // no ciphertext references it yet, and the next attempt reuses it).
        if (_auditLog is not null)
        {
            await _auditLog.RecordAsync(new AuditEvent(
                Action: AuditActions.DekCreate,
                TargetType: "space_dek",
                TargetId: $"{spaceId.Value}/v{version}",
                SpaceId: spaceId.Value,
                DetailsJson: AuditDetails.Json(new { kekId = activeKekId, dekVersion = version })),
                required: true, ct);
        }

        // Cache stores the canonical array; the handle gets its own copy so that
        // concurrent cache eviction (EvictExpired / EvictAll / ShredAsync) cannot
        // zero the array while AesGcm is still reading it (torn-key fix).
        AddToCache(cacheKey, plainDek, version, activeKekId);
        var handle = new DekHandle((byte[])plainDek.Clone(), version, activeKekId);
        return handle;
    }

    private async Task<DekHandle?> LoadDekFromDbAsync(
        SpaceId spaceId, int dekVersion, string cacheKey, CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var row = await db.SpaceEncryptionKeys
            .FirstOrDefaultAsync(r => r.SpaceId == spaceId.Value && r.DekVersion == dekVersion, ct);
        if (row is null)
            return null;
        return await UnwrapAndCacheAsync(row, cacheKey, ct);
    }

    private async Task<DekHandle> UnwrapAndCacheAsync(
        SpaceEncryptionKeyRecord row, string cacheKey, CancellationToken ct)
    {
        // MAJOR companion fix (#55, preserved across the C5 seam): an EXISTING DEK row whose KEK
        // is ABSENT from config must THROW (IKekProvider.UnwrapDekAsync raises
        // InvalidOperationException) — returning null would allow callers to silently fall back
        // to DataProtection format, defeating the feature. CryptographicException from AES-GCM
        // (wrong AAD / tamper) is re-thrown as InvalidOperationException below.
        byte[] plainDek;
        try
        {
            plainDek = await _kekProvider.UnwrapDekAsync(
                row.KekId, row.WrapNonce, row.WrappedDek, row.SpaceId, row.DekVersion, ct);
        }
        catch (CryptographicException ex)
        {
            // Do NOT log KEK or DEK material; log only the structural metadata.
            _logger.LogError(ex,
                "Envelope: DEK unwrap authentication failure for space {SpaceId} v{DekVersion} — tampered data or wrong KEK.",
                row.SpaceId, row.DekVersion);

            // 032 C1: best-effort audit — an unwrap failure is a prime IR detection signal
            // (tampered SpaceEncryptionKeys rows / swapped KEK bytes, runbook §1).
            if (_auditLog is not null)
            {
                await _auditLog.RecordAsync(new AuditEvent(
                    Action: AuditActions.DekUnwrapFailure,
                    TargetType: "space_dek",
                    TargetId: $"{row.SpaceId}/v{row.DekVersion}",
                    Outcome: AuditOutcomes.Failure,
                    SpaceId: row.SpaceId,
                    DetailsJson: AuditDetails.Json(new { kekId = row.KekId })),
                    required: false, ct);
            }

            // Re-throw as InvalidOperationException: the DEK row exists but cannot be unwrapped.
            // This signals infrastructure-level corruption (not a per-ciphertext auth failure).
            throw new InvalidOperationException(
                $"Envelope: DEK unwrap authentication failure for space '{row.SpaceId}' v{row.DekVersion} " +
                $"(kekId='{row.KekId}'). This indicates tampered wrapped-DEK storage or wrong KEK bytes.", ex);
        }

        // Cache owns the canonical array; return a private copy to the caller.
        AddToCache(cacheKey, plainDek, row.DekVersion, row.KekId);
        return new DekHandle((byte[])plainDek.Clone(), row.DekVersion, row.KekId);
    }

    private void AddToCache(string cacheKey, byte[] dek, int dekVersion, string kekId)
    {
        if (_cache.Count >= MaxCacheEntries)
            EvictExpired();

        _cache[cacheKey] = new CacheEntry(dek, dekVersion, kekId, DateTimeOffset.UtcNow.Add(AbsoluteTtl));
    }

    private static bool IsExpired(CacheEntry entry)
    {
        var now = DateTimeOffset.UtcNow;
        return now > entry.AbsoluteExpiry || now > entry.SlidingExpiry;
    }

    private void EvictExpired()
    {
        // Drop reference only — do NOT zero the cached array.
        // Each DekHandle already holds a private copy, so no live caller shares this array.
        // Zeroing here would be redundant and was previously the source of the torn-key
        // corruption: if a handle was obtained just before eviction the shared array could be
        // zeroed mid-AesGcm-constructor. GC reclaims the memory once no references remain.
        foreach (var (key, entry) in _cache)
            if (IsExpired(entry))
                _cache.TryRemove(key, out _);
    }

    private void EvictAll()
    {
        // Same rationale as EvictExpired: drop references, rely on GC.
        // ShredAsync is the only path that intentionally zeros (crypto-shred semantics).
        foreach (var key in _cache.Keys)
            _cache.TryRemove(key, out _);
    }

    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.NoOptimization)]
    private static void ZeroMemory(byte[] data) => Array.Clear(data, 0, data.Length);

    private static string CacheKey(string spaceId, string versionLabel) => $"{spaceId}|{versionLabel}";

    public void Dispose() => EvictAll();
}

/// <summary>
/// A handle to an unwrapped (plaintext) DEK.
/// The <see cref="Dek"/> array is a PRIVATE COPY owned exclusively by this handle —
/// it is independent of the cache and may be used (read) freely without synchronising
/// with eviction. Callers must not mutate or zero it; just let it be GC-collected.
/// </summary>
public sealed record DekHandle(byte[] Dek, int DekVersion, string KekId);
