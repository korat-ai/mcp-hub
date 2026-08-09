using Google.Protobuf;
using Korat.Cloud.Gateways;
using Korat.Cloud.Mcp.Space;
using Korat.Cloud.Observability;
using Korat.Domain;
using Korat.GrainInterfaces;
using Korat.Relay.V1;
using Microsoft.Extensions.Logging.Abstractions;
using Orleans;
using Orleans.Runtime;

namespace Korat.Auth.Tests.SpaceMcp;

/// <summary>
/// Task 3 (2026-07-10 Space-MCP increment-1 plan), B1 plan-review correction: proves the
/// in-process delivery leg — <see cref="CallbackServerStreamWriter"/> registered against
/// <see cref="SessionRoutingTable.RegisterAgentStreamAsync"/> for a synthetic
/// <see cref="ConnectionId"/> — marshals every delivered frame into
/// <see cref="ISpaceMcpAggregatorGrain.OnDeliveryAsync"/> via a grain reference (never runs grain
/// code on the caller's thread directly), and NEVER lets a throw inside that grain call evict the
/// writer from <see cref="SessionRoutingTable"/> (<c>WriteLocalToConnectionAsync</c> would
/// otherwise evict on ANY exception — <c>SessionRoutingTable.cs:1012-1017</c>).
///
/// No Orleans cluster, no NATS — pure in-memory fakes, mirroring
/// <c>SessionAdmissionCharacterizationTests</c>' stub-grain-factory style and
/// <see cref="SessionRoutingTable"/>'s internal test constructor (<c>SessionRoutingTable.cs:123</c>).
/// </summary>
public class ConsumerDeliveryLegTests
{
    // ── Shared routing-table plumbing (mirrors SessionAdmissionCharacterizationTests) ──────────

    private static McpToolCallInspector NoopInspector() => new(new NoopSink(), NullLogger<McpToolCallInspector>.Instance);

    private sealed class NoopSink : IMcpToolCallSink
    {
        public void Record(in ToolCallEvent toolCall) { }
    }

    private sealed class NoResolver : ISessionRouteResolver
    {
        public Task<SessionRouteInfo?> ResolveAsync(SessionId sessionId, CancellationToken cancellationToken)
            => Task.FromResult<SessionRouteInfo?>(null);
    }

    private static SessionRoutingTable NewRoutingTable() =>
        new(new NullRelayBackplane(), new NoResolver(), NoopInspector(),
            _ => throw new NotSupportedException("not exercised by this test"),
            _ => throw new NotSupportedException("not exercised by this test"),
            NullLogger<SessionRoutingTable>.Instance);

    // ── Fake ISpaceMcpAggregatorGrain — captures OnDeliveryAsync args; can be told to throw ────

    private sealed class FakeAggregatorGrain : ISpaceMcpAggregatorGrain
    {
        public readonly List<(string BackendSessionId, byte[] Payload, uint Enc, string? CloseReason)> Deliveries = new();
        public bool ThrowOnNextDelivery;

        public Task OnDeliveryAsync(string backendSessionId, byte[] payload, uint enc, string? closeReason)
        {
            if (ThrowOnNextDelivery)
            {
                ThrowOnNextDelivery = false;
                throw new InvalidOperationException("simulated grain-call failure");
            }

            Deliveries.Add((backendSessionId, payload, enc, closeReason));
            return Task.CompletedTask;
        }

        // Not exercised by this delivery-leg test — only OnDeliveryAsync (above) is under test.
        public Task<string> InitializeAsync(SpaceMcpSessionContext ctx, string clientInitializeJson) => throw new NotSupportedException();
        public Task<string?> DispatchAsync(string jsonRpc) => throw new NotSupportedException();
        public Task<long> NextListChangedAsync(long knownCursor) => throw new NotSupportedException();
        public Task TerminateAsync() => throw new NotSupportedException();
        public Task<SpaceMcpBinding?> GetBindingAsync() => throw new NotSupportedException();
    }

    /// <summary>Minimal IGrainFactory — dispatches GetGrain&lt;ISpaceMcpAggregatorGrain&gt;(string)
    /// to a single fake keyed by the grain's primary key string. Every other member throws
    /// NotSupportedException; CallbackServerStreamWriter never calls them.</summary>
    private sealed class FakeGrainFactory : IGrainFactory
    {
        public readonly Dictionary<string, FakeAggregatorGrain> Grains = new();

        public TGrainInterface GetGrain<TGrainInterface>(string primaryKey, string? grainClassNamePrefix = null)
            where TGrainInterface : IGrainWithStringKey
        {
            if (typeof(TGrainInterface) == typeof(ISpaceMcpAggregatorGrain))
            {
                if (!Grains.TryGetValue(primaryKey, out var g))
                    Grains[primaryKey] = g = new FakeAggregatorGrain();
                return (TGrainInterface)(object)g;
            }
            throw new NotSupportedException($"Grain type {typeof(TGrainInterface).Name} not supported.");
        }

        // Unused by CallbackServerStreamWriter — satisfy IGrainFactory.
        public TGrainInterface GetGrain<TGrainInterface>(Guid primaryKey, string? grainClassNamePrefix = null)
            where TGrainInterface : IGrainWithGuidKey => throw new NotSupportedException();
        public TGrainInterface GetGrain<TGrainInterface>(long primaryKey, string? grainClassNamePrefix = null)
            where TGrainInterface : IGrainWithIntegerKey => throw new NotSupportedException();
        public TGrainInterface GetGrain<TGrainInterface>(Guid primaryKey, string keyExtension, string? grainClassNamePrefix = null)
            where TGrainInterface : IGrainWithGuidCompoundKey => throw new NotSupportedException();
        public TGrainInterface GetGrain<TGrainInterface>(long primaryKey, string keyExtension, string? grainClassNamePrefix = null)
            where TGrainInterface : IGrainWithIntegerCompoundKey => throw new NotSupportedException();
        public TGrainInterface GetGrain<TGrainInterface>(GrainId grainId)
            where TGrainInterface : IAddressable => throw new NotSupportedException();
        public IAddressable GetGrain(GrainId grainId) => throw new NotSupportedException();
        public IAddressable GetGrain(GrainId grainId, GrainInterfaceType interfaceType) => throw new NotSupportedException();
        public IGrain GetGrain(Type grainInterfaceType, Guid grainPrimaryKey) => throw new NotSupportedException();
        public IGrain GetGrain(Type grainInterfaceType, long grainPrimaryKey) => throw new NotSupportedException();
        public IGrain GetGrain(Type grainInterfaceType, string grainPrimaryKey) => throw new NotSupportedException();
        public IGrain GetGrain(Type grainInterfaceType, Guid primaryKey, string keyExtension) => throw new NotSupportedException();
        public IGrain GetGrain(Type grainInterfaceType, long primaryKey, string keyExtension) => throw new NotSupportedException();
        public IAddressable GetGrain(Type grainInterfaceType, IdSpan grainKey) => throw new NotSupportedException();
        public IAddressable GetGrain(Type grainInterfaceType, IdSpan grainKey, string grainClassNamePrefix) => throw new NotSupportedException();
        public TGrainObserverInterface CreateObjectReference<TGrainObserverInterface>(IGrainObserver obj)
            where TGrainObserverInterface : IGrainObserver => throw new NotSupportedException();
        public void DeleteObjectReference<TGrainObserverInterface>(IGrainObserver obj)
            where TGrainObserverInterface : IGrainObserver => throw new NotSupportedException();
    }

    // ── Tests ────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task SendToConnection_DeliversFrameToGrain_ViaGrainReference()
    {
        var routingTable = NewRoutingTable();
        var grainFactory = new FakeGrainFactory();
        const string grainKey = "mcp-session-1";
        var writer = new CallbackServerStreamWriter(grainFactory, grainKey, NullLogger.Instance);
        var conn = SpaceMcpConsumerIdentity.SyntheticConnectionId(grainKey);

        await routingTable.RegisterAgentStreamAsync(conn, writer, CancellationToken.None);

        var payloadBytes = "hello-backend"u8.ToArray();
        var sent = await routingTable.SendToConnectionAsync(conn, new GatewayToNodeMessage
        {
            Frame = new RelayFrame { SessionId = "backend-session-1", Ciphertext = ByteString.CopyFrom(payloadBytes), Enc = 0 }
        }, CancellationToken.None);

        Assert.True(sent);
        var grain = grainFactory.Grains[grainKey];
        var delivery = Assert.Single(grain.Deliveries);
        Assert.Equal("backend-session-1", delivery.BackendSessionId);
        Assert.Equal(payloadBytes, delivery.Payload);
        Assert.Equal(0u, delivery.Enc);
        Assert.Null(delivery.CloseReason);
    }

    [Fact]
    public async Task SendToConnection_DeliversCloseSessionAsCloseReason()
    {
        var routingTable = NewRoutingTable();
        var grainFactory = new FakeGrainFactory();
        const string grainKey = "mcp-session-close";
        var writer = new CallbackServerStreamWriter(grainFactory, grainKey, NullLogger.Instance);
        var conn = SpaceMcpConsumerIdentity.SyntheticConnectionId(grainKey);
        await routingTable.RegisterAgentStreamAsync(conn, writer, CancellationToken.None);

        await routingTable.SendToConnectionAsync(conn, new GatewayToNodeMessage
        {
            CloseSession = new CloseSession { SessionId = "backend-session-2", Reason = "revoked" }
        }, CancellationToken.None);

        var grain = grainFactory.Grains[grainKey];
        var delivery = Assert.Single(grain.Deliveries);
        Assert.Equal("backend-session-2", delivery.BackendSessionId);
        Assert.Empty(delivery.Payload);
        Assert.Equal("revoked", delivery.CloseReason);
    }

    [Fact]
    public async Task GrainCallThrow_DoesNotEvictTheWriter_LegSurvives()
    {
        var routingTable = NewRoutingTable();
        var grainFactory = new FakeGrainFactory();
        const string grainKey = "mcp-session-throw";
        var writer = new CallbackServerStreamWriter(grainFactory, grainKey, NullLogger.Instance);
        var conn = SpaceMcpConsumerIdentity.SyntheticConnectionId(grainKey);
        await routingTable.RegisterAgentStreamAsync(conn, writer, CancellationToken.None);

        // Pre-seed the fake grain and arm it to throw on the FIRST delivery.
        var grain = (FakeAggregatorGrain)grainFactory.GetGrain<ISpaceMcpAggregatorGrain>(grainKey);
        grain.ThrowOnNextDelivery = true;

        // First send: the grain call throws. WriteLocalToConnectionAsync would evict the writer's
        // ConnectionId slot on ANY exception surfacing from WriteAsync — CallbackServerStreamWriter
        // must swallow it instead, so SendToConnectionAsync still reports delivered-to-local-writer
        // (true), not "undeliverable".
        var firstSent = await routingTable.SendToConnectionAsync(conn, new GatewayToNodeMessage
        {
            Frame = new RelayFrame { SessionId = "backend-session-3", Ciphertext = ByteString.CopyFrom("first"u8.ToArray()), Enc = 0 }
        }, CancellationToken.None);

        // Second send: if the writer had been evicted, this would silently no-op (WriteLocalAsync
        // returns null → SendToConnectionAsync falls through to the backplane, which is a no-op
        // NullRelayBackplane — the delivery would be LOST, not merely delayed). Asserting it still
        // reaches the grain proves the leg was never evicted.
        var secondSent = await routingTable.SendToConnectionAsync(conn, new GatewayToNodeMessage
        {
            Frame = new RelayFrame { SessionId = "backend-session-3", Ciphertext = ByteString.CopyFrom("second"u8.ToArray()), Enc = 0 }
        }, CancellationToken.None);

        Assert.True(firstSent);
        Assert.True(secondSent);
        var delivery = Assert.Single(grain.Deliveries); // only the (non-throwing) second call recorded a delivery
        Assert.Equal("second"u8.ToArray(), delivery.Payload);
    }
}
