using System.Security.Cryptography;
using Korat.Cloud.Security.Envelope;
using Korat.Domain;
using Korat.Domain.Persistence;
using Korat.Persistence;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Korat.Auth.Tests;

/// <summary>
/// Envelope test doubles, extracted from the removed EnvelopeSecurityAcceptanceTests when
/// the agent platform went: these are generic (DbContext factory, options monitor, in-memory
/// DEK store) and EnvelopeCryptoTests still needs them.
/// </summary>
public static class EnvelopeSecurityAcceptanceTests
{
    public sealed class TestDbContextFactory(InMemoryDatabaseRoot root, string name)
        : IDbContextFactory<KoratDbContext>
    {
        public KoratDbContext CreateDbContext() =>
            new(new DbContextOptionsBuilder<KoratDbContext>()
                .UseInMemoryDatabase(name, root)
                .Options);

        public Task<KoratDbContext> CreateDbContextAsync(CancellationToken ct = default) =>
            Task.FromResult(CreateDbContext());
    }

    public sealed class StaticOptionsMonitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue => value;
        public T Get(string? name) => value;
        public IDisposable OnChange(Action<T, string?> listener) => DummyDisposable.Instance;

        public sealed class DummyDisposable : IDisposable
        {
            public static readonly DummyDisposable Instance = new();
            public void Dispose() { }
        }
    }

    /// <summary>
    /// In-memory "store" that holds {ciphertext, hint} per pointId,
    /// also backed by an EF InMemory KoratDbContext (for SpaceEncryptionKeys).
    /// </summary>
    public sealed class InMemoryDekSecretStore : IMetadataRepository
    {
        private readonly Dictionary<string, string?> _ciphertexts = new();
        private readonly Dictionary<string, string?> _hints       = new();

        public InMemoryDatabaseRoot Root   { get; }
        public string               DbName { get; }

        public InMemoryDekSecretStore(InMemoryDatabaseRoot root, string dbName)
        {
            Root   = root;
            DbName = dbName;
        }




        // ── IMetadataRepository secret methods ───────────────────────────────




        // All other IMetadataRepository members not used by these tests
        public Task EnsureCreatedAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task UpsertNodeAsync(Korat.Domain.Entities.Node n, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<Korat.Domain.Entities.Node?> GetNodeAsync(NodeId id, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<Korat.Domain.Entities.Node>> ListNodesAsync(SpaceId id, CancellationToken ct = default) => throw new NotSupportedException();
        public Task DeleteNodeAsync(NodeId id, CancellationToken ct = default) => throw new NotSupportedException();
        public Task UpsertMcpServerAsync(Korat.Domain.Entities.McpServer s, CancellationToken ct = default) => throw new NotSupportedException();
        public Task SetMcpServerSecretAsync(McpServerId id, string ciphertext, string secretHint, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<string?> GetMcpServerSecretCiphertextAsync(McpServerId id, CancellationToken ct = default) => throw new NotSupportedException();
        public Task ClearMcpServerSecretAsync(McpServerId id, CancellationToken ct = default) => throw new NotSupportedException();
        public Task SetMcpServerOAuthTokenAsync(McpServerId id, string ciphertext, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<string?> GetMcpServerOAuthTokenCiphertextAsync(McpServerId id, CancellationToken ct = default) => throw new NotSupportedException();
        public Task ClearMcpServerOAuthTokenAsync(McpServerId id, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<Korat.Domain.Entities.McpServer?> GetMcpServerAsync(McpServerId id, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<Korat.Domain.Entities.McpServer?> GetMcpServerByDisplayNameAsync(SpaceId id, string n, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<Korat.Domain.Entities.McpServer>> ListMcpServersAsync(SpaceId id, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<PurgeableServer>> ListPurgeableMcpServersAsync(DateTimeOffset d, CancellationToken ct = default) => throw new NotSupportedException();
        public Task DeleteMcpServerAsync(McpServerId id, CancellationToken ct = default) => throw new NotSupportedException();
        public Task AddTombstoneAsync(SpaceId s, NodeId n, string d, Korat.Domain.Auth.UserId u, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<bool> TombstoneExistsAsync(SpaceId s, NodeId n, string d, CancellationToken ct = default) => throw new NotSupportedException();
        public Task RemoveTombstoneAsync(SpaceId s, NodeId n, string d, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<Korat.Domain.Entities.McpServerTombstone>> ListTombstonesForNodeAsync(SpaceId s, NodeId n, CancellationToken ct = default) => throw new NotSupportedException();
        public Task UpsertAccessRequestAsync(Korat.Domain.Entities.AccessRequest r, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<Korat.Domain.Entities.AccessRequest?> GetAccessRequestAsync(AccessRequestId id, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<Korat.Domain.Entities.AccessRequest?> GetPendingAccessRequestAsync(SpaceId s, ConsumerId a, McpServerId m, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<Korat.Domain.Entities.AccessRequest>> ListAccessRequestsAsync(SpaceId s, AccessRequestStatus? status = null, CancellationToken ct = default) => throw new NotSupportedException();
        public Task UpsertGrantAsync(Korat.Domain.Entities.Grant g, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<Korat.Domain.Entities.Grant?> GetGrantAsync(GrantId id, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<Korat.Domain.Entities.Grant?> GetActiveGrantAsync(SpaceId s, ConsumerId a, McpServerId m, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<Korat.Domain.Entities.Grant>> ListGrantsAsync(SpaceId s, CancellationToken ct = default) => throw new NotSupportedException();
        public Task UpsertAgentClientAsync(Korat.Domain.Entities.Consumer a, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<Korat.Domain.Entities.Consumer?> GetAgentClientAsync(ConsumerId id, CancellationToken ct = default) => throw new NotSupportedException();
        public Task UpsertSessionAsync(Korat.Domain.Entities.RelaySession s, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<Korat.Domain.Entities.RelaySession?> GetSessionAsync(SessionId id, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<Korat.Domain.Entities.RelaySession>> ListSessionsAsync(SpaceId s, bool inc = true, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<ReapableSession>> ListReapableSessionsAsync(DateTimeOffset d, DateTimeOffset sentinelSessionAgeCutoff, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<(Korat.Domain.Entities.AccessRequest, Korat.Domain.Entities.Grant)> ApproveAccessRequestAsync(Korat.Domain.Entities.AccessRequest r, Korat.Domain.Entities.Grant g, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<Korat.Domain.Auth.UserId>> ListUserIdsWithOnlineServerAsync(DateTimeOffset d, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<bool> HasOnlineServerAsync(Korat.Domain.Auth.UserId u, DateTimeOffset d, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<Korat.Domain.Entities.Space?> GetSpaceAsync(SpaceId id, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<SpaceId?> GetSpaceIdBySlugAsync(string slug, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<string?> GetSpaceSlugAsync(SpaceId id, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<bool> TrySetSpaceSlugAsync(SpaceId id, string slug, CancellationToken ct = default) => throw new NotSupportedException();
        // F6: user-profile methods — not exercised by envelope security tests.
        public Task<Korat.Domain.Auth.User?> GetUserAsync(Korat.Domain.Auth.UserId userId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<Korat.Domain.Auth.User> UpdateUserDisplayNameAsync(Korat.Domain.Auth.UserId userId, string displayName, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<Korat.Domain.Auth.User> ReloadUserAsync(Korat.Domain.Auth.UserId userId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task DeleteAgentAsync(AgentId id, CancellationToken ct = default) => throw new NotSupportedException();
        public Task TouchThreadAsync(ThreadId id, DateTimeOffset updatedAt, CancellationToken ct = default) => throw new NotSupportedException();
        public Task RemoveRoomParticipantAsync(RoomId roomId, AgentId agentId, CancellationToken ct = default) => throw new NotSupportedException();
    }

}