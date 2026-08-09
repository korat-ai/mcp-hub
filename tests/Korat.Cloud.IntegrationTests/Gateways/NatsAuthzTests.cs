using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Configurations;
using DotNet.Testcontainers.Containers;
using Google.Protobuf;
using Korat.Cloud.Gateways;
using Korat.Domain;
using Korat.Relay.V1;
using Microsoft.Extensions.Logging.Abstractions;
using NATS.Client.Core;
using NATS.NKeys;
using Xunit.Abstractions;

namespace Korat.Cloud.IntegrationTests.Gateways;

/// <summary>
/// Shared NATS container fixture for <see cref="NatsAuthzTests"/>.  One container per
/// test class (IClassFixture) so Testcontainers' Ryuk reaper does not race between
/// per-test container starts.  The NKey pair is generated fresh for each fixture lifecycle.
/// </summary>
public sealed class NatsAuthzFixture : IAsyncLifetime
{
    public string NatsUrl { get; private set; } = default!;
    public string NkeySeed { get; private set; } = default!;
    public string NkeyPublic { get; private set; } = default!;

    private IContainer _container = default!;
    private string _tmpDir = default!;

    public async Task InitializeAsync()
    {
        var kp = KeyPair.CreatePair(PrefixByte.User);
        NkeySeed = kp.GetSeed();
        NkeyPublic = kp.GetPublicKey();

        _tmpDir = Path.Combine(Path.GetTempPath(), "korat-nats-authz-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tmpDir);
        await File.WriteAllTextAsync(Path.Combine(_tmpDir, "nats.conf"), BuildNatsConf(NkeyPublic));

        try
        {
            _container = new ContainerBuilder("nats:2.10")
                .WithBindMount(_tmpDir, "/etc/nats-test", AccessMode.ReadOnly)
                .WithPortBinding(4222, true)
                .WithPortBinding(8222, true)
                .WithCommand("-c", "/etc/nats-test/nats.conf",
                             "--addr", "0.0.0.0", "--port", "4222",
                             "--http_port", "8222")
                .WithWaitStrategy(
                    Wait.ForUnixContainer()
                        .UntilHttpRequestIsSucceeded(r => r.ForPort(8222).ForPath("/healthz")))
                .Build();

            await _container.StartAsync();
            var hostPort = _container.GetMappedPublicPort(4222);
            NatsUrl = $"nats://127.0.0.1:{hostPort}";
        }
        catch (Exception ex)
        {
            // Docker unavailable — NatsUrl stays null; tests will skip via SkipIfNoDocker().
            NatsUrl = null!;
            Console.Error.WriteLine(
                $"[SKIP] SKIPPED: Docker unavailable — relay authz invariants not checked " +
                $"(NatsAuthzFixture: nats:2.10 container failed to start: {ex.Message})");
        }
    }

    public async Task DisposeAsync()
    {
        if (_container is not null)
        {
            try { await _container.StopAsync(); } catch { /* best-effort */ }
            await _container.DisposeAsync();
        }
        if (_tmpDir is not null && Directory.Exists(_tmpDir))
        {
            try { Directory.Delete(_tmpDir, recursive: true); } catch { /* best-effort */ }
        }
    }

    private static string BuildNatsConf(string publicKey)
    {
        // 031 MAJOR-4: load the SHIPPED deploy/korat-nats/nats.conf and substitute the
        // placeholder public key so the test validates the DEPLOYED artifact, not a
        // hand-built clone.  The placeholder text is the sentinel in the real config file.
        const string Placeholder = "UXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXX";
        var repoRoot = FindRepoRoot();
        if (repoRoot is not null)
        {
            var confPath = Path.Combine(repoRoot, "deploy", "korat-nats", "nats.conf");
            if (File.Exists(confPath))
            {
                var shipped = File.ReadAllText(confPath);
                if (shipped.Contains(Placeholder))
                    return shipped.Replace(Placeholder, publicKey);
                // The shipped file already has a real key baked in (non-dev env).
                // Replace any 56-char U-prefixed NKey with our test key.
                return System.Text.RegularExpressions.Regex.Replace(
                    shipped,
                    @"U[A-Z2-7]{55}",
                    publicKey);
            }
        }

        // Fallback (repo root not found): build a minimal config that mirrors the shipped one.
        // This is a safety net only — CI always has the repo available.
        return
            "# Fallback test nats.conf — mirrors deploy/korat-nats/nats.conf (031 N-1a).\n" +
            "# No_auth_user intentionally absent: anonymous CONNECT must be rejected.\n" +
            "accounts {\n" +
            "  KORAT {\n" +
            "    users [\n" +
            "      {\n" +
            $"        nkey: {publicKey}\n" +
            "        permissions {\n" +
            "          publish {\n" +
            "            allow: [\"korat.relay.frame.>\", \"korat.relay.conn.>\", \"korat.relay.inf.>\"]\n" +
            "          }\n" +
            "          subscribe {\n" +
            "            allow: [\"korat.relay.frame.>\", \"korat.relay.conn.>\", \"korat.relay.inf.>\"]\n" +
            "          }\n" +
            "        }\n" +
            "      }\n" +
            "    ]\n" +
            "  }\n" +
            "}\n";
    }

    /// <summary>
    /// Walk up from the test assembly location to find the repository root
    /// (the directory containing <c>Korat.slnx</c> or <c>deploy/korat-nats/nats.conf</c>).
    /// </summary>
    private static string? FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Korat.slnx")) ||
                Directory.Exists(Path.Combine(dir.FullName, "deploy", "korat-nats")))
                return dir.FullName;
            dir = dir.Parent;
        }
        return null;
    }
}

/// <summary>
/// 031-relay-confidentiality (N-1a): verifies NATS per-subject authorization config and
/// the cloud <see cref="NatsUrl"/> / <see cref="NatsRelayBackplane"/> NKey wiring.
///
/// Uses a shared <see cref="NatsAuthzFixture"/> (IClassFixture) so the NATS container
/// is created once per class — avoids Testcontainers Ryuk-reaper races.
///
/// A6a — anonymous clients are rejected.
/// A6b — NKey clients can pub/sub relay subjects.
/// A6c — <see cref="NatsRelayBackplane"/> round-trips frames with authz-enabled broker.
///
/// Docker is required. If the fixture container fails to start the tests are dynamically
/// skipped (xunit SkipException).
/// </summary>
[Trait("Category", "Docker")]
public sealed class NatsAuthzTests : IClassFixture<NatsAuthzFixture>
{
    private readonly NatsAuthzFixture _fixture;
    private readonly ITestOutputHelper _output;
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan OpTimeout = TimeSpan.FromSeconds(5);

    private const string DockerSkipReason =
        "SKIPPED: Docker unavailable — relay authz invariants not checked " +
        "(NatsAuthzTests requires a running Docker daemon with NATS container).";

    public NatsAuthzTests(NatsAuthzFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _output = output;
    }

    // ── A6a: anonymous connect/publish fails ──────────────────────────────────

    /// <summary>
    /// An anonymous CONNECT to the authz-enabled broker must fail: no <c>no_auth_user</c>
    /// means the server rejects connections that present no credentials.
    /// </summary>
    [Fact]
    public async Task Nats_AnonymousClient_CannotConnectOrPublishRelaySubjects()
    {
        SkipIfNoDocker();

        var opts = NatsOpts.Default with
        {
            Url = _fixture.NatsUrl,
            // No AuthOpts => anonymous.
            RetryOnInitialConnect = false,
            MaxReconnectRetry = 0,
        };

        bool failed = false;
        try
        {
            await using var conn = new NatsConnection(opts);
            using var cts = new CancellationTokenSource(ConnectTimeout);
            await conn.ConnectAsync();
            // If connected, try to publish — should also fail / get permission error.
            using var pubCts = new CancellationTokenSource(OpTimeout);
            await conn.PublishAsync("korat.relay.frame.test", new byte[] { 0x01 },
                cancellationToken: pubCts.Token);
        }
        catch (Exception)
        {
            failed = true;
        }

        Assert.True(failed, "Anonymous connection/publish should fail on authz-enabled broker.");
    }

    // ── A6b: NKey client allowed on relay subjects ────────────────────────────

    /// <summary>
    /// An NKey-authenticated client can successfully publish and receive on relay subjects.
    /// Uses two separate connections: one subscriber, one publisher (NATS does not
    /// loopback a message to the same connection that published it).
    /// </summary>
    [Fact]
    public async Task Nats_NkeyClient_AllowedOnRelaySubjects_CanPubSubAndReceive()
    {
        SkipIfNoDocker();

        var opts = NatsUrl.ToOpts(_fixture.NatsUrl, name: "test-nkey", nkeySeed: _fixture.NkeySeed);
        await using var subConn = new NatsConnection(opts);
        await using var pubConn = new NatsConnection(opts);

        using var connectCts = new CancellationTokenSource(ConnectTimeout);
        await subConn.ConnectAsync();
        await pubConn.ConnectAsync();

        var nodeId = NodeId.New();
        var subject = NatsSubjects.Frame(nodeId);
        await using var sub = await subConn.SubscribeCoreAsync<byte[]>(subject, cancellationToken: connectCts.Token);

        // Small settle time to ensure SUB is registered at the broker before publishing.
        await Task.Delay(150);

        var payload = new byte[] { 0xCA, 0xFE };
        using var pubCts = new CancellationTokenSource(OpTimeout);
        await pubConn.PublishAsync(subject, payload, cancellationToken: pubCts.Token);

        // Receive — the message must arrive because both publish + subscribe are allowed.
        using var recvCts = new CancellationTokenSource(OpTimeout);
        NatsMsg<byte[]>? received = null;
        await foreach (var msg in sub.Msgs.ReadAllAsync(recvCts.Token))
        {
            received = msg;
            break;
        }

        Assert.NotNull(received);
        Assert.Equal(payload, received.Value.Data);

        // Connection remains alive after a successful relay-subject round-trip.
        using var pingCts = new CancellationTokenSource(OpTimeout);
        await subConn.PingAsync(pingCts.Token);
    }

    // ── A6c: NatsRelayBackplane end-to-end with authz broker ──────────────────

    /// <summary>
    /// <see cref="NatsRelayBackplane"/> successfully relays a <see cref="GatewayToNodeMessage"/>
    /// frame end-to-end when authenticated with an NKey seed against the authz broker.
    /// </summary>
    [Fact]
    public async Task NatsRelayBackplane_WithNkeyAuth_RelaysFrames()
    {
        SkipIfNoDocker();

        var opts = NatsUrl.ToOpts(_fixture.NatsUrl, name: "test-backplane", nkeySeed: _fixture.NkeySeed);
        await using var publisherConn = new NatsConnection(opts);
        await using var peerConn = new NatsConnection(opts);

        using var connectCts = new CancellationTokenSource(ConnectTimeout);
        await publisherConn.ConnectAsync();
        await peerConn.ConnectAsync();

        var backplane = new NatsRelayBackplane(
            publisherConn,
            NullLogger<NatsRelayBackplane>.Instance);

        var nodeId = NodeId.New();
        var received = new TaskCompletionSource<GatewayToNodeMessage>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        // Subscribe the "peer node" (would be on a different silo) on its own connection.
        await using var peerSub = await peerConn.SubscribeCoreAsync<byte[]>(
            NatsSubjects.Frame(nodeId), cancellationToken: connectCts.Token);

        using var loopCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var peerLoop = Task.Run(async () =>
        {
            await foreach (var msg in peerSub.Msgs.ReadAllAsync(loopCts.Token))
            {
                if (msg.Data is { Length: > 0 } data)
                {
                    received.TrySetResult(GatewayToNodeMessage.Parser.ParseFrom(data));
                    break;
                }
            }
        });

        // Settle: ensure SUB is registered at the broker.
        await Task.Delay(150);

        var frame = new RelayFrame
        {
            SessionId = Guid.NewGuid().ToString("N"),
            SequenceNumber = 42,
            Direction = "client_to_server",
            Ciphertext = ByteString.CopyFromUtf8("nats-authz-test-payload"),
        };
        var envelope = new GatewayToNodeMessage { Frame = frame };

        using var pubCts = new CancellationTokenSource(OpTimeout);
        var published = await backplane.PublishToNodeAsync(nodeId, envelope, pubCts.Token);
        Assert.True(published, "Backplane should successfully publish to a relay frame subject.");

        using var receiveCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var receivedEnvelope = await received.Task.WaitAsync(receiveCts.Token);

        Assert.Equal(GatewayToNodeMessage.PayloadOneofCase.Frame, receivedEnvelope.PayloadCase);
        Assert.Equal(frame.SessionId, receivedEnvelope.Frame.SessionId);
        Assert.Equal(42ul, receivedEnvelope.Frame.SequenceNumber);
        Assert.Equal("nats-authz-test-payload", receivedEnvelope.Frame.Ciphertext.ToStringUtf8());

        await loopCts.CancelAsync();
        await peerSub.DisposeAsync();
        try { await peerLoop; } catch { /* loop ended by cancellation */ }
    }

    // ── NatsUrl.ToOpts unit cases (no Docker required) ────────────────────────

    [Fact]
    public void NatsUrl_WithNkeySeed_SetsBothNKeyAndSeed()
    {
        var kp = KeyPair.CreatePair(PrefixByte.User);
        var seed = kp.GetSeed();
        var expectedPub = kp.GetPublicKey();

        var opts = NatsUrl.ToOpts("nats://localhost:4222", "test", nkeySeed: seed);

        // NATS NKey auth requires BOTH: the public key (to look up permissions)
        // and the seed (for nonce-signing).  NatsUrl.ToOpts derives NKey from the seed.
        Assert.Equal(expectedPub, opts.AuthOpts.NKey);
        Assert.Equal(seed, opts.AuthOpts.Seed);
    }

    [Fact]
    public void NatsUrl_WithoutNkeySeed_AnonymousAuthOpts()
    {
        var opts = NatsUrl.ToOpts("nats://localhost:4222", "test");
        Assert.True(string.IsNullOrEmpty(opts.AuthOpts.Seed));
        Assert.True(string.IsNullOrEmpty(opts.AuthOpts.NKey));
    }

    [Fact]
    public void NatsUrl_WithWhitespaceNkeySeed_AnonymousAuthOpts()
    {
        var opts = NatsUrl.ToOpts("nats://localhost:4222", "test", nkeySeed: "   ");
        Assert.True(string.IsNullOrEmpty(opts.AuthOpts.Seed));
        Assert.True(string.IsNullOrEmpty(opts.AuthOpts.NKey));
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Dynamically skips the current test when the shared NATS container failed to start
    /// (Docker unavailable, Ryuk reaper error, etc.).  The fixture's <c>NatsUrl</c> is null
    /// when the container did not start.
    /// Emits a loud, clearly visible skip reason to both the test output and stderr so a
    /// local run does not silently appear to pass.
    /// </summary>
    private void SkipIfNoDocker()
    {
        if (string.IsNullOrEmpty(_fixture.NatsUrl))
        {
            _output.WriteLine(DockerSkipReason);
            Console.Error.WriteLine($"[SKIP] {DockerSkipReason}");
            throw Xunit.Sdk.SkipException.ForSkip(DockerSkipReason);
        }
    }
}
