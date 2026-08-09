using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Korat.Domain;
using Korat.Domain.Entities;
using Korat.GrainInterfaces;

namespace Korat.Cloud.IntegrationTests;

/// <summary>
/// Node-visibility-doctor design (2026-07-02), Task B2: PATCH /api/nodes/{id} sets/clears the
/// owner-editable Note. BOLA-safe (foreign/unknown node → 404, same as a missing node), capped
/// at 500 chars (endpoint-level 400), null clears.
/// </summary>
public sealed class NodeNoteEndpointTests(KoratIntegrationFixture fixture) : IClassFixture<KoratIntegrationFixture>
{
    private static async Task<NodeId> RegisterNodeAsync(KoratIntegrationFixture fixture, string spaceId, string displayName)
    {
        var nodeId = NodeId.New();
        var grain = fixture.ClusterClient.GetGrain<ISpaceGrain>(spaceId);
        await grain.RegisterNodeAsync(new Node
        {
            Id = nodeId,
            SpaceId = new SpaceId(spaceId),
            DisplayName = displayName,
            Status = NodeStatus.Online,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        return nodeId;
    }

    private static StringContent NoteBody(string? note) =>
        new(JsonSerializer.Serialize(new { note }), Encoding.UTF8, "application/json");

    private static StringContent RawBody(string json) =>
        new(json, Encoding.UTF8, "application/json");

    [Fact]
    public async Task Patch_Unauthenticated_Returns401()
    {
        var resp = await fixture.Factory.CreateClient()
            .PatchAsync("/api/nodes/some-node-id", NoteBody("hello"));
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task Patch_UnknownNode_Returns404()
    {
        var owner = await fixture.SeedUserAsync($"node-note-unknown-{Guid.NewGuid():N}@x.io", "Note Unknown");
        using var client = await fixture.CreateAuthenticatedClientAsync(owner.UserId);

        var resp = await client.PatchAsync("/api/nodes/does-not-exist", NoteBody("hello"));
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task Patch_ForeignNode_Returns404_SameAsUnknown()
    {
        // BOLA: user B PATCHing user A's node must get the exact same 404 as an unknown id —
        // no existence oracle.
        var a = await fixture.SeedUserAsync($"node-note-foreign-a-{Guid.NewGuid():N}@x.io", "Note Foreign A");
        var b = await fixture.SeedUserAsync($"node-note-foreign-b-{Guid.NewGuid():N}@x.io", "Note Foreign B");

        var nodeIdA = await RegisterNodeAsync(fixture, a.SpaceId, "a-node");

        using var clientB = await fixture.CreateAuthenticatedClientAsync(b.UserId);
        var resp = await clientB.PatchAsync($"/api/nodes/{nodeIdA.Value}", NoteBody("hijack"));

        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task Patch_OwnNode_SetsNote()
    {
        var owner = await fixture.SeedUserAsync($"node-note-set-{Guid.NewGuid():N}@x.io", "Note Set");
        var nodeId = await RegisterNodeAsync(fixture, owner.SpaceId, "my-mac");
        using var client = await fixture.CreateAuthenticatedClientAsync(owner.UserId);

        var resp = await client.PatchAsync($"/api/nodes/{nodeId.Value}", NoteBody("  work laptop  "));
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        // Grain layer trims whitespace.
        Assert.Equal("work laptop", body.GetProperty("note").GetString());

        // Persisted — a fresh grain read reflects the trimmed note.
        var node = await fixture.ClusterClient.GetGrain<INodeGrain>(nodeId.Value).GetAsync();
        Assert.Equal("work laptop", node.Note);
    }

    [Fact]
    public async Task Patch_NullNote_ClearsExistingNote()
    {
        var owner = await fixture.SeedUserAsync($"node-note-clear-{Guid.NewGuid():N}@x.io", "Note Clear");
        var nodeId = await RegisterNodeAsync(fixture, owner.SpaceId, "my-mac-2");
        using var client = await fixture.CreateAuthenticatedClientAsync(owner.UserId);

        var setResp = await client.PatchAsync($"/api/nodes/{nodeId.Value}", NoteBody("temporary"));
        Assert.Equal(HttpStatusCode.OK, setResp.StatusCode);

        var clearResp = await client.PatchAsync($"/api/nodes/{nodeId.Value}", NoteBody(null));
        Assert.Equal(HttpStatusCode.OK, clearResp.StatusCode);

        var body = await clearResp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(JsonValueKind.Null, body.GetProperty("note").ValueKind);

        var node = await fixture.ClusterClient.GetGrain<INodeGrain>(nodeId.Value).GetAsync();
        Assert.Null(node.Note);
    }

    [Fact]
    public async Task Patch_NoteOver500Chars_Returns400()
    {
        var owner = await fixture.SeedUserAsync($"node-note-toolong-{Guid.NewGuid():N}@x.io", "Note TooLong");
        var nodeId = await RegisterNodeAsync(fixture, owner.SpaceId, "my-mac-3");
        using var client = await fixture.CreateAuthenticatedClientAsync(owner.UserId);

        var resp = await client.PatchAsync($"/api/nodes/{nodeId.Value}", NoteBody(new string('x', 501)));
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);

        // Note must remain unset — the rejected PATCH must not have reached the grain.
        var node = await fixture.ClusterClient.GetGrain<INodeGrain>(nodeId.Value).GetAsync();
        Assert.Null(node.Note);
    }

    [Fact]
    public async Task Patch_NoteExactly500Chars_Returns200()
    {
        var owner = await fixture.SeedUserAsync($"node-note-exact500-{Guid.NewGuid():N}@x.io", "Note Exact500");
        var nodeId = await RegisterNodeAsync(fixture, owner.SpaceId, "my-mac-4");
        using var client = await fixture.CreateAuthenticatedClientAsync(owner.UserId);

        var text = new string('y', 500);
        var resp = await client.PatchAsync($"/api/nodes/{nodeId.Value}", NoteBody(text));
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    // ──────────────────────────────────────────────────────────────────────────
    // LOW-finding regression: {} (note property ABSENT) is indistinguishable from
    // {"note":null} when model-bound straight into PatchNodeRequest(string? Note) — both
    // deserialize to a C# null, so an empty-object PATCH used to silently CLEAR the note.
    // Absent must now be a 400 usage error; explicit null still clears; a string still sets.
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Patch_EmptyObject_NotePropertyAbsent_Returns400_AndLeavesNoteUntouched()
    {
        var owner = await fixture.SeedUserAsync($"node-note-absent-{Guid.NewGuid():N}@x.io", "Note Absent");
        var nodeId = await RegisterNodeAsync(fixture, owner.SpaceId, "my-mac-5");
        using var client = await fixture.CreateAuthenticatedClientAsync(owner.UserId);

        // Establish a note first so a wrongly-implicit clear would be observable.
        var setResp = await client.PatchAsync($"/api/nodes/{nodeId.Value}", NoteBody("keep me"));
        Assert.Equal(HttpStatusCode.OK, setResp.StatusCode);

        var resp = await client.PatchAsync($"/api/nodes/{nodeId.Value}", RawBody("{}"));
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);

        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains("note", body.GetProperty("error").GetString(), StringComparison.OrdinalIgnoreCase);

        // The rejected PATCH must not have reached the grain — note stays as it was.
        var node = await fixture.ClusterClient.GetGrain<INodeGrain>(nodeId.Value).GetAsync();
        Assert.Equal("keep me", node.Note);
    }

    [Fact]
    public async Task Patch_ExplicitNullNote_Returns200_AndClears_DistinctFromAbsent()
    {
        var owner = await fixture.SeedUserAsync($"node-note-explicitnull-{Guid.NewGuid():N}@x.io", "Note ExplicitNull");
        var nodeId = await RegisterNodeAsync(fixture, owner.SpaceId, "my-mac-6");
        using var client = await fixture.CreateAuthenticatedClientAsync(owner.UserId);

        var setResp = await client.PatchAsync($"/api/nodes/{nodeId.Value}", NoteBody("temporary"));
        Assert.Equal(HttpStatusCode.OK, setResp.StatusCode);

        var resp = await client.PatchAsync($"/api/nodes/{nodeId.Value}", RawBody("""{"note":null}"""));
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var node = await fixture.ClusterClient.GetGrain<INodeGrain>(nodeId.Value).GetAsync();
        Assert.Null(node.Note);
    }

    [Fact]
    public async Task Patch_StringNote_Returns200_AndSets()
    {
        var owner = await fixture.SeedUserAsync($"node-note-stringset-{Guid.NewGuid():N}@x.io", "Note StringSet");
        var nodeId = await RegisterNodeAsync(fixture, owner.SpaceId, "my-mac-7");
        using var client = await fixture.CreateAuthenticatedClientAsync(owner.UserId);

        var resp = await client.PatchAsync($"/api/nodes/{nodeId.Value}", RawBody("""{"note":"set via string shape"}"""));
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var node = await fixture.ClusterClient.GetGrain<INodeGrain>(nodeId.Value).GetAsync();
        Assert.Equal("set via string shape", node.Note);
    }

    [Fact]
    public async Task Patch_PascalCaseNoteProperty_IsAccepted_MatchesCliWireFormat()
    {
        // The real CLI sends PascalCase ("Note") — its source-generated JSON context has no
        // naming policy (see KoratCliJsonContext). The endpoint must accept both casings.
        var owner = await fixture.SeedUserAsync($"node-note-pascal-{Guid.NewGuid():N}@x.io", "Note Pascal");
        var nodeId = await RegisterNodeAsync(fixture, owner.SpaceId, "my-mac-8");
        using var client = await fixture.CreateAuthenticatedClientAsync(owner.UserId);

        var resp = await client.PatchAsync($"/api/nodes/{nodeId.Value}", RawBody("""{"Note":"pascal case from the real cli"}"""));
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var node = await fixture.ClusterClient.GetGrain<INodeGrain>(nodeId.Value).GetAsync();
        Assert.Equal("pascal case from the real cli", node.Note);
    }

    [Fact]
    public async Task Patch_NoteWrongJsonType_Returns400_AndLeavesNoteUntouched()
    {
        var owner = await fixture.SeedUserAsync($"node-note-wrongtype-{Guid.NewGuid():N}@x.io", "Note WrongType");
        var nodeId = await RegisterNodeAsync(fixture, owner.SpaceId, "my-mac-9");
        using var client = await fixture.CreateAuthenticatedClientAsync(owner.UserId);

        var resp = await client.PatchAsync($"/api/nodes/{nodeId.Value}", RawBody("""{"note":42}"""));
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);

        var node = await fixture.ClusterClient.GetGrain<INodeGrain>(nodeId.Value).GetAsync();
        Assert.Null(node.Note);
    }
}
