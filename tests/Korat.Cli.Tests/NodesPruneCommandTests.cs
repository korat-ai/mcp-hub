using System.Net;
using System.Text;
using System.Text.Json;
using Korat.Cli.Auth;
using Korat.Cli.Commands;

namespace Korat.Cli.Tests;

/// <summary>
/// <c>korat runtimes prune [--older-than 30] [--yes] [--json]</c> — preview stale internal
/// consumer identities, confirm (unless <c>--yes</c>), POST /api/nodes/prune, print the result. The confirm
/// step is injected (mirrors <see cref="NodesPruneCommand.ExecuteAsync"/>'s
/// <c>Func&lt;string, Task&lt;bool&gt;&gt;? confirmAsync</c> parameter) so these tests never touch a
/// real terminal — same reasoning UpgradeCommand's tty logic is left untested at the RunAsync
/// layer (see UpgradeCommandTests, which only covers the pure static helpers).
/// </summary>
public sealed class NodesPruneCommandTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    private CredentialStore BuildStore() => new(_tempDir);

    private static CliCredentials MakeCreds(string url = "https://cloud.example.com") =>
        new("korat_cli_test_bearer_token", "full", DateTimeOffset.UtcNow.AddDays(90), url);

    private static HttpResponseMessage SpaceResponse(DateTimeOffset now, params (string Id, string Name, string Kind, DateTimeOffset? LastSeenAt)[] nodes) =>
        SpaceResponseWithCreatedAt(now, nodes.Select(n => (n.Id, n.Name, n.Kind, n.LastSeenAt, CreatedAt: now.AddDays(-1))).ToArray());

    /// <summary>
    /// #167 review (fix 1): overload carrying an explicit CreatedAt per node, so tests can cover
    /// the never-seen-node fallback (LastSeenAt ?? CreatedAt) that NodesPruneCommand's preview now
    /// mirrors from the cloud. <see cref="SpaceResponse(DateTimeOffset, ValueTuple{string,string,string,DateTimeOffset?}[])"/>
    /// defaults CreatedAt to "yesterday" (recent, so it never accidentally makes an existing
    /// never-seen-fresh-style test go stale). A distinct name (rather than a 5-tuple overload)
    /// avoids an ambiguous-call error on the zero-args case, which both params-array overloads
    /// would otherwise match equally well.
    /// </summary>
    private static HttpResponseMessage SpaceResponseWithCreatedAt(DateTimeOffset now, params (string Id, string Name, string Kind, DateTimeOffset? LastSeenAt, DateTimeOffset CreatedAt)[] nodes)
    {
        var body = JsonSerializer.Serialize(new
        {
            serverTime = now,
            presenceStaleSeconds = 90,
            nodes = nodes.Select(n => new
            {
                id = new { value = n.Id },
                displayName = n.Name,
                status = "Offline",
                lastSeenAt = n.LastSeenAt,
                createdAt = n.CreatedAt,
                kind = n.Kind,
            }),
            mcpServers = Array.Empty<object>(),
        });
        return new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }

    private static HttpResponseMessage PruneOkResponse(params string[] prunedNames) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(new { prunedCount = prunedNames.Length, prunedNames }),
                Encoding.UTF8, "application/json")
        };

    // ──────────────────────────────────────────────────────────────────────────
    // Zero matches — early exit, no confirm, no POST.
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Zero_matches_exits_early_without_confirm_or_post()
    {
        var credStore = BuildStore();
        await credStore.SaveAsync(MakeCreds());
        var now = DateTimeOffset.UtcNow;

        var confirmCalled = false;
        var postCalled = false;
        var handler = new LoginCommandTests.CallbackHandler(req =>
        {
            if (req.Method == HttpMethod.Get)
                return SpaceResponse(now, ("n1", "fresh-agent", "agent", now.AddMinutes(-1)));
            postCalled = true;
            return PruneOkResponse();
        });

        var output = new StringWriter();
        var exitCode = await NodesPruneCommand.ExecuteAsync(
            olderThanDays: 30, yes: false, outputJson: false,
            credentialStore: credStore, handlerOverride: handler, outputWriter: output,
            confirmAsync: _ => { confirmCalled = true; return Task.FromResult(true); },
            ct: default);

        Assert.Equal(0, exitCode);
        Assert.False(confirmCalled);
        Assert.False(postCalled);
        Assert.Contains("No stale internal consumer identities", output.ToString());
    }

    [Fact]
    public async Task Zero_matches_with_json_prints_empty_result_shape()
    {
        var credStore = BuildStore();
        await credStore.SaveAsync(MakeCreds());
        var now = DateTimeOffset.UtcNow;

        var handler = new LoginCommandTests.CallbackHandler(req =>
        {
            if (req.Method == HttpMethod.Get)
                return SpaceResponse(now); // no nodes at all
            throw new InvalidOperationException("must not POST when nothing matches");
        });

        var output = new StringWriter();
        var exitCode = await NodesPruneCommand.ExecuteAsync(
            olderThanDays: 30, yes: false, outputJson: true,
            credentialStore: credStore, handlerOverride: handler, outputWriter: output,
            confirmAsync: null, ct: default);

        Assert.Equal(0, exitCode);
        using var doc = JsonDocument.Parse(output.ToString());
        Assert.Equal(0, doc.RootElement.GetProperty("prunedCount").GetInt32());
        Assert.Empty(doc.RootElement.GetProperty("prunedNames").EnumerateArray());
    }

    // ──────────────────────────────────────────────────────────────────────────
    // List → confirm → prune flow.
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Lists_candidates_then_confirms_then_posts_prune_and_prints_result()
    {
        var credStore = BuildStore();
        await credStore.SaveAsync(MakeCreds());
        var now = DateTimeOffset.UtcNow;

        string? confirmPrompt = null;
        HttpRequestMessage? postRequest = null;
        string? postBody = null;
        var handler = new LoginCommandTests.CallbackHandler(req =>
        {
            if (req.Method == HttpMethod.Get)
                return SpaceResponse(now,
                    ("n1", "stale-agent", "agent", now.AddDays(-45)),
                    ("n2", "fresh-agent", "agent", now.AddMinutes(-1)),
                    ("n3", "old-publisher", "publisher", now.AddDays(-365)));

            postRequest = req;
            postBody = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return PruneOkResponse("stale-agent");
        });

        var output = new StringWriter();
        var exitCode = await NodesPruneCommand.ExecuteAsync(
            olderThanDays: 30, yes: false, outputJson: false,
            credentialStore: credStore, handlerOverride: handler, outputWriter: output,
            confirmAsync: prompt => { confirmPrompt = prompt; return Task.FromResult(true); },
            ct: default);

        Assert.Equal(0, exitCode);
        Assert.NotNull(confirmPrompt);

        var printed = output.ToString();
        // Preview lists the stale agent (with last-seen) but NOT the fresh agent or the publisher.
        Assert.Contains("stale-agent", printed);
        Assert.DoesNotContain("fresh-agent", printed);
        Assert.DoesNotContain("old-publisher", printed);

        Assert.NotNull(postRequest);
        Assert.Equal(HttpMethod.Post, postRequest!.Method);
        Assert.EndsWith("/api/nodes/prune", postRequest.RequestUri!.AbsolutePath);
        Assert.Contains("\"agent\"", postBody);
        Assert.Contains("30", postBody);

        Assert.Contains("Pruned 1 consumer identity record(s): stale-agent", printed);
    }

    // ──────────────────────────────────────────────────────────────────────────
    // #167 review (fix 1): never-seen-node preview parity — mirrors the cloud's
    // (LastSeenAt ?? CreatedAt) fallback so the preview never lists a node the cloud would not
    // actually prune.
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Never_seen_node_with_recent_CreatedAt_is_excluded_from_preview()
    {
        var credStore = BuildStore();
        await credStore.SaveAsync(MakeCreds());
        var now = DateTimeOffset.UtcNow;

        var postCalled = false;
        var handler = new LoginCommandTests.CallbackHandler(req =>
        {
            if (req.Method == HttpMethod.Get)
                return SpaceResponseWithCreatedAt(now,
                    ("n1", "never-seen-fresh", "agent", (DateTimeOffset?)null, now.AddDays(-1)));
            postCalled = true;
            return PruneOkResponse();
        });

        var output = new StringWriter();
        var exitCode = await NodesPruneCommand.ExecuteAsync(
            olderThanDays: 30, yes: false, outputJson: false,
            credentialStore: credStore, handlerOverride: handler, outputWriter: output,
            confirmAsync: _ => Task.FromResult(true),
            ct: default);

        Assert.Equal(0, exitCode);
        Assert.False(postCalled);
        var printed = output.ToString();
        Assert.DoesNotContain("never-seen-fresh", printed);
        Assert.Contains("No stale internal consumer identities", printed);
    }

    [Fact]
    public async Task Never_seen_node_with_old_CreatedAt_is_included_in_preview()
    {
        var credStore = BuildStore();
        await credStore.SaveAsync(MakeCreds());
        var now = DateTimeOffset.UtcNow;

        var handler = new LoginCommandTests.CallbackHandler(req =>
        {
            if (req.Method == HttpMethod.Get)
                return SpaceResponseWithCreatedAt(now,
                    ("n1", "never-seen-old", "agent", (DateTimeOffset?)null, now.AddDays(-90)));
            return PruneOkResponse("never-seen-old");
        });

        var output = new StringWriter();
        var exitCode = await NodesPruneCommand.ExecuteAsync(
            olderThanDays: 30, yes: true, outputJson: false,
            credentialStore: credStore, handlerOverride: handler, outputWriter: output,
            confirmAsync: null, ct: default);

        Assert.Equal(0, exitCode);
        var printed = output.ToString();
        Assert.Contains("never-seen-old", printed);
        Assert.Contains("(last seen: never)", printed);
    }

    [Fact]
    public async Task Confirm_declined_cancels_without_posting()
    {
        var credStore = BuildStore();
        await credStore.SaveAsync(MakeCreds());
        var now = DateTimeOffset.UtcNow;

        var postCalled = false;
        var handler = new LoginCommandTests.CallbackHandler(req =>
        {
            if (req.Method == HttpMethod.Get)
                return SpaceResponse(now, ("n1", "stale-agent", "agent", now.AddDays(-45)));
            postCalled = true;
            return PruneOkResponse("stale-agent");
        });

        var output = new StringWriter();
        var exitCode = await NodesPruneCommand.ExecuteAsync(
            olderThanDays: 30, yes: false, outputJson: false,
            credentialStore: credStore, handlerOverride: handler, outputWriter: output,
            confirmAsync: _ => Task.FromResult(false),
            ct: default);

        Assert.Equal(0, exitCode);
        Assert.False(postCalled);
        Assert.Contains("cancelled", output.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    // ──────────────────────────────────────────────────────────────────────────
    // --yes skips confirmation entirely.
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Yes_option_skips_confirm_and_posts_directly()
    {
        var credStore = BuildStore();
        await credStore.SaveAsync(MakeCreds());
        var now = DateTimeOffset.UtcNow;

        var confirmCalled = false;
        var postCalled = false;
        var handler = new LoginCommandTests.CallbackHandler(req =>
        {
            if (req.Method == HttpMethod.Get)
                return SpaceResponse(now, ("n1", "stale-agent", "agent", now.AddDays(-45)));
            postCalled = true;
            return PruneOkResponse("stale-agent");
        });

        var output = new StringWriter();
        var exitCode = await NodesPruneCommand.ExecuteAsync(
            olderThanDays: 30, yes: true, outputJson: false,
            credentialStore: credStore, handlerOverride: handler, outputWriter: output,
            confirmAsync: _ => { confirmCalled = true; return Task.FromResult(true); },
            ct: default);

        Assert.Equal(0, exitCode);
        Assert.False(confirmCalled);
        Assert.True(postCalled);
        Assert.Contains("Pruned 1 consumer identity record(s): stale-agent", output.ToString());
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Validation / auth / transport failures.
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task OlderThanDays_below_1_is_a_usage_error_and_sends_no_request()
    {
        var credStore = BuildStore();
        await credStore.SaveAsync(MakeCreds());

        var handler = new LoginCommandTests.CallbackHandler(_ =>
            throw new InvalidOperationException("must not call the cloud on a usage error"));

        var output = new StringWriter();
        var exitCode = await NodesPruneCommand.ExecuteAsync(
            olderThanDays: 0, yes: true, outputJson: false,
            credentialStore: credStore, handlerOverride: handler, outputWriter: output,
            confirmAsync: null, ct: default);

        Assert.Equal(1, exitCode);
        Assert.Contains("--older-than", output.ToString());
    }

    [Fact]
    public async Task Missing_credentials_prints_error()
    {
        var credStore = BuildStore();

        var output = new StringWriter();
        var exitCode = await NodesPruneCommand.ExecuteAsync(
            olderThanDays: 30, yes: true, outputJson: false,
            credentialStore: credStore, handlerOverride: null, outputWriter: output,
            confirmAsync: null, ct: default);

        Assert.Equal(1, exitCode);
        Assert.Contains("korat login", output.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Cloud_error_status_on_prune_post_is_reported()
    {
        var credStore = BuildStore();
        await credStore.SaveAsync(MakeCreds());
        var now = DateTimeOffset.UtcNow;

        var handler = new LoginCommandTests.CallbackHandler(req =>
        {
            if (req.Method == HttpMethod.Get)
                return SpaceResponse(now, ("n1", "stale-agent", "agent", now.AddDays(-45)));
            return new HttpResponseMessage(HttpStatusCode.InternalServerError);
        });

        var output = new StringWriter();
        var exitCode = await NodesPruneCommand.ExecuteAsync(
            olderThanDays: 30, yes: true, outputJson: false,
            credentialStore: credStore, handlerOverride: handler, outputWriter: output,
            confirmAsync: null, ct: default);

        Assert.Equal(1, exitCode);
        Assert.Contains("500", output.ToString());
    }
}
