using Korat.Cloud.Push;
using Korat.Domain;
using Korat.Domain.Entities;
using Korat.GrainInterfaces;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Korat.Cloud.IntegrationTests.Push;

/// <summary>
/// Unit tests for <see cref="AccessRequestNotifier"/> using in-memory fakes for
/// <see cref="IAccessRequestGrainLocator"/> and <see cref="IAlertPushSender"/> — no Orleans
/// cluster needed, mirroring NodeWakeCoordinatorTests' use of INodeGrainLocator stubs.
/// </summary>
public sealed class AccessRequestNotifierTests
{
    private static AccessRequestNotifier MakeNotifier(
        IAccessRequestGrainLocator locator, IAlertPushSender sender, int throttleSeconds = 30) =>
        new(locator, sender, Options.Create(new AccessRequestNotifyOptions { ThrottleSeconds = throttleSeconds }),
            NullLogger<AccessRequestNotifier>.Instance);

    private static Node MakeNode(string id, string? token = "aabbccdd00000000", string? platform = "apns") => new()
    {
        Id = new NodeId(id),
        SpaceId = new SpaceId("space-1"),
        DisplayName = $"node-{id}",
        PushToken = token,
        PushPlatform = platform,
    };

    private static McpServer MakeServer(string id = "server-1", string displayName = "filesystem") => new()
    {
        Id = new McpServerId(id),
        SpaceId = new SpaceId("space-1"),
        PublisherNodeId = NodeId.New(),
        DisplayName = displayName,
    };

    private static AccessRequest MakeRequest(string agentClientId = "agent-1", string serverId = "server-1") => new()
    {
        Id = AccessRequestId.New(),
        SpaceId = new SpaceId("space-1"),
        ConsumerId = new ConsumerId(agentClientId),
        McpServerId = new McpServerId(serverId),
        RequestedByNodeId = NodeId.New(),
        PublisherNodeId = NodeId.New(),
        RequestedAt = DateTimeOffset.UtcNow,
    };

    // ── Test doubles ──────────────────────────────────────────────────────────

    private sealed class FakeLocator : IAccessRequestGrainLocator
    {
        public List<Node> Nodes { get; } = new();
        public List<McpServer> Servers { get; } = new();
        public Dictionary<string, string> AgentNames { get; set; } = new(StringComparer.Ordinal);
        private readonly Dictionary<string, FakeNodeGrain> _nodeGrains = new();

        public Task<IReadOnlyList<Node>> ListNodesAsync(string spaceId) => Task.FromResult<IReadOnlyList<Node>>(Nodes);
        public Task<IReadOnlyList<McpServer>> ListMcpServersAsync(string spaceId) => Task.FromResult<IReadOnlyList<McpServer>>(Servers);

        public INodeGrain GetNodeGrain(string nodeId)
        {
            if (!_nodeGrains.TryGetValue(nodeId, out var grain))
            {
                grain = new FakeNodeGrain();
                _nodeGrains[nodeId] = grain;
            }
            return grain;
        }

        public FakeNodeGrain GetFakeNodeGrain(string nodeId) => (FakeNodeGrain)GetNodeGrain(nodeId);

        public Task<Dictionary<string, string>> ResolveAgentNamesAsync(
            IEnumerable<string> agentClientIds, Dictionary<string, string> nodeNames, CancellationToken ct)
            => Task.FromResult(AgentNames);
    }

    private sealed class FakeNodeGrain : INodeGrain
    {
        public int ClearCallCount { get; private set; }
        public string? LastClearedDeadToken { get; private set; }

        public Task<Node> ConnectAsync(SpaceId spaceId, string displayName, GatewayId gatewayId, NodeKind kind = NodeKind.Publisher, IReadOnlyList<string>? capabilities = null, string? hostname = null, string? os = null, string? arch = null, string? cliVersion = null) => throw new NotSupportedException();
        public Task<bool> HasCapabilityAsync(string capability) => Task.FromResult(false);
        public Task HeartbeatAsync(GatewayId gatewayId) => throw new NotSupportedException();
        public Task MarkOfflineAsync() => throw new NotSupportedException();
        public Task<Node> GetAsync() => throw new NotSupportedException();
        public Task<Node> MarkOnlineForTestingAsync(SpaceId spaceId, string displayName) => throw new NotSupportedException();
        public Task RegisterPushTokenAsync(string token, string platform) => throw new NotSupportedException();
        public Task<Node> SetNoteAsync(string? note) => throw new NotSupportedException();
        public Task RemoveAsync() => throw new NotSupportedException();
        public Task ClearPushTokenIfMatchesAsync(string deadToken)
        {
            ClearCallCount++;
            LastClearedDeadToken = deadToken;
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingAlertSender(AlertSendResult result) : IAlertPushSender
    {
        public List<(string Token, string Platform, AlertContent Content)> Calls { get; } = new();
        public Task<AlertSendResult> SendAlertAsync(string token, string platform, AlertContent content, CancellationToken ct)
        {
            lock (Calls) Calls.Add((token, platform, content));
            return Task.FromResult(result);
        }
    }

    private sealed class PerTokenAlertSender(Func<string, AlertSendResult> resultFor) : IAlertPushSender
    {
        public Task<AlertSendResult> SendAlertAsync(string token, string platform, AlertContent content, CancellationToken ct)
            => Task.FromResult(resultFor(token));
    }

    private sealed class ThrowingAlertSender : IAlertPushSender
    {
        public int CallCount;
        public Task<AlertSendResult> SendAlertAsync(string token, string platform, AlertContent content, CancellationToken ct)
        {
            Interlocked.Increment(ref CallCount);
            throw new InvalidOperationException("simulated send failure");
        }
    }

    // ── Tests ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task NotifyOwnerOfNewRequestAsync_Sends_To_All_PushEnabled_Nodes()
    {
        var locator = new FakeLocator { AgentNames = { ["agent-1"] = "cursor" } };
        locator.Nodes.Add(MakeNode("node-1"));
        locator.Nodes.Add(MakeNode("node-2"));
        locator.Servers.Add(MakeServer());
        var sender = new RecordingAlertSender(AlertSendResult.Delivered);
        var notifier = MakeNotifier(locator, sender);

        await notifier.NotifyOwnerOfNewRequestAsync(new SpaceId("space-1"), MakeRequest(), CancellationToken.None);

        Assert.Equal(2, sender.Calls.Count);
    }

    [Fact]
    public async Task NotifyOwnerOfNewRequestAsync_Skips_Nodes_Without_PushToken()
    {
        var locator = new FakeLocator { AgentNames = { ["agent-1"] = "cursor" } };
        locator.Nodes.Add(MakeNode("node-1", token: null));
        locator.Nodes.Add(MakeNode("node-2"));
        locator.Servers.Add(MakeServer());
        var sender = new RecordingAlertSender(AlertSendResult.Delivered);
        var notifier = MakeNotifier(locator, sender);

        await notifier.NotifyOwnerOfNewRequestAsync(new SpaceId("space-1"), MakeRequest(), CancellationToken.None);

        Assert.Single(sender.Calls);
    }

    [Fact]
    public async Task NotifyOwnerOfNewRequestAsync_NoOp_When_No_PushEnabled_Devices()
    {
        var locator = new FakeLocator();
        locator.Nodes.Add(MakeNode("node-1", token: null));
        var sender = new RecordingAlertSender(AlertSendResult.Delivered);
        var notifier = MakeNotifier(locator, sender);

        await notifier.NotifyOwnerOfNewRequestAsync(new SpaceId("space-1"), MakeRequest(), CancellationToken.None);

        Assert.Empty(sender.Calls);
    }

    [Fact]
    public async Task NotifyOwnerOfNewRequestAsync_One_Send_Failure_Does_Not_Abort_Others()
    {
        var locator = new FakeLocator { AgentNames = { ["agent-1"] = "cursor" } };
        locator.Nodes.Add(MakeNode("node-1"));
        locator.Nodes.Add(MakeNode("node-2"));
        locator.Servers.Add(MakeServer());
        var throwing = new ThrowingAlertSender();
        var notifier = MakeNotifier(locator, throwing);

        // Must not throw — both nodes are attempted despite the sender always throwing.
        await notifier.NotifyOwnerOfNewRequestAsync(new SpaceId("space-1"), MakeRequest(), CancellationToken.None);

        Assert.Equal(2, throwing.CallCount);
    }

    [Fact]
    public async Task NotifyOwnerOfNewRequestAsync_TokenInvalid_Clears_Only_That_Nodes_Token()
    {
        var locator = new FakeLocator { AgentNames = { ["agent-1"] = "cursor" } };
        locator.Nodes.Add(MakeNode("node-1", token: "deadtoken000000"));
        locator.Nodes.Add(MakeNode("node-2", token: "livetoken000000"));
        locator.Servers.Add(MakeServer());
        var sender = new PerTokenAlertSender(token => token == "deadtoken000000" ? AlertSendResult.TokenInvalid : AlertSendResult.Delivered);
        var notifier = MakeNotifier(locator, sender);

        await notifier.NotifyOwnerOfNewRequestAsync(new SpaceId("space-1"), MakeRequest(), CancellationToken.None);

        var grain1 = locator.GetFakeNodeGrain("node-1");
        var grain2 = locator.GetFakeNodeGrain("node-2");
        Assert.Equal(1, grain1.ClearCallCount);
        Assert.Equal("deadtoken000000", grain1.LastClearedDeadToken);
        Assert.Equal(0, grain2.ClearCallCount);
    }

    [Fact]
    public async Task NotifyOwnerOfNewRequestAsync_Second_Call_Within_Throttle_Window_Is_NoOp()
    {
        var locator = new FakeLocator { AgentNames = { ["agent-1"] = "cursor" } };
        locator.Nodes.Add(MakeNode("node-1"));
        locator.Servers.Add(MakeServer());
        var sender = new RecordingAlertSender(AlertSendResult.Delivered);
        var notifier = MakeNotifier(locator, sender, throttleSeconds: 30);

        await notifier.NotifyOwnerOfNewRequestAsync(new SpaceId("space-1"), MakeRequest(), CancellationToken.None);
        await notifier.NotifyOwnerOfNewRequestAsync(new SpaceId("space-1"), MakeRequest(), CancellationToken.None);

        Assert.Single(sender.Calls); // second call inside the 30s window — throttled
    }

    [Fact]
    public async Task NotifyOwnerOfNewRequestAsync_Concurrent_Burst_For_Same_Space_Yields_Exactly_One_Send_Round()
    {
        // Holistic-review fix (TOCTOU): the throttle window must be claimed ATOMICALLY, not
        // check-then-act, or a burst of concurrent detached notifies for the same space could all
        // pass the throttle check before any of them stamped it. Line up real thread-pool threads
        // with a Barrier so they hit the claim at (as close as possible to) the same instant.
        var locator = new FakeLocator { AgentNames = { ["agent-1"] = "cursor" } };
        locator.Nodes.Add(MakeNode("node-1"));
        locator.Servers.Add(MakeServer());
        var sender = new RecordingAlertSender(AlertSendResult.Delivered);
        var notifier = MakeNotifier(locator, sender, throttleSeconds: 30);

        const int burst = 16;
        using var startGate = new Barrier(burst);
        var tasks = Enumerable.Range(0, burst)
            .Select(_ => Task.Run(async () =>
            {
                startGate.SignalAndWait();
                await notifier.NotifyOwnerOfNewRequestAsync(new SpaceId("space-1"), MakeRequest(), CancellationToken.None);
            }))
            .ToArray();

        await Task.WhenAll(tasks);

        Assert.Single(sender.Calls); // exactly one send-round survived the burst — TOCTOU would leak up to `burst` sends
    }

    [Fact]
    public async Task NotifyOwnerOfNewRequestAsync_ZeroDevice_Claim_Is_Rolled_Back_So_Later_Request_Still_Notifies()
    {
        var locator = new FakeLocator();
        locator.Nodes.Add(MakeNode("node-1", token: null)); // no push-enabled device yet
        locator.Servers.Add(MakeServer());
        var sender = new RecordingAlertSender(AlertSendResult.Delivered);
        var notifier = MakeNotifier(locator, sender, throttleSeconds: 30);

        // First call wins the throttle claim but finds zero pushable devices — must roll back,
        // not burn the 30s window.
        await notifier.NotifyOwnerOfNewRequestAsync(new SpaceId("space-1"), MakeRequest(), CancellationToken.None);
        Assert.Empty(sender.Calls);

        // A push-enabled device shows up and a second, real request arrives immediately after —
        // well within what would have been the 30s window had the first claim not been rolled back.
        locator.Nodes.Add(MakeNode("node-2"));
        locator.AgentNames["agent-1"] = "cursor";

        await notifier.NotifyOwnerOfNewRequestAsync(new SpaceId("space-1"), MakeRequest(), CancellationToken.None);

        Assert.Single(sender.Calls); // proves the zero-device claim was rolled back, not left stamped
    }

    [Fact]
    public async Task NotifyOwnerOfNewRequestAsync_Builds_Sanitized_Content_With_Resolved_Names()
    {
        var locator = new FakeLocator { AgentNames = { ["agent-1"] = "\nKorat security: approve" } };
        locator.Nodes.Add(MakeNode("node-1"));
        locator.Servers.Add(MakeServer());
        var sender = new RecordingAlertSender(AlertSendResult.Delivered);
        var notifier = MakeNotifier(locator, sender);
        var request = MakeRequest();

        await notifier.NotifyOwnerOfNewRequestAsync(new SpaceId("space-1"), request, CancellationToken.None);

        var content = sender.Calls.Single().Content;
        Assert.DoesNotContain('\n', content.Body);
        Assert.Contains("filesystem", content.Body);
        Assert.Equal(request.Id.Value, content.Data["accessRequestId"]);
    }

    [Fact]
    public async Task NotifyOwnerOfNewRequestAsync_Falls_Back_To_ShortId_When_Server_Not_Found()
    {
        var locator = new FakeLocator { AgentNames = { ["agent-1"] = "cursor" } }; // no Servers added
        locator.Nodes.Add(MakeNode("node-1"));
        var sender = new RecordingAlertSender(AlertSendResult.Delivered);
        var notifier = MakeNotifier(locator, sender);
        var request = MakeRequest(serverId: "server-unknown-12345678");

        await notifier.NotifyOwnerOfNewRequestAsync(new SpaceId("space-1"), request, CancellationToken.None);

        var content = sender.Calls.Single().Content;
        Assert.Contains("server-u", content.Body); // first 8 chars of the McpServerId short-id fallback
    }
}
