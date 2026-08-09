using Google.Protobuf;
using Grpc.Core;
using Korat.Cloud;
using Korat.Cloud.Gateways;
using Korat.Cloud.Observability;
using Korat.Domain;
using Korat.Domain.Entities;
using Korat.GrainInterfaces;
using Korat.Relay.V1;
using Microsoft.Extensions.Logging.Abstractions;
using NATS.Client.Core;
using Testcontainers.Nats;

namespace Korat.Auth.Tests;

/// <summary>
/// 009-nats-relay-backplane: end-to-end proof that the relay is machine-independent. Spins a
/// REAL NATS server (Testcontainers) and wires TWO independent <see cref="SessionRoutingTable"/>
/// instances over their own NATS connections — i.e. two machines. A frame submitted on machine A
/// (where only the agent is connected) must reach the publisher's stream on machine B.
///
/// Skips when Docker is unavailable (CI without a Docker daemon).
/// </summary>
public class NatsRelayBackplaneIntegrationTests
{
    private static readonly NodeId Agent = new("agent-node");
    private static readonly NodeId Publisher = new("publisher-node");
    private static readonly SessionId RelaySession = new("sess-x");
    private static readonly McpServerId Server = new("srv-x");
    private static readonly SpaceId Space = new("space-x");

    [SkippableFact]
    public async Task Frame_CrossesMachines_ViaNats()
    {
        NatsContainer? nats = null;
        try
        {
            try
            {
                nats = new NatsBuilder().Build();
                await nats.StartAsync();
            }
            catch (Exception ex)
            {
                throw new SkipException($"Docker/NATS container unavailable: {ex.GetType().Name}");
            }

            var connectionString = nats.GetConnectionString();

            // Two machines = two independent NATS connections to the same server.
            await using var connA = new NatsConnection(NatsUrl.ToOpts(connectionString, "machine-a"));
            await using var connB = new NatsConnection(NatsUrl.ToOpts(connectionString, "machine-b"));

            // Shared control plane: both machines resolve the same session topology.
            var resolver = new StubResolver { Route = new SessionRouteInfo(Agent, Publisher, Server, Space) };

            var tableA = new SessionRoutingTable(
                new NatsRelayBackplane(connA, NullLogger<NatsRelayBackplane>.Instance),
                resolver, NoopInspector(), _ => NoopSessionGrain(),
                _ => throw new NotSupportedException("not exercised by this test"),
                NullLogger<SessionRoutingTable>.Instance);
            var tableB = new SessionRoutingTable(
                new NatsRelayBackplane(connB, NullLogger<NatsRelayBackplane>.Instance),
                resolver, NoopInspector(), _ => NoopSessionGrain(),
                _ => throw new NotSupportedException("not exercised by this test"),
                NullLogger<SessionRoutingTable>.Instance);

            // Agent is connected to machine A; publisher to machine B.
            var publisherWriter = new RecordingWriter();
            var epochAgent = await tableA.RegisterStreamAsync(Agent, new RecordingWriter(), CancellationToken.None);
            // RegisterStreamAsync awaits SubscribeCoreAsync, so B's subscription is established
            // on the server before this returns — no pre-publish settle delay needed.
            var epochPublisher = await tableB.RegisterStreamAsync(Publisher, publisherWriter, CancellationToken.None);

            var frame = new RelayFrame
            {
                SessionId = RelaySession.Value,
                Direction = "client_to_server",
                Ciphertext = ByteString.CopyFromUtf8("hello-across-machines"),
            };

            var delivered = await tableA.ForwardFrameAsync(Agent, frame, CancellationToken.None);
            Assert.True(delivered, "ForwardFrameAsync should report delivery (published to backplane).");

            // NATS delivery is async — poll the publisher's stream on machine B.
            var arrived = await WaitUntilAsync(() => publisherWriter.Written.Count > 0, TimeSpan.FromSeconds(10));
            Assert.True(arrived, "Frame did not cross machines via NATS within the timeout.");

            var received = Assert.Single(publisherWriter.Written);
            Assert.Equal(GatewayToNodeMessage.PayloadOneofCase.Frame, received.PayloadCase);
            Assert.Equal("hello-across-machines", received.Frame.Ciphertext.ToStringUtf8());

            await tableA.UnregisterStreamAsync(Agent, epochAgent);
            await tableB.UnregisterStreamAsync(Publisher, epochPublisher);
        }
        finally
        {
            if (nats is not null)
                await nats.DisposeAsync();
        }
    }

    private static async Task<bool> WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
                return true;
            await Task.Delay(50);
        }
        return condition();
    }

    private static ISessionGrain NoopSessionGrain() => new NoopSessionGrainImpl();

    private sealed class NoopSessionGrainImpl : ISessionGrain
    {
        public Task<RelaySession> OpenAsync(GrantId g, ConsumerId a, McpServerId m, NodeId c, NodeId p, GatewayId h, SpaceId s, ConnectionId connId = default) => throw new NotSupportedException();
        public Task RecordBytesAsync(long c2s, long s2c) => Task.CompletedTask;
        public Task CloseAsync(SessionCloseReason r) => Task.CompletedTask;
        public Task RevokeAsync() => Task.CompletedTask;
        public Task<RelaySession> GetAsync() => throw new NotSupportedException();
    }

    private static McpToolCallInspector NoopInspector()
        => new(new NoopSink(), NullLogger<McpToolCallInspector>.Instance);

    private sealed class NoopSink : IMcpToolCallSink
    {
        public void Record(in ToolCallEvent toolCall) { }
    }

    private sealed class StubResolver : ISessionRouteResolver
    {
        public SessionRouteInfo? Route;
        public Task<SessionRouteInfo?> ResolveAsync(SessionId sessionId, CancellationToken cancellationToken)
            => Task.FromResult(Route);
    }

    private sealed class RecordingWriter : IAsyncStreamWriter<GatewayToNodeMessage>
    {
        private readonly List<GatewayToNodeMessage> _written = [];
        public IReadOnlyList<GatewayToNodeMessage> Written
        {
            get { lock (_written) return _written.ToList(); }
        }
        public WriteOptions? WriteOptions { get; set; }
        public Task WriteAsync(GatewayToNodeMessage message) => WriteAsync(message, CancellationToken.None);
        public Task WriteAsync(GatewayToNodeMessage message, CancellationToken cancellationToken)
        {
            lock (_written) _written.Add(message);
            return Task.CompletedTask;
        }
    }
}
