using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Google.Protobuf;
using Korat.Cli.Mcp.Aggregation;
using Korat.Relay.V1;
using Xunit;
using Korat.Mcp;

public class SpaceWatcherTests
{
    [Fact]
    public void Diff_detects_new_granted_and_removed_ungranted()
    {
        var prev = new SpaceSnapshot(
            new[] { new ServerDescriptor("s1","GitHub",true) },
            new[] { new ServerDescriptor("s2","Postgres",true) });
        var cur = new SpaceSnapshot(
            new[] { new ServerDescriptor("s1","GitHub",true), new ServerDescriptor("s2","Postgres",true) },
            Array.Empty<ServerDescriptor>());

        var diff = SpaceWatcher.ComputeDiff(prev, cur);
        Assert.Contains("s2", diff.GrantedAdded.Select(s => s.Id));
        Assert.Contains("s2", diff.UngrantedRemoved.Select(s => s.Id));
        Assert.True(diff.HasChanges);
    }

    [Fact]
    public void Diff_no_changes_when_snapshots_equal()
    {
        var snap = new SpaceSnapshot(
            new[] { new ServerDescriptor("s1","GitHub",true) },
            new[] { new ServerDescriptor("s2","Postgres",true) });
        var diff = SpaceWatcher.ComputeDiff(snap, snap);
        Assert.False(diff.HasChanges);
    }

    [Fact]
    public void Diff_detects_removed_granted_and_added_ungranted()
    {
        var prev = new SpaceSnapshot(new[]{ new ServerDescriptor("s1","GitHub",true) }, Array.Empty<ServerDescriptor>());
        var cur  = new SpaceSnapshot(Array.Empty<ServerDescriptor>(), new[]{ new ServerDescriptor("s1","GitHub",true) });
        var diff = SpaceWatcher.ComputeDiff(prev, cur);
        Assert.Contains("s1", diff.GrantedRemoved.Select(s=>s.Id));
        Assert.Contains("s1", diff.UngrantedAdded.Select(s=>s.Id));
    }

    // Minimal fake gateway that grants sessions + auto-replies to JSON-RPC requests
    // (so OpenAsync's initialize+tools/list complete). Replicated locally from BackendSessionManagerTests.
    private sealed class FakeGatewayConnection : IGatewayConnection
    {
        private readonly Channel<GatewayToNodeMessage> _in = Channel.CreateUnbounded<GatewayToNodeMessage>();
        public ChannelReader<GatewayToNodeMessage> IncomingMessages => _in.Reader;

        /// <summary>
        /// Server IDs in this set will receive an AccessDenied reply on the next open attempt,
        /// and are removed from the set so subsequent attempts succeed.
        /// </summary>
        public HashSet<string> FailOpenOnce { get; } = new();

        public Task SendRequestSessionAsync(string requestId, string agentClientId, string mcpServerId, CancellationToken ct = default)
        {
            if (FailOpenOnce.Remove(mcpServerId))
            {
                _in.Writer.TryWrite(new GatewayToNodeMessage { AccessDenied = new AccessDenied { RequestId = requestId, Reason = "offline" } });
            }
            else
            {
                _in.Writer.TryWrite(new GatewayToNodeMessage { SessionOpened = new SessionOpened { RequestId = requestId, SessionId = $"sess-{mcpServerId}" } });
            }
            return Task.CompletedTask;
        }
        public Task SendFrameAsync(string sessionId, ReadOnlyMemory<byte> ciphertext, ulong seq, string direction, CancellationToken ct = default)
        {
            var text = Encoding.UTF8.GetString(ciphertext.Span).TrimEnd('\n');
            var node = JsonNode.Parse(text)!.AsObject();
            if (node.TryGetPropertyValue("id", out var idNode) && idNode is not null && node.TryGetPropertyValue("method", out var mNode))
            {
                var method = mNode!.GetValue<string>();
                JsonObject result = method == "tools/list"
                    ? new JsonObject { ["tools"] = new JsonArray(new JsonObject { ["name"]="t", ["description"]="d", ["inputSchema"]=new JsonObject{["type"]="object"} }) }
                    : new JsonObject { ["ok"] = true };
                var reply = new JsonObject { ["jsonrpc"]="2.0", ["id"]=idNode.DeepClone(), ["result"]=result };
                _in.Writer.TryWrite(new GatewayToNodeMessage { Frame = new RelayFrame { SessionId = sessionId, Ciphertext = ByteString.CopyFrom(Encoding.UTF8.GetBytes(reply.ToJsonString()+"\n")) } });
            }
            return Task.CompletedTask;
        }
        public Task SendHeartbeatAsync(CancellationToken ct = default) => Task.CompletedTask;

        // 031 (MAJOR-3): E2E stubs — reply E2eNotSupported so handshake falls back to plaintext.
        public Task SendE2eFrameAsync(string sessionId, ReadOnlyMemory<byte> wirePayload, ulong sequenceNumber, string direction, Korat.Relay.V1.FrameMetadata meta, CancellationToken ct = default)
            => Task.CompletedTask;
        public Task SendE2eKeyOfferAsync(string sessionId, uint version, string curve, byte[] pubKey, byte[] salt, CancellationToken ct = default)
        {
            _in.Writer.TryWrite(new GatewayToNodeMessage
            {
                E2ENotSupported = new E2eNotSupported { SessionId = sessionId, Reason = "test-spacewatcher" }
            });
            return Task.CompletedTask;
        }
        public Task SendE2eKeyConfirmAsync(string sessionId, byte[] confirmTag, CancellationToken ct = default)
            => Task.CompletedTask;
        public Task SendCloseSessionAsync(string sessionId, string reason, CancellationToken ct = default)
            => Task.CompletedTask;
    }

    [Fact]
    public async Task Reconcile_opens_new_granted_updates_catalog_and_signals_change()
    {
        var fake = new FakeGatewayConnection();
        await using var mgr = new BackendSessionManager(fake, "ag1");
        var catalog = new AggregateCatalog();
        var changes = 0;

        var baseline = new SpaceSnapshot(Array.Empty<ServerDescriptor>(), Array.Empty<ServerDescriptor>());
        var watcher = new SpaceWatcher(
            discover: _ => Task.FromResult(baseline),  // unused in this direct ReconcileAsync test
            sessions: mgr, catalog: catalog,
            onChanged: _ => { changes++; return Task.CompletedTask; },
            baseline: baseline);

        var cur = new SpaceSnapshot(
            new[] { new ServerDescriptor("s1","GitHub",true) },
            new[] { new ServerDescriptor("s2","Postgres",true) });

        var changed = await watcher.ReconcileAsync(cur, default).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(changed);
        Assert.Equal(1, changes);
        var tools = JsonNode.Parse(catalog.ToolsListJson())!["tools"]!.AsArray();
        Assert.Contains(tools, t => t!["name"]!.GetValue<string>() == "github__t");          // granted, namespaced
        Assert.Contains(tools, t => t!["name"]!.GetValue<string>() == "request-access__postgres"); // ungranted
    }

    [Fact]
    public async Task Reconcile_no_change_does_not_signal()
    {
        var fake = new FakeGatewayConnection();
        await using var mgr = new BackendSessionManager(fake, "ag1");
        var catalog = new AggregateCatalog();
        var changes = 0;
        var snap = new SpaceSnapshot(new[]{ new ServerDescriptor("s1","GitHub",true) }, Array.Empty<ServerDescriptor>());
        var watcher = new SpaceWatcher(_ => Task.FromResult(snap), mgr, catalog,
            _ => { changes++; return Task.CompletedTask; }, baseline: snap);

        var changed = await watcher.ReconcileAsync(snap, default).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(changed);
        Assert.Equal(0, changes);
    }

    [Fact]
    public async Task Reconcile_revoked_granted_server_removes_its_tools_and_signals()
    {
        var fake = new FakeGatewayConnection();
        await using var mgr = new BackendSessionManager(fake, "ag1");
        var catalog = new AggregateCatalog();
        var changes = 0;

        var s1 = new ServerDescriptor("s1", "GitHub", true);
        // Seed: open s1 through the manager + catalog so baseline reflects an actually-open server.
        var slug = ToolNamespacer.Slug(s1.DisplayName, s1.Id);
        var tools = await mgr.OpenAsync(s1, slug, default).WaitAsync(TimeSpan.FromSeconds(5));
        catalog.SetGranted(s1.Id, slug, s1.DisplayName, tools);
        var baseline = new SpaceSnapshot(new[] { s1 }, Array.Empty<ServerDescriptor>());

        var watcher = new SpaceWatcher(_ => Task.FromResult(baseline), mgr, catalog,
            _ => { changes++; return Task.CompletedTask; }, baseline: baseline);

        // Revoke: s1 no longer appears in the new snapshot at all.
        var cur = new SpaceSnapshot(Array.Empty<ServerDescriptor>(), Array.Empty<ServerDescriptor>());
        var changed = await watcher.ReconcileAsync(cur, default).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(changed);
        Assert.Equal(1, changes);
        // submit_feedback is always present; only the granted server tools should be gone.
        var toolsAfterRevoke = JsonNode.Parse(catalog.ToolsListJson())!["tools"]!.AsArray();
        Assert.DoesNotContain(toolsAfterRevoke, t => t!["name"]!.GetValue<string>() == $"{slug}__t");
        Assert.False(catalog.TryResolve($"{slug}__t", out _));
    }

    [Fact]
    public async Task Reconcile_failed_open_retries_next_tick()
    {
        var fake = new FakeGatewayConnection();
        await using var mgr = new BackendSessionManager(fake, "ag1");
        var catalog = new AggregateCatalog();
        var changes = 0;

        var s1 = new ServerDescriptor("s1", "GitHub", true);
        var slug = ToolNamespacer.Slug(s1.DisplayName, s1.Id);
        var baseline = new SpaceSnapshot(Array.Empty<ServerDescriptor>(), Array.Empty<ServerDescriptor>());
        var watcher = new SpaceWatcher(_ => Task.FromResult(baseline), mgr, catalog,
            _ => { changes++; return Task.CompletedTask; }, baseline: baseline);

        // Make the first open attempt fail with AccessDenied (simulates publisher offline).
        fake.FailOpenOnce.Add("s1");
        var cur = new SpaceSnapshot(new[] { s1 }, Array.Empty<ServerDescriptor>());

        // Tick 1: open fails — s1 NOT in catalog, NOT in _previous, onChanged NOT fired.
        var changed1 = await watcher.ReconcileAsync(cur, default).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(changed1);
        Assert.Equal(0, changes);
        // submit_feedback is always present; s1 tools must not appear on failed open.
        var toolsTick1 = JsonNode.Parse(catalog.ToolsListJson())!["tools"]!.AsArray();
        Assert.DoesNotContain(toolsTick1, t => t!["name"]!.GetValue<string>() == $"{slug}__t");

        // Tick 2: same snapshot, open now succeeds — s1 added to catalog, onChanged fires.
        var changed2 = await watcher.ReconcileAsync(cur, default).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(changed2);
        Assert.Equal(1, changes);
        var toolsNode = JsonNode.Parse(catalog.ToolsListJson())!["tools"]!.AsArray();
        Assert.Contains(toolsNode, t => t!["name"]!.GetValue<string>() == $"{slug}__t");
    }
}
