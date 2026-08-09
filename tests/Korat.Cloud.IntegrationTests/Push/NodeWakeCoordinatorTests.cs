using Korat.Cloud.Push;
using Korat.Domain;
using Korat.Domain.Entities;
using Korat.GrainInterfaces;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Korat.Cloud.IntegrationTests.Push;

/// <summary>
/// Unit tests for <see cref="NodeWakeCoordinator"/>.
/// Tests: eligibility, dedup, startup clamp, and send/wait behaviour.
///
/// Uses <see cref="INodeGrainLocator"/> stubs instead of full IClusterClient mocks
/// — the coordinator only ever calls GetNodeGrain(nodeId).
/// </summary>
public sealed class NodeWakeCoordinatorTests
{
    // ── Helpers ───────────────────────────────────────────────────────────────

    private static Node MakeNode(string? pushToken = "aabbccdd00000000", string? platform = "apns")
        => new()
        {
            Id = new NodeId("test-node-1"),
            SpaceId = new SpaceId("space-1"),
            DisplayName = "test",
            Status = NodeStatus.Offline,
            PushToken = pushToken,
            PushPlatform = platform,
        };

    private static ApnsOptions DefaultOptions(int wakeWait = 12, int dedup = 10) => new()
    {
        KeyId = "TESTKEYID1",
        TeamId = "ABCDE12345",
        BundleId = "dev.korat.node",
        PrivateKeyPem = null,
        WakeWaitSeconds = wakeWait,
        WakeDedupSeconds = dedup,
    };

    private static NodeWakeCoordinator MakeCoordinator(
        IPushWakeSender sender,
        INodeGrainLocator locator,
        ApnsOptions? opts = null) =>
        new(sender, locator, Options.Create(opts ?? DefaultOptions()),
            NullLogger<NodeWakeCoordinator>.Instance);

    // ── Eligibility ───────────────────────────────────────────────────────────

    [Fact]
    public async Task TryWakeAsync_Returns_False_Immediately_When_PushToken_Empty()
    {
        var sender = new RecordingSender(PushWakeResult.Sent);
        var locator = new NeverCalledLocator();
        var coord = MakeCoordinator(sender, locator);
        var node = MakeNode(pushToken: "");

        var result = await coord.TryWakeAsync(node, CancellationToken.None);

        Assert.False(result);
        Assert.Equal(0, sender.SendCount); // zero sends — no added latency
        Assert.Equal(0, locator.GetGrainCallCount); // grain never touched
    }

    [Fact]
    public async Task TryWakeAsync_Returns_False_Immediately_When_PushToken_Null()
    {
        var sender = new RecordingSender(PushWakeResult.Sent);
        var locator = new NeverCalledLocator();
        var coord = MakeCoordinator(sender, locator);
        var node = MakeNode(pushToken: null);

        var result = await coord.TryWakeAsync(node, CancellationToken.None);

        Assert.False(result);
        Assert.Equal(0, sender.SendCount);
        Assert.Equal(0, locator.GetGrainCallCount);
    }

    [Fact]
    public async Task TryWakeAsync_Returns_False_Immediately_When_Sender_NotConfigured()
    {
        var sender = new RecordingSender(PushWakeResult.Sent);
        var locator = new NeverCalledLocator();
        var opts = DefaultOptions();
        opts.KeyId = null; // no KeyId → NullPushWakeSender scenario
        var coord = MakeCoordinator(sender, locator, opts);
        var node = MakeNode(pushToken: "aabbccdd00000000");

        var result = await coord.TryWakeAsync(node, CancellationToken.None);

        Assert.False(result);
        Assert.Equal(0, sender.SendCount); // eligibility fails early — no latency added
        Assert.Equal(0, locator.GetGrainCallCount);
    }

    [Fact]
    public async Task TryWakeAsync_Returns_False_Immediately_For_Fcm_Platform_Token()
    {
        // B1 (031): an fcm-platform token must NEVER be POSTed to APNs — Apple would 400 it and
        // (even with Task 3's compare-and-clear) needlessly burn a token that can never wake
        // anything via APNs.
        var sender = new RecordingSender(PushWakeResult.Sent);
        var locator = new NeverCalledLocator();
        var coord = MakeCoordinator(sender, locator);
        var node = MakeNode(pushToken: "fcmtoken00000000", platform: "fcm");

        var result = await coord.TryWakeAsync(node, CancellationToken.None);

        Assert.False(result);
        Assert.Equal(0, sender.SendCount); // zero sends — the fcm token is never touched
        Assert.Equal(0, locator.GetGrainCallCount);
    }

    // ── IsConfigured property ─────────────────────────────────────────────────

    [Fact]
    public void IsConfigured_True_When_KeyId_Present()
    {
        var sender = new RecordingSender(PushWakeResult.NotConfigured);
        var locator = new NeverCalledLocator();
        var coord = MakeCoordinator(sender, locator, DefaultOptions());

        Assert.True(coord.IsConfigured);
    }

    [Fact]
    public void IsConfigured_False_When_KeyId_Absent()
    {
        var sender = new RecordingSender(PushWakeResult.NotConfigured);
        var locator = new NeverCalledLocator();
        var opts = DefaultOptions();
        opts.KeyId = null;
        var coord = MakeCoordinator(sender, locator, opts);

        Assert.False(coord.IsConfigured);
    }

    // ── Startup clamp ─────────────────────────────────────────────────────────

    [Fact]
    public void Constructor_Clamps_WakeWaitSeconds_When_At_Or_Above_15()
    {
        // WakeWaitSeconds = 20 (above ceiling) must be clamped to 15 and a warning logged.
        var sender = new RecordingSender(PushWakeResult.NotConfigured);
        var locator = new NeverCalledLocator();
        using var logSpy = new LogSpy<NodeWakeCoordinator>();

        var opts = DefaultOptions(wakeWait: 20);
        var coord = new NodeWakeCoordinator(sender, locator, Options.Create(opts), logSpy);

        // Coordinator created without throwing; clamp warning was emitted.
        Assert.True(coord.IsConfigured);
        Assert.True(logSpy.HasWarning);
    }

    [Fact]
    public void Constructor_Does_Not_Clamp_WakeWaitSeconds_Below_15()
    {
        var sender = new RecordingSender(PushWakeResult.NotConfigured);
        var locator = new NeverCalledLocator();
        using var logSpy = new LogSpy<NodeWakeCoordinator>();

        var opts = DefaultOptions(wakeWait: 12);
        var coord = new NodeWakeCoordinator(sender, locator, Options.Create(opts), logSpy);

        Assert.True(coord.IsConfigured);
        Assert.False(logSpy.HasWarning); // no clamp warning for valid value
    }

    [Fact]
    public async Task TryWakeAsync_ExplicitOverride_CanOutliveConfiguredWait()
    {
        var sender = new RecordingSender(PushWakeResult.Sent);
        var locator = new OnlineOnSecondPollLocator();
        var coord = MakeCoordinator(sender, locator, DefaultOptions(wakeWait: 1));

        var woke = await coord.TryWakeAsync(
            MakeNode(), CancellationToken.None, waitOverride: TimeSpan.FromSeconds(3));

        Assert.True(woke);
        Assert.Equal(1, sender.SendCount);
        Assert.True(locator.Grain.GetCallCount >= 2);
    }

    // ── Dedup ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task TryWakeAsync_Sends_Only_Once_Within_DedupWindow()
    {
        // Arrange: sender returns Sent; locator reports Offline forever.
        var sender = new RecordingSender(PushWakeResult.Sent);
        var locator = new AlwaysOfflineLocator();
        // WakeWaitSeconds = 1 so the test completes quickly.
        var opts = new ApnsOptions
        {
            KeyId = "TESTKEYID1",
            TeamId = "ABCDE12345",
            BundleId = "dev.korat.node",
            PrivateKeyPem = null,
            WakeWaitSeconds = 1,   // short wait for test speed
            WakeDedupSeconds = 10, // 10 s dedup window
        };
        var coord = MakeCoordinator(sender, locator, opts);
        var node = MakeNode();

        // First call — should send.
        await coord.TryWakeAsync(node, CancellationToken.None);
        var firstSendCount = sender.SendCount;

        // Second call immediately — within dedup window — should NOT send again.
        await coord.TryWakeAsync(node, CancellationToken.None);
        var secondSendCount = sender.SendCount;

        Assert.Equal(1, firstSendCount);   // first call sent
        Assert.Equal(1, secondSendCount);  // second call deduped — count unchanged
    }

    // ── TokenInvalid retrofit (031: compare-and-clear, not unconditional clear) ────────────────

    [Fact]
    public async Task TryWakeAsync_TokenInvalid_Uses_CompareAndClear_Not_UnconditionalClear()
    {
        var sender = new RecordingSender(PushWakeResult.TokenInvalid);
        var locator = new RecordingClearLocator();
        var opts = new ApnsOptions
        {
            KeyId = "TESTKEYID1", TeamId = "ABCDE12345", BundleId = "dev.korat.node",
            WakeWaitSeconds = 1, WakeDedupSeconds = 10,
        };
        var coord = MakeCoordinator(sender, locator, opts);
        var node = MakeNode(pushToken: "deadtoken00000000");

        await coord.TryWakeAsync(node, CancellationToken.None);
        // The clear runs fire-and-forget on Task.Run — poll briefly for it to land.
        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (locator.Grain.ClearCallCount == 0 && DateTime.UtcNow < deadline)
            await Task.Delay(10);

        Assert.Equal(1, locator.Grain.ClearCallCount);
        Assert.Equal("deadtoken00000000", locator.Grain.LastDeadToken);
        Assert.Equal(0, locator.Grain.RegisterPushTokenCallCount); // must NOT use the old unconditional clear
    }

    private sealed class RecordingClearNodeGrain : INodeGrain
    {
        public int ClearCallCount { get; private set; }
        public string? LastDeadToken { get; private set; }
        public int RegisterPushTokenCallCount { get; private set; }

        public Task<Node> ConnectAsync(SpaceId spaceId, string displayName, GatewayId gatewayId, NodeKind kind = NodeKind.Publisher, IReadOnlyList<string>? capabilities = null, string? hostname = null, string? os = null, string? arch = null, string? cliVersion = null) => throw new NotSupportedException();
        public Task<bool> HasCapabilityAsync(string capability) => Task.FromResult(false);
        public Task HeartbeatAsync(GatewayId gatewayId) => throw new NotSupportedException();
        public Task MarkOfflineAsync() => throw new NotSupportedException();
        public Task<Node> GetAsync() => Task.FromResult(new Node
        {
            Id = new NodeId("test-node-1"),
            SpaceId = new SpaceId("space-1"),
            Status = NodeStatus.Offline,
        });
        public Task<Node> MarkOnlineForTestingAsync(SpaceId spaceId, string displayName) => throw new NotSupportedException();
        public Task RegisterPushTokenAsync(string token, string platform)
        {
            RegisterPushTokenCallCount++;
            return Task.CompletedTask;
        }
        public Task<Node> SetNoteAsync(string? note) => throw new NotSupportedException();
        public Task RemoveAsync() => throw new NotSupportedException();
        public Task ClearPushTokenIfMatchesAsync(string deadToken)
        {
            ClearCallCount++;
            LastDeadToken = deadToken;
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingClearLocator : INodeGrainLocator
    {
        public RecordingClearNodeGrain Grain { get; } = new();
        public INodeGrain GetNodeGrain(string nodeId) => Grain;
    }

    // ── Test doubles ──────────────────────────────────────────────────────────

    /// <summary>Records send calls and returns the configured result.</summary>
    private sealed class RecordingSender(PushWakeResult result) : IPushWakeSender
    {
        public int SendCount { get; private set; }

        public Task<PushWakeResult> SendWakeAsync(string token, string platform, CancellationToken ct)
        {
            SendCount++;
            return Task.FromResult(result);
        }
    }

    /// <summary>
    /// Grain locator stub: fails if any method is called — for eligibility tests where the
    /// grain must never be reached.
    /// </summary>
    private sealed class NeverCalledLocator : INodeGrainLocator
    {
        public int GetGrainCallCount { get; private set; }

        public INodeGrain GetNodeGrain(string nodeId)
        {
            GetGrainCallCount++;
            throw new InvalidOperationException(
                $"NodeGrain should not be reached in this test (nodeId={nodeId}).");
        }
    }

    /// <summary>
    /// Grain locator stub: returns a stub <see cref="INodeGrain"/> that always reports Offline.
    /// Used for dedup and timeout tests.
    /// </summary>
    private sealed class AlwaysOfflineLocator : INodeGrainLocator
    {
        public INodeGrain GetNodeGrain(string nodeId) => new AlwaysOfflineNodeGrain();
    }

    private sealed class AlwaysOfflineNodeGrain : INodeGrain
    {
        public Task<Node> ConnectAsync(SpaceId spaceId, string displayName, GatewayId gatewayId, NodeKind kind = NodeKind.Publisher, IReadOnlyList<string>? capabilities = null, string? hostname = null, string? os = null, string? arch = null, string? cliVersion = null) => throw new NotSupportedException();
        public Task<bool> HasCapabilityAsync(string capability) => Task.FromResult(false);
        public Task HeartbeatAsync(GatewayId gatewayId) => throw new NotSupportedException();
        public Task MarkOfflineAsync() => throw new NotSupportedException();
        public Task<Node> GetAsync() => Task.FromResult(new Node
        {
            Id = new NodeId("test-node-1"),
            SpaceId = new SpaceId("space-1"),
            Status = NodeStatus.Offline,
        });
        public Task<Node> MarkOnlineForTestingAsync(SpaceId spaceId, string displayName) => throw new NotSupportedException();
        public Task RegisterPushTokenAsync(string token, string platform) => Task.CompletedTask;
        public Task<Node> SetNoteAsync(string? note) => throw new NotSupportedException();
        public Task RemoveAsync() => throw new NotSupportedException();
        public Task ClearPushTokenIfMatchesAsync(string deadToken) => Task.CompletedTask;
    }

    private sealed class OnlineOnSecondPollLocator : INodeGrainLocator
    {
        public OnlineOnSecondPollNodeGrain Grain { get; } = new();
        public INodeGrain GetNodeGrain(string nodeId) => Grain;
    }

    private sealed class OnlineOnSecondPollNodeGrain : INodeGrain
    {
        public int GetCallCount { get; private set; }

        public Task<Node> GetAsync()
        {
            GetCallCount++;
            return Task.FromResult(new Node
            {
                Id = new NodeId("test-node-1"),
                SpaceId = new SpaceId("space-1"),
                Status = GetCallCount >= 2 ? NodeStatus.Online : NodeStatus.Offline,
            });
        }

        public Task<Node> ConnectAsync(SpaceId spaceId, string displayName, GatewayId gatewayId, NodeKind kind = NodeKind.Publisher, IReadOnlyList<string>? capabilities = null, string? hostname = null, string? os = null, string? arch = null, string? cliVersion = null) => throw new NotSupportedException();
        public Task<bool> HasCapabilityAsync(string capability) => Task.FromResult(false);
        public Task HeartbeatAsync(GatewayId gatewayId) => throw new NotSupportedException();
        public Task MarkOfflineAsync() => throw new NotSupportedException();
        public Task<Node> MarkOnlineForTestingAsync(SpaceId spaceId, string displayName) => throw new NotSupportedException();
        public Task RegisterPushTokenAsync(string token, string platform) => Task.CompletedTask;
        public Task<Node> SetNoteAsync(string? note) => throw new NotSupportedException();
        public Task RemoveAsync() => throw new NotSupportedException();
        public Task ClearPushTokenIfMatchesAsync(string deadToken) => Task.CompletedTask;
    }

    /// <summary>
    /// Simple logger spy that captures whether any Warning was emitted.
    /// </summary>
    private sealed class LogSpy<T> : Microsoft.Extensions.Logging.ILogger<T>, IDisposable
    {
        public bool HasWarning { get; private set; }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => this;
        public void Dispose() { }

        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

        public void Log<TState>(
            Microsoft.Extensions.Logging.LogLevel logLevel,
            Microsoft.Extensions.Logging.EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel >= Microsoft.Extensions.Logging.LogLevel.Warning)
                HasWarning = true;
        }
    }
}
