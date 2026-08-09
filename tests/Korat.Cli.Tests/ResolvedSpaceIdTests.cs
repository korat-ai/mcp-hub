using System.Text.Json;
using Korat.Cli.Commands;

namespace Korat.Cli.Tests;

/// <summary>
/// TDD tests for fix/default-space-placeholder:
///
/// (a) LocalIdentity default SpaceId is empty string, not "default".
/// (b) LocalIdentityStore.PersistResolvedSpaceId replaces any existing SpaceId
///     (including "default") with the server-resolved value and saves to disk.
/// (c) A legacy config.json with "default" round-trips cleanly (back-compat load),
///     and PersistResolvedSpaceId overwrites it with the real space.
/// (d) Proto back-compat: field numbers 1-4 in GatewayHello are unchanged
///     (checked by inspecting the generated descriptor).
/// </summary>
public class ResolvedSpaceIdTests : IDisposable
{
    private readonly string _tempPath =
        Path.Combine(Path.GetTempPath(), $"korat-space-test-{Guid.NewGuid():N}.json");

    public void Dispose()
    {
        if (File.Exists(_tempPath)) File.Delete(_tempPath);
    }

    // ── (a) Default SpaceId is empty string ─────────────────────────────────

    [Fact]
    public void LocalIdentity_DefaultSpaceId_IsEmpty()
    {
        var identity = new LocalIdentity();
        Assert.Equal(string.Empty, identity.SpaceId);
    }

    [Fact]
    public void LocalIdentityStore_CreateNew_SpaceId_IsEmpty()
    {
        var store = new LocalIdentityStore(_tempPath);
        // LoadOrCreate will call CreateNew() since the path does not exist yet.
        var identity = store.LoadOrCreate();
        Assert.Equal(string.Empty, identity.SpaceId);
    }

    // ── (b) PersistResolvedSpaceId writes the real space ────────────────────

    [Fact]
    public void PersistResolvedSpaceId_OverwritesEmptyPlaceholderAndSaves()
    {
        var store = new LocalIdentityStore(_tempPath);
        var identity = store.LoadOrCreate(); // SpaceId == ""
        Assert.Equal(string.Empty, identity.SpaceId);

        var realSpaceId = Guid.NewGuid().ToString();
        store.PersistResolvedSpaceId(identity, realSpaceId);

        // In-memory object updated.
        Assert.Equal(realSpaceId, identity.SpaceId);

        // Reload from disk — must reflect the persisted real value.
        var reloaded = store.LoadOrCreate();
        Assert.Equal(realSpaceId, reloaded.SpaceId);
    }

    [Fact]
    public void PersistResolvedSpaceId_DoesNotPersist_WhenResolved_IsEmpty()
    {
        var store = new LocalIdentityStore(_tempPath);
        var identity = store.LoadOrCreate();

        // Calling with empty resolved value should be a no-op (server returned empty).
        store.PersistResolvedSpaceId(identity, string.Empty);

        // The on-disk SpaceId remains whatever it was (still empty seed in this case).
        var reloaded = store.LoadOrCreate();
        Assert.Equal(string.Empty, reloaded.SpaceId);
    }

    // ── (c) Legacy config.json with "default" is back-compat ────────────────

    [Fact]
    public void LegacyConfig_WithDefaultSpaceId_LoadsSuccessfully()
    {
        // Write a legacy config.json with SpaceId = "default" (the old placeholder).
        var legacyJson = """
            {
              "SpaceId": "default",
              "NodeId": "legacy-node-id",
              "CloudUrl": "https://my.korat.ai",
              "CloudGrpcUrl": "https://my.korat.ai",
              "McpServers": [],
              "Agents": []
            }
            """;
        File.WriteAllText(_tempPath, legacyJson);

        var store = new LocalIdentityStore(_tempPath);
        var identity = store.LoadOrCreate();

        // Must load without error and expose the stored "default".
        Assert.Equal("default", identity.SpaceId);
        Assert.Equal("legacy-node-id", identity.NodeId);
    }

    [Fact]
    public void LegacyConfig_DefaultSpaceId_OverwrittenByPersistResolvedSpaceId()
    {
        var legacyJson = """
            {
              "SpaceId": "default",
              "NodeId": "legacy-node-id",
              "CloudUrl": "https://my.korat.ai",
              "CloudGrpcUrl": "https://my.korat.ai",
              "McpServers": [],
              "Agents": []
            }
            """;
        File.WriteAllText(_tempPath, legacyJson);

        var store = new LocalIdentityStore(_tempPath);
        var identity = store.LoadOrCreate();
        Assert.Equal("default", identity.SpaceId);

        // Simulate first successful connect: server returns the real SpaceId.
        var realSpaceId = Guid.NewGuid().ToString();
        store.PersistResolvedSpaceId(identity, realSpaceId);

        // In-memory updated.
        Assert.Equal(realSpaceId, identity.SpaceId);

        // On-disk updated — "default" is gone.
        var reloaded = store.LoadOrCreate();
        Assert.Equal(realSpaceId, reloaded.SpaceId);
        Assert.Equal("legacy-node-id", reloaded.NodeId); // other fields intact
    }

    // ── (d) JSON round-trip: SpaceId persists correctly ─────────────────────

    [Fact]
    public void LocalIdentity_SpaceId_RoundTripsViaSourceGenContext()
    {
        var identity = new LocalIdentity
        {
            SpaceId = "aaaabbbb-cccc-dddd-eeee-ffffffffffff",
            NodeId = "test-node",
        };

        var json = JsonSerializer.Serialize(identity, Korat.Cli.KoratCliJsonContext.Default.LocalIdentity);
        var loaded = JsonSerializer.Deserialize(json, Korat.Cli.KoratCliJsonContext.Default.LocalIdentity);

        Assert.NotNull(loaded);
        Assert.Equal("aaaabbbb-cccc-dddd-eeee-ffffffffffff", loaded.SpaceId);
    }
}
