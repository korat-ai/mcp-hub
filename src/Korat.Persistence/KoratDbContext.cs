using Korat.Domain;
using Korat.Domain.Auth;
using Korat.Domain.Entities;
using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

// Auth-domain type aliases: short names like User / RelaySession / Invite collide with BCL types,
// legacy SessionRecord, and (in the case of User) several .NET base-class names. Aliasing once
// at the file head keeps DbSet declarations + Entity<T> blocks readable without `Korat.Domain.Auth.*`
// prefix noise everywhere.
using AuthUser = Korat.Domain.Auth.User;
using AuthExternalLogin = Korat.Domain.Auth.ExternalLogin;
using AuthMagicLinkToken = Korat.Domain.Auth.MagicLinkToken;

namespace Korat.Persistence;

/// <summary>
/// EF Core контекст Postgres. *Record — строки таблиц; доменные типы маппятся через <see cref="EntityMapping"/>.
/// </summary>
public sealed class KoratDbContext(DbContextOptions<KoratDbContext> options) : DbContext(options), IDataProtectionKeyContext
{
    /// <summary>
    /// 010-drop-redis-to-postgres: ASP.NET Data Protection key ring (OAuth state, antiforgery,
    /// session cookies) — moved off Redis so all instances share one key set via Postgres.
    /// </summary>
    public DbSet<DataProtectionKey> DataProtectionKeys => Set<DataProtectionKey>();

    public DbSet<SpaceRecord> Spaces => Set<SpaceRecord>();
    public DbSet<SpaceMemberRecord> SpaceMembers => Set<SpaceMemberRecord>();
    public DbSet<NodeRecord> Nodes => Set<NodeRecord>();
    public DbSet<McpServerRecord> McpServers => Set<McpServerRecord>();
    public DbSet<McpServerTombstoneRecord> McpServerTombstones => Set<McpServerTombstoneRecord>();
    public DbSet<ConsumerRecord> AgentClients => Set<ConsumerRecord>();
    public DbSet<AccessRequestRecord> AccessRequests => Set<AccessRequestRecord>();
    public DbSet<GrantRecord> Grants => Set<GrantRecord>();
    public DbSet<SessionRecord> Sessions => Set<SessionRecord>();
    // Auth DbSets — entity types live in Korat.Domain.Auth.
    public DbSet<AuthUser> Users => Set<AuthUser>();
    public DbSet<AuthExternalLogin> ExternalLogins => Set<AuthExternalLogin>();
    /// <summary>Auth sessions (sliding-window cookie sessions). Named AuthSessions to avoid clash with relay SessionRecord.</summary>
    public DbSet<LoginSession> AuthSessions => Set<LoginSession>();
    public DbSet<AuthMagicLinkToken> MagicLinkTokens => Set<AuthMagicLinkToken>();
    /// <summary>Long-lived CLI credentials issued via device flow (SP4).</summary>
    public DbSet<Korat.Domain.Auth.CliToken> CliTokens => Set<Korat.Domain.Auth.CliToken>();
    /// <summary>Pending email-change verification tokens (SP3). Single-use, 30-min TTL, hashed.</summary>
    public DbSet<Korat.Domain.Auth.EmailChangeToken> EmailChangeTokens => Set<Korat.Domain.Auth.EmailChangeToken>();

    // ── Envelope encryption (#55) ─────────────────────────────────────────────
    /// <summary>#55: per-space DEK rows. PK = (SpaceId, DekVersion). Wrapped under KEK — plaintext DEK never persisted.</summary>
    public DbSet<SpaceEncryptionKeyRecord> SpaceEncryptionKeys => Set<SpaceEncryptionKeyRecord>();

    // ── Audit logging (#57 Leg 3 C1, spec 032) ────────────────────────────────
    /// <summary>032: tamper-evident, append-only audit trail. Hash-chained rows — NEVER stores secret values.</summary>
    public DbSet<AuditEventRecord> AuditEvents => Set<AuditEventRecord>();
    /// <summary>032: single-row (Id=1) chain head. Serializes Seq assignment via SELECT ... FOR UPDATE.</summary>
    public DbSet<AuditChainHeadRecord> AuditChainHead => Set<AuditChainHeadRecord>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<SpaceRecord>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasMaxLength(64);
            e.Property(x => x.OwnerUserId).HasMaxLength(128);
            e.Property(x => x.DisplayName).HasMaxLength(256);
            // SC-1: exactly one default Space per owner enforced at the DB layer.
            // InMemory ignores filtered indexes (no-op); production Postgres enforces the predicate.
            e.HasIndex(x => x.OwnerUserId)
                .IsUnique()
                .HasFilter($"\"{nameof(SpaceRecord.IsDefault)}\" = true");
            // 029: unique URL slug — nullable (lazy-assigned); unique filtered index on non-null values.
            // Rolling-deploy-safe: old silos ignore the column; null means "not yet assigned".
            e.Property(x => x.Slug).HasMaxLength(64);
            e.HasIndex(x => x.Slug)
                .IsUnique()
                .HasFilter($"\"{nameof(SpaceRecord.Slug)}\" IS NOT NULL");
        });

        modelBuilder.Entity<SpaceMemberRecord>(e =>
        {
            e.HasKey(x => new { x.SpaceId, x.UserId });
            e.Property(x => x.SpaceId).HasMaxLength(64).IsRequired();
            e.Property(x => x.UserId).HasMaxLength(128).IsRequired();
            e.Property(x => x.Role).HasConversion<string>().HasMaxLength(32).IsRequired();
            e.Property(x => x.JoinedAt).IsRequired();
        });

        modelBuilder.Entity<NodeRecord>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.SpaceId, x.DisplayName });
            e.Property(x => x.Id).HasMaxLength(64);
            e.Property(x => x.SpaceId).HasMaxLength(64);
            // 017: store Kind as a string (varchar 32) for consistency with AccessRequestStatus,
            // SpaceMemberRole and other enum columns. Keeps Postgres predicate-filter SQL readable
            // and avoids the InMemory/Postgres int-vs-string mismatch described in G11 comments.
            e.Property(x => x.Kind)
                .HasConversion<string>()
                .HasMaxLength(32)
                .HasDefaultValue(NodeKind.Publisher);
            // Node host metadata (B3-review, low): client-controlled strings refreshed on every
            // hello — capped at varchar(256) so a hostile/buggy CLI can't persist unbounded text.
            // The gateway truncates to the same cap in NodeGatewayService.HandleHelloAsync.
            e.Property(x => x.Hostname).HasMaxLength(256);
            e.Property(x => x.Os).HasMaxLength(256);
            e.Property(x => x.Arch).HasMaxLength(256);
            e.Property(x => x.CliVersion).HasMaxLength(256);
            // Owner-editable note (node-visibility-doctor design 2026-07-02): 500-char cap
            // mirrors the endpoint-level validation (PATCH /api/nodes/{id} rejects >500 with 400).
            e.Property(x => x.Note).HasMaxLength(500);
        });

        modelBuilder.Entity<McpServerRecord>(e =>
        {
            e.HasKey(x => x.Id);
            // Имя MCP уникально в пределах Space (повтор — только после disable старого).
            e.HasIndex(x => new { x.SpaceId, x.DisplayName }).IsUnique();
            e.Property(x => x.Id).HasMaxLength(64);
            e.Property(x => x.SpaceId).HasMaxLength(64);
            // 021: IsAsserted — set-membership bit for declarative server reconcile (Layer 1).
            // NOT NULL DEFAULT true so pre-021 rows (written before this migration) are always
            // treated as asserted — matching the default on the domain entity.
            e.Property(x => x.IsAsserted)
                .IsRequired()
                .HasDefaultValue(true);
            // Increment 1 (http_cloud) additive nullable columns — mirrors
            // InferencePointRecord's Provider/BaseUrl/AuthHeaderName/EncryptedSecret block exactly.
            e.Property(x => x.RemoteUrl).HasMaxLength(2048);
            e.Property(x => x.AuthMode).HasMaxLength(32);
            e.Property(x => x.AuthHeaderName).HasMaxLength(256);
            e.Property(x => x.SecretHint).HasMaxLength(64);
            e.Property(x => x.EncryptedSecret).HasColumnType("text");
        });

        modelBuilder.Entity<McpServerTombstoneRecord>(e =>
        {
            // Composite key = (SpaceId, PublisherNodeId, DisplayName) — at most one tombstone per
            // deleted (node, name) in a space. Add() is an idempotent upsert keyed on this.
            e.HasKey(x => new { x.SpaceId, x.PublisherNodeId, x.DisplayName });
            e.Property(x => x.SpaceId).HasMaxLength(64);
            e.Property(x => x.PublisherNodeId).HasMaxLength(64);
            e.Property(x => x.DisplayName).HasMaxLength(256);
        });

        modelBuilder.Entity<ConsumerRecord>(e =>
        {
            // Имя таблицы закреплено явно: тип переименован AgentClient → Consumer, схема не менялась.
            e.ToTable("AgentClients");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasMaxLength(64);
        });

        modelBuilder.Entity<AccessRequestRecord>(e =>
        {
            // Колонка сохраняет прежнее имя: свойство переименовано вслед за типом
            // (AgentClientId → ConsumerId), схема не менялась.
            e.Property(x => x.ConsumerId).HasColumnName("AgentClientId");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasMaxLength(64);
            // G11: Store Status as a string (varchar 32) so the filtered-index predicate
            // "'Pending'" is a string literal, not an integer literal.  Without this
            // conversion EF Core maps enums to int by default, and Postgres rejects
            // WHERE "Status" = 'Pending' against an integer column with
            //   ERROR: operator does not exist: integer = unknown
            // The InMemory provider silently ignores filtered indexes so the bug is
            // invisible in unit tests — it only surfaces at migration / Postgres deploy time.
            e.Property(x => x.Status)
                .HasConversion<string>()
                .HasMaxLength(32);
            // C4 invariant — at most one Pending request per (SpaceId, ConsumerId, McpServerId).
            // The filtered index enforces this at the database level.
            // For Postgres the filter is a SQL predicate; for InMemory (tests) the index is a no-op
            // and the grain's idempotent-create path enforces the invariant in application code.
            e.HasIndex(x => new { x.SpaceId, x.ConsumerId, x.McpServerId })
                .IsUnique()
                .HasFilter($"\"{nameof(AccessRequestRecord.Status)}\" = '{AccessRequestStatus.Pending}'");
        });

        modelBuilder.Entity<GrantRecord>(e =>
        {
            // Колонка сохраняет прежнее имя: свойство переименовано вслед за типом
            // (AgentClientId → ConsumerId), схема не менялась.
            e.Property(x => x.ConsumerId).HasColumnName("AgentClientId");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasMaxLength(64);
            // Store Status as a string (varchar 32) for consistency with AccessRequestRecord and so
            // that any future filtered index predicate on this column uses a string literal (not int).
            e.Property(x => x.Status)
                .HasConversion<string>()
                .HasMaxLength(32);
            // Р26: 32 hex-символа (128 бит от SHA-256). Пустая строка = грант, выданный до Р26.
            e.Property(x => x.ApprovedDefinitionDigest).HasMaxLength(64);
            // Быстрый поиск active grant для пары (agent, server).
            e.HasIndex(x => new { x.ConsumerId, x.McpServerId, x.Status });
        });

        modelBuilder.Entity<SessionRecord>(e =>
        {
            // Колонка сохраняет прежнее имя: свойство переименовано вслед за типом
            // (AgentClientId → ConsumerId), схема не менялась.
            e.Property(x => x.ConsumerId).HasColumnName("AgentClientId");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasMaxLength(64);
            e.Property(x => x.AgentConnectionId).HasMaxLength(64);
            // Store Status as a string (varchar 32) for consistency with AccessRequestRecord and so
            // that any future filtered index predicate on this column uses a string literal (not int).
            e.Property(x => x.Status)
                .HasConversion<string>()
                .HasMaxLength(32);
        });

        // ── Auth entities ─────────────────────────────────────────────────────

        modelBuilder.Entity<AuthUser>(b =>
        {
            b.ToTable("User");
            b.HasKey(u => u.Id);
            b.Property(u => u.Id)
                .HasConversion(id => id.Value, v => new Korat.Domain.Auth.UserId(v));
            b.Property(u => u.PrimaryEmail).IsRequired().HasMaxLength(256);
            b.Property(u => u.DisplayName).HasMaxLength(256);
            b.Property(u => u.CreatedAt).IsRequired();
            b.Property(u => u.Status)
                .HasConversion<string>()
                .HasMaxLength(16)
                .IsRequired();
            b.Property(u => u.IsAdmin).IsRequired();
            // Unique constraint: advisory fast-path check in EmailChangeService.RequestAsync
            // scans this index; the real arbiter is the DB constraint itself — a concurrent
            // verify (Task 3) that races past the advisory check will be rejected here.
            b.HasIndex(u => u.PrimaryEmail).IsUnique();
        });

        modelBuilder.Entity<AuthExternalLogin>(b =>
        {
            b.ToTable("ExternalLogin");
            b.HasKey(x => x.Id);
            b.Property(x => x.UserId)
                .HasConversion(id => id.Value, v => new Korat.Domain.Auth.UserId(v))
                .IsRequired();
            b.Property(x => x.Provider)
                .HasConversion<string>()
                .HasMaxLength(16)
                .IsRequired();
            b.Property(x => x.ProviderUserId).IsRequired().HasMaxLength(128);
            b.Property(x => x.EmailAtLink).IsRequired().HasMaxLength(256);
            b.Property(x => x.EmailVerified).IsRequired();
            b.Property(x => x.LinkedAt).IsRequired();
            b.HasIndex(x => new { x.Provider, x.ProviderUserId }).IsUnique();
            b.HasIndex(x => x.UserId);
        });

        modelBuilder.Entity<LoginSession>(b =>
        {
            b.ToTable("AuthSession");
            b.HasKey(s => s.Id);
            b.Property(s => s.UserId)
                .HasConversion(id => id.Value, v => new Korat.Domain.Auth.UserId(v))
                .IsRequired();
            b.Property(s => s.CreatedAt).IsRequired();
            b.Property(s => s.LastUsedAt).IsRequired();
            b.Property(s => s.ExpiresAt).IsRequired();
            b.Property(s => s.AbsoluteExpiresAt).IsRequired();
            b.Property(s => s.UserAgent).HasMaxLength(512);
            b.Property(s => s.CreatedFromIp).HasMaxLength(64);
            b.Property(s => s.RevokedAt);
            b.HasIndex(s => new { s.UserId, s.RevokedAt });
        });

        modelBuilder.Entity<AuthMagicLinkToken>(b =>
        {
            b.ToTable("MagicLinkToken");
            b.HasKey(t => t.Id);
            // F5: TokenHash stores the SHA-256 hex of the raw URL token — raw value never persisted.
            // Nullable for rolling-deploy safety: old silos (pre-migration) write null; after all
            // silos are updated the column effectively becomes NOT NULL in practice.
            // SHA-256 hex = 64 chars; unique index enables O(1) hash-based lookup in TryConsumeAsync.
            b.Property(t => t.TokenHash).HasMaxLength(64);
            b.HasIndex(t => t.TokenHash).IsUnique();
            b.Property(t => t.Email).IsRequired().HasMaxLength(256);
            b.Property(t => t.IssuedAt).IsRequired();
            b.Property(t => t.ExpiresAt).IsRequired();
            b.Property(t => t.ConsumedAt);
            b.Property(t => t.IssuedFromIp).HasMaxLength(64);
            b.Property(t => t.IssuedUaHash).HasMaxLength(64);
            b.Property(t => t.ConsumedFromIp).HasMaxLength(64);
            b.Property(t => t.ConsumedUaHash).HasMaxLength(64);
            b.HasIndex(t => new { t.Email, t.IssuedAt });
            // Partial index for the cleanup job — only unconsumed tokens need expiry scanning.
            b.HasIndex(t => t.ExpiresAt)
                .HasFilter(@"""ConsumedAt"" IS NULL");
        });

        modelBuilder.Entity<Korat.Domain.Auth.CliToken>(b =>
        {
            b.ToTable("CliToken");
            b.HasKey(t => t.Id);
            b.Property(t => t.UserId)
                .HasConversion(id => id.Value, v => new Korat.Domain.Auth.UserId(v))
                .IsRequired();
            b.Property(t => t.TokenHash).IsRequired().HasMaxLength(64);
            // Space-MCP (increment 1, Task 1, S5): "space-mcp:{spaceId}" Space-pins the token
            // (10-char prefix + 32-hex SpaceId = 42 chars) — widened from 16 ("full"/"bridge-only")
            // to fit that plus headroom.
            b.Property(t => t.Scope).IsRequired().HasMaxLength(48);
            b.Property(t => t.IssuedAt).IsRequired();
            b.Property(t => t.ExpiresAt).IsRequired();
            b.Property(t => t.LastUsedAt).IsRequired();
            b.Property(t => t.RevokedAt);
            b.HasIndex(t => t.TokenHash).IsUnique();
            b.HasIndex(t => t.UserId);
            // FK UserId -> User cascade delete: orphan CliTokens cannot survive user deletion.
            b.HasOne<AuthUser>().WithMany().HasForeignKey(t => t.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<Korat.Domain.Auth.EmailChangeToken>(b =>
        {
            b.ToTable("EmailChangeToken");
            b.HasKey(t => t.Id);
            b.Property(t => t.UserId)
                .HasConversion(id => id.Value, v => new Korat.Domain.Auth.UserId(v))
                .IsRequired();
            b.Property(t => t.NewEmail).IsRequired().HasMaxLength(256);
            // SHA-256 hex = 64 chars; unique so constant-time lookup is efficient.
            b.Property(t => t.TokenHash).IsRequired().HasMaxLength(64);
            b.Property(t => t.CreatedAt).IsRequired();
            b.Property(t => t.ExpiresAt).IsRequired();
            b.Property(t => t.ConsumedAt);
            b.Property(t => t.SupersededAt);
            b.HasIndex(t => t.TokenHash).IsUnique();
            // Per-user rate-limit query scans this index.
            b.HasIndex(t => new { t.UserId, t.CreatedAt });
            // FK UserId -> User cascade delete.
            b.HasOne<AuthUser>().WithMany().HasForeignKey(t => t.UserId).OnDelete(DeleteBehavior.Cascade);
        });


        // ── Envelope encryption (#55) ─────────────────────────────────────────────

        modelBuilder.Entity<SpaceEncryptionKeyRecord>(e =>
        {
            // Composite PK: (SpaceId, DekVersion). DekVersion starts at 1, increments on DEK rotation.
            e.HasKey(x => new { x.SpaceId, x.DekVersion });
            e.Property(x => x.SpaceId).HasMaxLength(64).IsRequired();
            e.Property(x => x.KekId).HasMaxLength(64).IsRequired();
            // WrapNonce: 12 bytes (AES-GCM nonce). Fixed length.
            e.Property(x => x.WrapNonce).HasMaxLength(12).IsRequired();
            // WrappedDek: 48 bytes (32B ciphertext + 16B GCM tag). Fixed length.
            e.Property(x => x.WrappedDek).HasMaxLength(48).IsRequired();
            // Index for fast "list all DEK versions for a space" (crypto-shred, rotation).
            e.HasIndex(x => x.SpaceId);
            // No FK to Spaces — shred is explicit/audited, not cascade-delete triggered.
        });

        // ── Audit logging (#57 Leg 3 C1, spec 032) ───────────────────────────────

        modelBuilder.Entity<AuditEventRecord>(e =>
        {
            // Seq is assigned by AuditLogger from the chain head (it must be known BEFORE the
            // row hash is computed), so it is explicitly NOT a database identity column.
            e.HasKey(x => x.Seq);
            e.Property(x => x.Seq).ValueGeneratedNever();
            e.Property(x => x.ActorType).HasMaxLength(32).IsRequired();
            e.Property(x => x.ActorId).HasMaxLength(128).IsRequired();
            e.Property(x => x.AuthKind).HasMaxLength(32).IsRequired();
            e.Property(x => x.SpaceId).HasMaxLength(64);
            e.Property(x => x.Action).HasMaxLength(64).IsRequired();
            e.Property(x => x.TargetType).HasMaxLength(64).IsRequired();
            e.Property(x => x.TargetId).HasMaxLength(256).IsRequired();
            e.Property(x => x.Outcome).HasMaxLength(16).IsRequired();
            // Small, non-secret structured context. Deny-scrubbed by AuditLogger before insert.
            e.Property(x => x.DetailsJson).HasMaxLength(2048);
            e.Property(x => x.TraceId).HasMaxLength(64);
            e.Property(x => x.SourceIp).HasMaxLength(64);
            // SHA-256 chain hashes: fixed 32 bytes.
            e.Property(x => x.PrevHash).HasMaxLength(32).IsRequired();
            e.Property(x => x.RowHash).HasMaxLength(32).IsRequired();
            e.HasIndex(x => new { x.SpaceId, x.OccurredAtUtc });
            e.HasIndex(x => new { x.Action, x.OccurredAtUtc });
            e.HasIndex(x => new { x.ActorId, x.OccurredAtUtc });
        });

        modelBuilder.Entity<AuditChainHeadRecord>(e =>
        {
            // Exactly one row (Id = 1), seeded by the AddAuditEvents migration (genesis hash)
            // and lazily created by AuditLogger for InMemory test databases.
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.LastHash).HasMaxLength(32).IsRequired();
        });

        // OpenIddict: registers Applications, Authorizations, Scopes, and Tokens tables.
        // Must run after all application-owned entity configurations so OpenIddict's own
        // entity setup does not interfere with existing table mappings.
        modelBuilder.UseOpenIddict();
    }
}

/// <summary>Таблица spaces — личное пространство владельца.</summary>
public sealed class SpaceRecord
{
    public string Id { get; set; } = string.Empty;
    public string OwnerUserId { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    /// <summary>True for exactly one Space per owner — enforced by a filtered unique index in Task 2.</summary>
    public bool IsDefault { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    /// <summary>029: URL-safe slug for the /inference/{slug}/... path. Nullable — assigned lazily.</summary>
    public string? Slug { get; set; }
}

/// <summary>Таблица nodes — машина с Korat Node.</summary>
public sealed class NodeRecord
{
    public string Id { get; set; } = string.Empty;
    public string SpaceId { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    // UNWIRED/DEFERRED: DeviceFingerprint is always empty string in production — the Node gRPC
    // Hello handshake does not yet collect or transmit a device fingerprint. Column exists in the
    // schema for a future hardware-binding feature (TODO: device fingerprint collection in CLI/Node).
    public string DeviceFingerprint { get; set; } = string.Empty;
    public NodeStatus Status { get; set; }
    /// <summary>017: Publisher (runs korat up/service) or Agent (korat connect consumer identity).
    /// Stored as string for consistency with other enum columns (AccessRequestStatus, SpaceMemberRole).
    /// Default Publisher keeps pre-017 rows valid.</summary>
    public NodeKind Kind { get; set; } = NodeKind.Publisher;
    /// <summary>Gateway, через который узел сейчас online.</summary>
    public string? CurrentGatewayId { get; set; }
    public DateTimeOffset? LastSeenAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    // 030 (push-to-wake): APNs device token columns. All nullable — old rows stay valid.
    /// <summary>APNs device token (lowercase hex). Null = not wake-capable.</summary>
    public string? PushToken { get; set; }
    /// <summary>"apns" (production) or "apns_sandbox" (debug/dev builds). Null = none.</summary>
    public string? PushPlatform { get; set; }
    /// <summary>When the push token was last registered or cleared.</summary>
    public DateTimeOffset? PushTokenUpdatedAt { get; set; }

    /// <summary>
    /// cloud-m9: JSON array of capability strings advertised in the last NodeHello.
    /// Null = node has never declared capabilities (pre-029 or no capabilities).
    /// Persisted so NodeGrain can repopulate its volatile _capabilities on reactivation.
    /// </summary>
    public string? CapabilitiesJson { get; set; }

    // Node host metadata (additive, node-visibility-doctor design 2026-07-02). All nullable —
    // old rows stay valid; refreshed on every hello.
    /// <summary>Machine hostname reported by the connecting CLI. Null = legacy CLI / not yet advertised.</summary>
    public string? Hostname { get; set; }
    /// <summary>"macos" | "linux" | "windows". Null = legacy CLI / not yet advertised.</summary>
    public string? Os { get; set; }
    /// <summary>Lowercase OS architecture (e.g. "arm64", "x64"). Null = legacy CLI / not yet advertised.</summary>
    public string? Arch { get; set; }
    /// <summary>Bare SemVer of the connecting CLI. Null = legacy CLI / not yet advertised.</summary>
    public string? CliVersion { get; set; }

    // Owner-editable note (additive, node-visibility-doctor design 2026-07-02). Nullable —
    // old rows stay valid; set/cleared only via PATCH /api/nodes/{id}, never by a hello.
    /// <summary>Owner-set free-text label, ≤500 chars (enforced at the endpoint). Null = unset.</summary>
    public string? Note { get; set; }
}

/// <summary>Таблица mcp_servers — опубликованный MCP на узле.</summary>
public sealed class McpServerRecord
{
    public string Id { get; set; } = string.Empty;
    public string SpaceId { get; set; } = string.Empty;
    /// <summary>Узел, где запускается процесс MCP.</summary>
    public string PublisherNodeId { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    // UNWIRED/DEFERRED comment removed — Transport is now wired for http_cloud (Increment 1).
    public string Transport { get; set; } = "Stdio";
    public string LaunchCommand { get; set; } = string.Empty;
    public string LaunchArguments { get; set; } = string.Empty;
    public McpServerStatus Status { get; set; }
    /// <summary>021: set-membership bit — true when the last SyncMcpServers from the owner node
    /// included this server. False = soft-retired (omitted from the last sync). Default true.</summary>
    public bool IsAsserted { get; set; } = true;
    public DateTimeOffset? LastSeenAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    // ── Increment 1 (http_cloud) additive nullable columns ──────────────────────
    public string? RemoteUrl { get; set; }
    public string? AuthMode { get; set; }
    public string? AuthHeaderName { get; set; }
    /// <summary>Р27: launch command before the most recent definition change. Null = never changed.</summary>
    public string? PreviousLaunchCommand { get; set; }
    /// <summary>Р27: arguments that went with PreviousLaunchCommand.</summary>
    public string? PreviousLaunchArguments { get; set; }
    /// <summary>Р27: when the definition last changed.</summary>
    public DateTimeOffset? DefinitionChangedAt { get; set; }
    public string? SecretHint { get; set; }
    /// <summary>
    /// Envelope-encrypted static secret ciphertext. Deliberately EF-only — NEVER in the domain
    /// McpServer entity, NEVER in EntityMapping.ToRecord/ToDomain, so a normal
    /// UpsertMcpServerAsync(domain-entity) call cannot null it out. Mirrors
    /// InferencePointRecord.EncryptedSecret exactly (same reasoning: PATCH must support
    /// "edit url without touching secret").
    /// </summary>
    public string? EncryptedSecret { get; set; }

    /// <summary>
    /// Increment 2 (HTTP MCP OAuth): envelope-encrypted OAuth token document ciphertext (one
    /// JSON document, one ciphertext — access/refresh/expiry/endpoints/client credentials).
    /// Deliberately EF-only — NEVER in the domain McpServer entity, NEVER in
    /// EntityMapping.ToRecord/ToDomain — mirrors EncryptedSecret exactly, for the identical
    /// reason (a normal UpsertMcpServerAsync(domain-entity) call must not be able to null it out).
    /// </summary>
    public string? EncryptedOAuthToken { get; set; }
}

/// <summary>Step-B: durable delete-tombstone — blocks passive re-creation of a deleted (node, name).</summary>
public sealed class McpServerTombstoneRecord
{
    public string SpaceId { get; set; } = string.Empty;
    public string PublisherNodeId { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
    public string CreatedByUserId { get; set; } = string.Empty;
}

/// <summary>Таблица agent_clients — агент (Cursor и т.д.) на узле.</summary>
public sealed class ConsumerRecord
{
    public string Id { get; set; } = string.Empty;
    public string SpaceId { get; set; } = string.Empty;
    public string NodeId { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public ConsumerStatus Status { get; set; }
    public DateTimeOffset? LastSeenAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>Таблица access_requests — запрос «разрешите agent → server».</summary>
public sealed class AccessRequestRecord
{
    public string Id { get; set; } = string.Empty;
    public string SpaceId { get; set; } = string.Empty;
    public string ConsumerId { get; set; } = string.Empty;
    public string McpServerId { get; set; } = string.Empty;
    /// <summary>Узел, с которого пришёл запрос.</summary>
    public string RequestedByNodeId { get; set; } = string.Empty;
    /// <summary>Узел, где крутится MCP-сервер.</summary>
    public string PublisherNodeId { get; set; } = string.Empty;
    public AccessRequestStatus Status { get; set; }
    public DateTimeOffset RequestedAt { get; set; }
    public DateTimeOffset? ResolvedAt { get; set; }
    public string? ResolvedByUserId { get; set; }
}

/// <summary>Таблица grants — выданное разрешение на relay.</summary>
public sealed class GrantRecord
{
    public string Id { get; set; } = string.Empty;
    public string SpaceId { get; set; } = string.Empty;
    public string ConsumerId { get; set; } = string.Empty;
    public string McpServerId { get; set; } = string.Empty;
    public GrantStatus Status { get; set; }
    /// <summary>Р26: digest of the server definition as approved. Empty = pre-Р26 grant.</summary>
    public string ApprovedDefinitionDigest { get; set; } = string.Empty;
    public string? CreatedFromAccessRequestId { get; set; }
    public string ApprovedByUserId { get; set; } = string.Empty;
    public DateTimeOffset ApprovedAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
    public string? RevokedByUserId { get; set; }
}

/// <summary>Таблица sessions — метаданные relay (без MCP-payload).</summary>
public sealed class SessionRecord
{
    public string Id { get; set; } = string.Empty;
    public string SpaceId { get; set; } = string.Empty;
    public string GrantId { get; set; } = string.Empty;
    public string ConsumerId { get; set; } = string.Empty;
    public string McpServerId { get; set; } = string.Empty;
    /// <summary>Узел агента (клиент relay).</summary>
    public string ClientNodeId { get; set; } = string.Empty;
    /// <summary>Узел с MCP-процессом (publisher relay).</summary>
    public string PublisherNodeId { get; set; } = string.Empty;
    /// <summary>Gateway, маршрутизирующий эту сессию.</summary>
    public string HomeGatewayId { get; set; } = string.Empty;
    public SessionStatus Status { get; set; }
    public SessionCloseReason? CloseReason { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? EndedAt { get; set; }
    /// <summary>Суммарные байты agent → server (opaque ciphertext).</summary>
    public long BytesClientToServer { get; set; }
    /// <summary>Суммарные байты server → agent.</summary>
    public long BytesServerToClient { get; set; }
    public bool LargeTransferWarning { get; set; }

    /// <summary>022/Step-A: per-stream ConnectionId of the agent bridge that opened this
    /// session. Persisted so any silo can address the agent stream for cross-silo teardown
    /// (PublishToConnectionAsync). Nullable column with default "" so existing rows are valid.</summary>
    public string AgentConnectionId { get; set; } = string.Empty;
}

/// <summary>Таблица space_members — участники Space (в SP2 только Owner-строка владельца).</summary>
public sealed class SpaceMemberRecord
{
    public string SpaceId { get; set; } = string.Empty;
    public string UserId { get; set; } = string.Empty;
    public SpaceMemberRole Role { get; set; } = SpaceMemberRole.Owner;
    public DateTimeOffset JoinedAt { get; set; }
}

/// <summary>Роль участника Space. Owner — единственная роль в SP2 (расширяется в будущем).</summary>
public enum SpaceMemberRole
{
    Owner,
}

// ── Envelope encryption (#55) ─────────────────────────────────────────────────

/// <summary>
/// #55: per-space DEK row. Stores the DEK wrapped under a KEK (AES-256-GCM).
/// Plaintext DEK is NEVER persisted. PK = (SpaceId, DekVersion).
/// NO cascade-delete FK to Spaces — shred is explicit and audited.
/// </summary>
public sealed class SpaceEncryptionKeyRecord
{
    /// <summary>Identifies the Space that owns this DEK. Max 64 chars (matches SpaceRecord.Id).</summary>
    public string SpaceId { get; set; } = string.Empty;

    /// <summary>Monotone version counter. Starts at 1, increments on DEK rotation.</summary>
    public int DekVersion { get; set; }

    /// <summary>The KEK id that was used to wrap this DEK. Must be present in EnvelopeOptions.Keks.</summary>
    public string KekId { get; set; } = string.Empty;

    /// <summary>12-byte AES-GCM nonce used to wrap the DEK under the KEK.</summary>
    public byte[] WrapNonce { get; set; } = [];

    /// <summary>48-byte blob: 32-byte DEK ciphertext || 16-byte GCM tag. Wrapped under KEK.</summary>
    public byte[] WrappedDek { get; set; } = [];

    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>Non-null after a DEK rotation (when this version replaced an older one).</summary>
    public DateTimeOffset? RotatedAt { get; set; }
}

// ── Audit logging (#57 Leg 3 C1, spec 032) ────────────────────────────────────

/// <summary>
/// 032: one tamper-evident audit event. Rows form a single global hash chain:
/// <c>RowHash = SHA256(UTF8(canonical(fields)) || PrevHash)</c>. Append-only — rows are
/// never updated; the only delete path is the retention prune (which writes a chained
/// checkpoint event so verification survives pruning).
/// NEVER stores secret values, KEK/DEK bytes, or raw tokens — token references are row ids.
/// </summary>
public sealed class AuditEventRecord
{
    /// <summary>Monotone sequence number assigned from <see cref="AuditChainHeadRecord"/>. PK, not identity.</summary>
    public long Seq { get; set; }

    public DateTimeOffset OccurredAtUtc { get; set; }

    /// <summary>"user" | "cli_token" | "inference_key" | "node" | "system".</summary>
    public string ActorType { get; set; } = string.Empty;

    /// <summary>UserId guid; for tokens the token ROW id — never the raw token.</summary>
    public string ActorId { get; set; } = string.Empty;

    /// <summary>"cookie" | "cli_bearer" | "inference_key" | "internal".</summary>
    public string AuthKind { get; set; } = string.Empty;

    /// <summary>Space the action targeted; null for global ops (e.g. kek.rewrap, audit.anchor).</summary>
    public string? SpaceId { get; set; }

    /// <summary>Catalogued action name, e.g. "grant.revoke" (see AuditActions).</summary>
    public string Action { get; set; } = string.Empty;

    public string TargetType { get; set; } = string.Empty;
    public string TargetId { get; set; } = string.Empty;

    /// <summary>"success" | "failure" | "denied".</summary>
    public string Outcome { get; set; } = string.Empty;

    /// <summary>Small non-secret structured context (e.g. {"kekId":"k2"}). Deny-scrubbed before insert.</summary>
    public string? DetailsJson { get; set; }

    public string? TraceId { get; set; }
    public string? SourceIp { get; set; }

    /// <summary>Chain hash of the previous event (32 bytes). Genesis = SHA256("korat-audit-genesis-v1").</summary>
    public byte[] PrevHash { get; set; } = [];

    /// <summary>SHA256(canonical || PrevHash), 32 bytes.</summary>
    public byte[] RowHash { get; set; } = [];
}

/// <summary>
/// 032: the single chain-head row (Id = 1). Insertions lock this row (SELECT ... FOR UPDATE)
/// to serialize Seq assignment and keep the hash chain unbroken under concurrency.
/// </summary>
public sealed class AuditChainHeadRecord
{
    public int Id { get; set; }
    public long LastSeq { get; set; }
    public byte[] LastHash { get; set; } = [];
}

