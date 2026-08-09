using Korat.Cli.Commands;

namespace Korat.Cli.Tests;

/// <summary>
/// Unit tests for 017 agent-identity registry:
/// - create on first use, persist, round-trip
/// - resolve existing by name (case-insensitive)
/// - each agent gets a NodeId distinct from the publisher NodeId
/// - connect uses agent NodeId, not publisher NodeId
/// </summary>
public class AgentIdentityTests
{
    // ──────────────────────────────────────────────────────────────────────────
    // Helpers
    // ──────────────────────────────────────────────────────────────────────────

    private static (LocalIdentityStore store, string path) MakeStore()
    {
        var path = Path.Combine(Path.GetTempPath(), $"korat-test-{Guid.NewGuid():N}.json");
        return (new LocalIdentityStore(path), path);
    }

    private static LocalIdentity MakePublisher(string publisherNodeId = "publisher-node-id") =>
        new()
        {
            SpaceId = "default",
            NodeId = publisherNodeId,
            CloudUrl = "http://localhost:5191",
        };

    // ──────────────────────────────────────────────────────────────────────────
    // 1. Auto-create on first use
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ResolveOrCreateAgent_creates_new_agent_when_absent()
    {
        var (store, path) = MakeStore();
        try
        {
            var identity = MakePublisher();
            var agent = ConnectCommand.ResolveOrCreateAgent(identity, "default", store);

            Assert.Equal("default", agent.Name);
            Assert.False(string.IsNullOrWhiteSpace(agent.NodeId));
            Assert.False(string.IsNullOrWhiteSpace(agent.AgentClientId));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void ResolveOrCreateAgent_agent_NodeId_distinct_from_publisher_NodeId()
    {
        var (store, path) = MakeStore();
        try
        {
            var identity = MakePublisher("publisher-node-id");
            var agent = ConnectCommand.ResolveOrCreateAgent(identity, "default", store);

            Assert.NotEqual(identity.NodeId, agent.NodeId);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void ResolveOrCreateAgent_different_names_get_different_NodeIds()
    {
        var (store, path) = MakeStore();
        try
        {
            var identity = MakePublisher();
            var cursor = ConnectCommand.ResolveOrCreateAgent(identity, "cursor", store);
            var claude = ConnectCommand.ResolveOrCreateAgent(identity, "claude", store);

            Assert.NotEqual(cursor.NodeId, claude.NodeId);
            Assert.NotEqual(cursor.AgentClientId, claude.AgentClientId);
        }
        finally { File.Delete(path); }
    }

    // ──────────────────────────────────────────────────────────────────────────
    // 2. Persist and round-trip
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ResolveOrCreateAgent_persists_to_config_json()
    {
        var (store, path) = MakeStore();
        try
        {
            var identity = MakePublisher();
            var created = ConnectCommand.ResolveOrCreateAgent(identity, "default", store);

            // Reload from disk.
            var reloaded = store.LoadOrCreate();
            var found = reloaded.Agents.Find(a => a.Name == "default");

            Assert.NotNull(found);
            Assert.Equal(created.NodeId, found.NodeId);
            Assert.Equal(created.AgentClientId, found.AgentClientId);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void ResolveOrCreateAgent_resolves_existing_without_creating_duplicate()
    {
        var (store, path) = MakeStore();
        try
        {
            var identity = MakePublisher();
            var first = ConnectCommand.ResolveOrCreateAgent(identity, "default", store);

            // Reload and resolve again.
            var reloaded = store.LoadOrCreate();
            var second = ConnectCommand.ResolveOrCreateAgent(reloaded, "default", store);

            Assert.Equal(first.NodeId, second.NodeId);
            Assert.Equal(first.AgentClientId, second.AgentClientId);
            Assert.Single(reloaded.Agents);
        }
        finally { File.Delete(path); }
    }

    // ──────────────────────────────────────────────────────────────────────────
    // 3. Name matching is case-insensitive
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ResolveOrCreateAgent_name_match_is_case_insensitive()
    {
        var (store, path) = MakeStore();
        try
        {
            var identity = MakePublisher();
            var created = ConnectCommand.ResolveOrCreateAgent(identity, "Cursor", store);

            var reloaded = store.LoadOrCreate();
            var resolved = ConnectCommand.ResolveOrCreateAgent(reloaded, "cursor", store);

            Assert.Equal(created.NodeId, resolved.NodeId);
            Assert.Single(reloaded.Agents);
        }
        finally { File.Delete(path); }
    }

    // ──────────────────────────────────────────────────────────────────────────
    // 4. Multiple agents coexist in same config.json
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Multiple_agents_coexist_and_all_survive_round_trip()
    {
        var (store, path) = MakeStore();
        try
        {
            var identity = MakePublisher();
            var a1 = ConnectCommand.ResolveOrCreateAgent(identity, "cursor", store);
            var a2 = ConnectCommand.ResolveOrCreateAgent(identity, "claude", store);
            var a3 = ConnectCommand.ResolveOrCreateAgent(identity, "default", store);

            var reloaded = store.LoadOrCreate();

            Assert.Equal(3, reloaded.Agents.Count);
            Assert.Contains(reloaded.Agents, a => a.Name == "cursor" && a.NodeId == a1.NodeId);
            Assert.Contains(reloaded.Agents, a => a.Name == "claude" && a.NodeId == a2.NodeId);
            Assert.Contains(reloaded.Agents, a => a.Name == "default" && a.NodeId == a3.NodeId);
        }
        finally { File.Delete(path); }
    }

    // ──────────────────────────────────────────────────────────────────────────
    // 5. 020-A: agent connect uses agent name as DisplayName (not machine name)
    // NodeGatewayConnection.ConnectAsync is not unit-testable without a live gRPC
    // server, so we verify the display-name string selection logic: the value
    // passed as displayName is the agentName parameter (the --agent CLI value),
    // which equals the agent's Name field returned by ResolveOrCreateAgent.
    // ──────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("cursor")]
    [InlineData("default")]
    [InlineData("test-workspace")]
    public void AgentName_used_as_displayName_is_the_resolved_agent_Name(string agentName)
    {
        // The connect flow passes agentName (the --agent value) as displayName to
        // NodeGatewayConnection.ConnectAsync. Verify that the resolved AgentIdentity.Name
        // matches so agents appear under their friendly names in the Nodes view (020-A).
        var (store, path) = MakeStore();
        try
        {
            var identity = MakePublisher();
            var agent = ConnectCommand.ResolveOrCreateAgent(identity, agentName, store);

            // The display name sent in NodeHello equals agentName, not MachineName.
            Assert.Equal(agentName, agent.Name);
            Assert.NotEqual(Environment.MachineName, agent.Name);
        }
        finally { File.Delete(path); }
    }

    // ──────────────────────────────────────────────────────────────────────────
    // 6. LocalIdentity JSON serialization includes Agents list (trim-safety check)
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void LocalIdentity_with_agents_serializes_and_deserializes_via_source_gen_context()
    {
        var identity = new LocalIdentity
        {
            NodeId = "pub-node",
            Agents =
            [
                new AgentIdentity { Name = "test", NodeId = "agent-node-1", AgentClientId = "client-1" }
            ]
        };

        var json = System.Text.Json.JsonSerializer.Serialize(identity, Korat.Cli.KoratCliJsonContext.Default.LocalIdentity);
        var loaded = System.Text.Json.JsonSerializer.Deserialize(json, Korat.Cli.KoratCliJsonContext.Default.LocalIdentity);

        Assert.NotNull(loaded);
        Assert.Single(loaded.Agents);
        Assert.Equal("test", loaded.Agents[0].Name);
        Assert.Equal("agent-node-1", loaded.Agents[0].NodeId);
        Assert.Equal("client-1", loaded.Agents[0].AgentClientId);
    }

    // ──────────────────────────────────────────────────────────────────────────
    // 7. PR-5 (agent-id-identity): AgentId field + legacy-name compat shim
    //
    // The hosted-agent bridge identity was re-keyed from `agent-{name}` to
    // `agent-{name}-{id8}` (delete->recreate safety). Naively resolving the new name would
    // mint a FRESH ConsumerId for every pre-existing hosted agent on its first post-PR-5
    // turn, silently detaching its Active grants and forcing a re-approval. The shim below
    // migrates a legacy `agent-{name}` identity to the new name IN PLACE (same NodeId +
    // ConsumerId) instead.
    // ──────────────────────────────────────────────────────────────────────────

    private const string TestAgentId = "a1b2c3d4e5f6a1b2c3d4e5f6a1b2c3d4"; // Guid("N")-shaped
    private const string TestAgentIdShort8 = "a1b2c3d4";

    [Fact]
    public void ResolveOrCreateAgent_new_agent_records_AgentId_when_supplied()
    {
        var (store, path) = MakeStore();
        try
        {
            var identity = MakePublisher();
            var agent = ConnectCommand.ResolveOrCreateAgent(
                identity, $"agent-concierge-{TestAgentIdShort8}", store, TestAgentId);

            Assert.Equal($"agent-concierge-{TestAgentIdShort8}", agent.Name);
            Assert.Equal(TestAgentId, agent.AgentId);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void ResolveOrCreateAgent_legacy_name_migrates_in_place_preserving_NodeId_and_AgentClientId()
    {
        var (store, path) = MakeStore();
        try
        {
            var identity = MakePublisher();
            // Pre-PR-5 identity: created under the legacy `agent-{name}` shape, no AgentId
            // recorded (predates the field).
            var legacy = ConnectCommand.ResolveOrCreateAgent(identity, "agent-concierge", store);
            var legacyNodeId = legacy.NodeId;
            var legacyAgentClientId = legacy.AgentClientId;

            // First post-PR-5 turn resolves under the NEW name, with the real AgentId known.
            var newName = $"agent-concierge-{TestAgentIdShort8}";
            var migrated = ConnectCommand.ResolveOrCreateAgent(identity, newName, store, TestAgentId);

            Assert.Equal(newName, migrated.Name);
            Assert.Equal(legacyNodeId, migrated.NodeId);
            Assert.Equal(legacyAgentClientId, migrated.AgentClientId);
            Assert.Equal(TestAgentId, migrated.AgentId);

            // In-place migration — exactly ONE identity survives, not a second one alongside
            // the (now-stale) legacy name.
            Assert.Single(identity.Agents);
            Assert.DoesNotContain(identity.Agents, a => a.Name == "agent-concierge");

            // Idempotent + persisted: reloading and resolving again returns the SAME identity
            // without creating a duplicate or re-migrating anything.
            var reloaded = store.LoadOrCreate();
            Assert.Single(reloaded.Agents);
            var second = ConnectCommand.ResolveOrCreateAgent(reloaded, newName, store, TestAgentId);
            Assert.Equal(legacyNodeId, second.NodeId);
            Assert.Equal(legacyAgentClientId, second.AgentClientId);
            Assert.Single(reloaded.Agents);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void ResolveOrCreateAgent_legacy_migration_is_case_insensitive()
    {
        var (store, path) = MakeStore();
        try
        {
            var identity = MakePublisher();
            var legacy = ConnectCommand.ResolveOrCreateAgent(identity, "Agent-Concierge", store);

            var newName = $"agent-concierge-{TestAgentIdShort8}";
            var migrated = ConnectCommand.ResolveOrCreateAgent(identity, newName, store, TestAgentId);

            Assert.Equal(legacy.NodeId, migrated.NodeId);
            Assert.Equal(legacy.AgentClientId, migrated.AgentClientId);
            Assert.Single(identity.Agents);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void ResolveOrCreateAgent_without_matching_legacy_name_creates_fresh_identity()
    {
        // No legacy `agent-concierge` on record — e.g. a brand-new hosted agent created
        // after PR-5. Must mint a fresh identity under the new name (no shim to trigger),
        // exactly like the pre-existing create-on-first-use path.
        var (store, path) = MakeStore();
        try
        {
            var identity = MakePublisher();
            var newName = $"agent-concierge-{TestAgentIdShort8}";
            var agent = ConnectCommand.ResolveOrCreateAgent(identity, newName, store, TestAgentId);

            Assert.Equal(newName, agent.Name);
            Assert.Equal(TestAgentId, agent.AgentId);
            Assert.False(string.IsNullOrWhiteSpace(agent.NodeId));
            Assert.False(string.IsNullOrWhiteSpace(agent.AgentClientId));
            Assert.Single(identity.Agents);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void ResolveOrCreateAgent_backfills_AgentId_onto_an_already_resolved_identity_missing_it()
    {
        // An identity resolved once already under the NEW name (e.g. because a prior turn
        // ran before the bridge started passing --agent-id) has no AgentId recorded yet.
        // A later turn that DOES supply it must backfill it in place — same NodeId/
        // ConsumerId, no duplicate created.
        var (store, path) = MakeStore();
        try
        {
            var identity = MakePublisher();
            var newName = $"agent-concierge-{TestAgentIdShort8}";
            var first = ConnectCommand.ResolveOrCreateAgent(identity, newName, store); // no agentId yet
            Assert.Null(first.AgentId);

            var second = ConnectCommand.ResolveOrCreateAgent(identity, newName, store, TestAgentId);

            Assert.Equal(first.NodeId, second.NodeId);
            Assert.Equal(first.AgentClientId, second.AgentClientId);
            Assert.Equal(TestAgentId, second.AgentId);
            Assert.Single(identity.Agents);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void ResolveOrCreateAgent_id8_collision_with_different_recorded_AgentId_mints_fresh_identity()
    {
        // fable #188 (LOW-2): a deleted agent left a local identity
        // `agent-concierge-{id8}` behind with AgentId=A recorded. A recreated
        // same-name agent whose Guid("N") AgentId happens to collide on the first 8
        // hex chars (id8) resolves to the SAME slot name. The exact-match branch must
        // detect the AgentId mismatch and mint a FRESH identity (new ConsumerId) —
        // not hand back the dead agent's ConsumerId (and therefore its grants).
        var (store, path) = MakeStore();
        try
        {
            var identity = MakePublisher();
            var slotName = $"agent-concierge-{TestAgentIdShort8}";

            var dead = ConnectCommand.ResolveOrCreateAgent(identity, slotName, store, TestAgentId);
            var deadAgentClientId = dead.AgentClientId;
            var deadNodeId = dead.NodeId;

            // Same id8 (first 8 hex chars), different full Guid("N") AgentId — a
            // different agent colliding on the 8-char slot.
            const string collidingAgentId = "a1b2c3d4ffffffffffffffffffffffff";
            Assert.Equal(TestAgentIdShort8, collidingAgentId[..8]);
            Assert.NotEqual(TestAgentId, collidingAgentId);

            var recreated = ConnectCommand.ResolveOrCreateAgent(
                identity, slotName, store, collidingAgentId);

            Assert.Equal(collidingAgentId, recreated.AgentId);
            Assert.NotEqual(deadAgentClientId, recreated.AgentClientId);
            Assert.NotEqual(deadNodeId, recreated.NodeId);

            // Replaced, not duplicated — exactly one identity survives under the slot.
            Assert.Single(identity.Agents);
            Assert.Equal(slotName, identity.Agents[0].Name);
            Assert.Equal(collidingAgentId, identity.Agents[0].AgentId);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void TryStripLegacyBridgeSuffix_strips_the_exact_id8_suffix()
    {
        var stripped = ConnectCommand.TryStripLegacyBridgeSuffix(
            $"agent-concierge-{TestAgentIdShort8}", TestAgentId, out var legacyName);

        Assert.True(stripped);
        Assert.Equal("agent-concierge", legacyName);
    }

    [Fact]
    public void TryStripLegacyBridgeSuffix_returns_false_when_the_name_does_not_end_with_this_id8()
    {
        var stripped = ConnectCommand.TryStripLegacyBridgeSuffix(
            "agent-concierge", TestAgentId, out var legacyName);

        Assert.False(stripped);
        Assert.Equal(string.Empty, legacyName);
    }

    [Fact]
    public void TryStripLegacyBridgeSuffix_is_case_insensitive_on_the_id8_suffix()
    {
        var stripped = ConnectCommand.TryStripLegacyBridgeSuffix(
            "agent-concierge-A1B2C3D4", TestAgentId, out var legacyName);

        Assert.True(stripped);
        Assert.Equal("agent-concierge", legacyName);
    }
}
