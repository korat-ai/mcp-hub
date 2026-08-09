using System.Net;
using System.Text;
using System.Text.Json;
using Korat.Cli.Auth;
using Korat.Cli.Commands;

namespace Korat.Cli.Tests;

/// <summary>
/// Tests for <c>korat doctor</c>: local checks (credentials, env-coherence, service) plus
/// cloud/network checks (cloud-auth, node-presence, agents-stale, grpc-gateway, version).
/// Uses temp-dir <see cref="CredentialStore"/>/<see cref="LocalIdentityStore"/> so the real
/// <c>~/.korat</c> is never touched (same idiom as <see cref="LoginCommandTests"/>), and a
/// canned-response <see cref="HttpMessageHandler"/> plus an injectable TCP probe so NO test
/// touches the real network (same fake-handler idiom as <see cref="SpaceDiscoveryTests"/>).
/// </summary>
public class DoctorCommandTests
{
    /// <param name="refresh">
    ///   Шов обновления пропуска. По умолчанию — «обновить не вышло», и без сети: настоящий
    ///   обмен ходил бы к живому провайдеру из теста, стоило бы кому-нибудь дописать сюда
    ///   истёкший пропуск с обновляющим токеном.
    /// </param>
    private static (string TempDir, CredentialStore CredStore, LocalIdentityStore IdStore) NewStores(
        CredentialStore.RefreshDelegate? refresh = null)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var credStore = new CredentialStore(
            tempDir,
            refresh ?? ((_, _) => Task.FromResult<CliCredentials?>(null)));
        var idStore = new LocalIdentityStore(Path.Combine(tempDir, "config.json"));
        return (tempDir, credStore, idStore);
    }

    private static void Cleanup(string tempDir)
    {
        if (Directory.Exists(tempDir))
            Directory.Delete(tempDir, recursive: true);
    }

    // ── Fake HTTP / TCP seams ───────────────────────────────────────────────────

    private sealed class RoutedHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(responder(request));
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
            => throw new HttpRequestException("stub offline — simulated network failure");
    }

    private static Task<bool> AlwaysReachableProbe(string host, int port, TimeSpan timeout) => Task.FromResult(true);
    private static Task<bool> NeverReachableProbe(string host, int port, TimeSpan timeout) => Task.FromResult(false);

    private static HttpResponseMessage JsonResponse(HttpStatusCode code, string body) =>
        new(code) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    /// <summary>
    /// One node entry for the canned <c>/api/space</c> body — mirrors the server's wire shape
    /// (<c>id.value</c>, camelCase props).
    /// </summary>
    private static string NodeJson(string id, string displayName, string kind, DateTimeOffset? lastSeenAt) =>
        $$"""{"id":{"value":"{{id}}"},"displayName":"{{displayName}}","status":"Online","lastSeenAt":{{(lastSeenAt is { } t ? $"\"{t:o}\"" : "null")}},"kind":"{{kind}}"}""";

    /// <summary>
    /// Builds a canned handler that answers <c>/api/auth/me</c>, <c>/api/space</c>, and the
    /// GitHub version-redirect HEAD request all with plausible "everything is fine" responses,
    /// so tests that only care about the LOCAL checks don't have their exit code flipped by an
    /// unrelated network check failing. <paramref name="ownNodeId"/> is placed in
    /// <c>nodes[]</c> as an online node so "node-presence" reports ok; extra nodes (e.g. a
    /// stale agent) can be appended via <paramref name="extraNodes"/>.
    /// </summary>
    private static HttpMessageHandler HealthyHandler(
        string ownNodeId,
        string email = "owner@example.com",
        DateTimeOffset? ownLastSeenAt = null,
        IEnumerable<string>? extraNodes = null,
        int presenceStaleSeconds = 120,
        bool includeVersionRedirect = true,
        DateTimeOffset? serverTime = null,
        string versionRedirectTarget = "v0.0.0-dev")
    {
        var nodes = new List<string> { NodeJson(ownNodeId, "this-node", "publisher", ownLastSeenAt ?? DateTimeOffset.UtcNow) };
        if (extraNodes is not null)
            nodes.AddRange(extraNodes);

        // A3 (final-review): serverTime defaults to real UtcNow so every EXISTING test
        // (whose ownLastSeenAt/extraNodes are computed relative to the real clock) keeps
        // behaving exactly as before. Tests exercising the clock-skew fix pass an explicit,
        // deliberately-skewed serverTime instead.
        var effectiveServerTime = serverTime ?? DateTimeOffset.UtcNow;
        var spaceJson = $$"""
            {"displayName":"Test Space","presenceStaleSeconds":{{presenceStaleSeconds}},
             "serverTime":"{{effectiveServerTime:o}}",
             "nodes":[{{string.Join(",", nodes)}}],"mcpServers":[]}
            """;

        return new RoutedHandler(req =>
        {
            var uri = req.RequestUri!;
            if (uri.AbsolutePath == "/api/auth/me")
                return JsonResponse(HttpStatusCode.OK, $$"""{"email":"{{email}}"}""");
            if (uri.AbsolutePath == "/api/space")
                return JsonResponse(HttpStatusCode.OK, spaceJson);
            if (uri.Host.Contains("github", StringComparison.OrdinalIgnoreCase))
            {
                if (!includeVersionRedirect)
                    return new HttpResponseMessage(HttpStatusCode.NotFound);

                // Version-resolve HEAD request. The exact version doesn't matter to most
                // assertions below — a mismatch just makes "version" report "warn", which
                // (by design) never fails the report or flips the exit code. Tests that DO
                // care about the exact "warn" text pass an explicit versionRedirectTarget.
                var resp = new HttpResponseMessage(HttpStatusCode.Found);
                resp.Headers.Location = new Uri(
                    $"https://github.com/korat-ai/homebrew-tap/releases/download/{versionRedirectTarget}/SHA256SUMS");
                return resp;
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });
    }

    // ── Local checks (credentials / env-coherence / service) ───────────────────

    [Fact]
    public async Task Doctor_matched_urls_reports_credentials_and_env_coherence_ok()
    {
        var (tempDir, credStore, idStore) = NewStores();
        try
        {
            await credStore.SaveAsync(new CliCredentials(
                AccessToken: "korat_cli_test_token",
                Scope: "full",
                ExpiresAt: DateTimeOffset.UtcNow.AddDays(90),
                CloudUrl: "https://cloud.example.com"));

            var identity = idStore.LoadOrCreate();
            identity.CloudUrl = "https://cloud.example.com";
            idStore.Save(identity);

            var output = new StringWriter();
            var exitCode = await DoctorCommand.RunAsync(
                json: false,
                credentialStore: credStore,
                identityStore: idStore,
                handlerOverride: HealthyHandler(identity.NodeId),
                tcpProbe: AlwaysReachableProbe,
                outputWriter: output,
                ct: default);

            var printed = output.ToString();
            Assert.Contains("✅ credentials", printed);
            Assert.Contains("✅ env-coherence", printed);
            // Service is host-dependent (installed or not on the test machine) but must never
            // fail the whole report on its own — only credentials/env-coherence do here.
            Assert.Equal(0, exitCode);
        }
        finally
        {
            Cleanup(tempDir);
        }
    }

    [Fact]
    public async Task Doctor_mismatched_urls_reports_env_coherence_fail_with_both_urls()
    {
        var (tempDir, credStore, idStore) = NewStores();
        try
        {
            await credStore.SaveAsync(new CliCredentials(
                AccessToken: "korat_cli_test_token",
                Scope: "full",
                ExpiresAt: DateTimeOffset.UtcNow.AddDays(90),
                CloudUrl: "https://my.korat.ai"));

            var identity = idStore.LoadOrCreate();
            identity.CloudUrl = "https://my.korat.dev";
            idStore.Save(identity);

            var output = new StringWriter();
            var exitCode = await DoctorCommand.RunAsync(
                json: false,
                credentialStore: credStore,
                identityStore: idStore,
                handlerOverride: HealthyHandler(identity.NodeId),
                tcpProbe: AlwaysReachableProbe,
                outputWriter: output,
                ct: default);

            var printed = output.ToString();
            Assert.Contains("❌ env-coherence", printed);
            Assert.Contains("https://my.korat.ai", printed);
            Assert.Contains("https://my.korat.dev", printed);
            Assert.Contains("korat login --cloud", printed);
            Assert.Equal(1, exitCode);
        }
        finally
        {
            Cleanup(tempDir);
        }
    }

    [Fact]
    public async Task Doctor_missing_credentials_reports_fail_and_exit_1()
    {
        var (tempDir, credStore, idStore) = NewStores();
        try
        {
            var output = new StringWriter();
            var exitCode = await DoctorCommand.RunAsync(
                json: false,
                credentialStore: credStore,
                identityStore: idStore,
                handlerOverride: new ThrowingHandler(),
                tcpProbe: NeverReachableProbe,
                outputWriter: output,
                ct: default);

            var printed = output.ToString();
            Assert.Contains("❌ credentials", printed);
            Assert.Contains("korat login", printed);
            // No credentials → nothing to compare → env-coherence must not be reported.
            Assert.DoesNotContain("env-coherence", printed);
            Assert.Equal(1, exitCode);
        }
        finally
        {
            Cleanup(tempDir);
        }
    }

    [Fact]
    public async Task Doctor_expired_credentials_reports_fail_and_exit_1()
    {
        var (tempDir, credStore, idStore) = NewStores();
        try
        {
            await credStore.SaveAsync(new CliCredentials(
                AccessToken: "korat_cli_test_token",
                Scope: "full",
                ExpiresAt: DateTimeOffset.UtcNow.AddDays(-1),
                CloudUrl: "https://cloud.example.com"));

            var output = new StringWriter();
            var exitCode = await DoctorCommand.RunAsync(
                json: false,
                credentialStore: credStore,
                identityStore: idStore,
                handlerOverride: new ThrowingHandler(),
                tcpProbe: NeverReachableProbe,
                outputWriter: output,
                ct: default);

            var printed = output.ToString();
            Assert.Contains("❌ credentials", printed);
            Assert.Contains("expired", printed, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(1, exitCode);
        }
        finally
        {
            Cleanup(tempDir);
        }
    }

    [Fact]
    public async Task Doctor_renews_an_expired_token_before_judging_it()
    {
        // Доктор читает пропуск через хранилище, а оно обновляет истёкший само. Пропуск,
        // который вот-вот обновится, — это не поломка, и доктор не должен звать войти
        // заново из-за состояния, которое чинится без человека.
        var renewed = new CliCredentials(
            AccessToken: "eyJhbG.renewed.sig",
            Scope: "openid email offline_access",
            ExpiresAt: DateTimeOffset.UtcNow.AddHours(1),
            CloudUrl: "https://cloud.example.com",
            RefreshToken: "refresh-2",
            Issuer: "https://id.example.test/");

        var (tempDir, credStore, idStore) = NewStores(
            refresh: (_, _) => Task.FromResult<CliCredentials?>(renewed));
        try
        {
            await credStore.SaveAsync(new CliCredentials(
                AccessToken: "eyJhbG.stale.sig",
                Scope: "openid email offline_access",
                ExpiresAt: DateTimeOffset.UtcNow.AddMinutes(-5),
                CloudUrl: "https://cloud.example.com",
                RefreshToken: "refresh-1",
                Issuer: "https://id.example.test/"));

            var identity = idStore.LoadOrCreate();
            identity.CloudUrl = "https://cloud.example.com";
            idStore.Save(identity);

            var output = new StringWriter();
            var exitCode = await DoctorCommand.RunAsync(
                json: false,
                credentialStore: credStore,
                identityStore: idStore,
                handlerOverride: HealthyHandler(identity.NodeId),
                tcpProbe: AlwaysReachableProbe,
                outputWriter: output,
                ct: default);

            var printed = output.ToString();
            Assert.Contains("✅ credentials", printed);
            Assert.Contains("renews automatically", printed);
            Assert.Equal(0, exitCode);
        }
        finally
        {
            Cleanup(tempDir);
        }
    }

    [Fact]
    public async Task Doctor_says_renewal_failed_rather_than_just_expired()
    {
        // Обновление стоит на пути чтения, поэтому истёкший пропуск ЗДЕСЬ означает «не
        // обновилось». Сказать просто «истёк» — значит спрятать то единственное, что
        // человеку нужно проверить: доступен ли провайдер и жива ли ещё сессия.
        var (tempDir, credStore, idStore) = NewStores();
        try
        {
            await credStore.SaveAsync(new CliCredentials(
                AccessToken: "eyJhbG.stale.sig",
                Scope: "openid email offline_access",
                ExpiresAt: DateTimeOffset.UtcNow.AddMinutes(-5),
                CloudUrl: "https://cloud.example.com",
                RefreshToken: "refresh-1",
                Issuer: "https://id.example.test/"));

            var output = new StringWriter();
            var exitCode = await DoctorCommand.RunAsync(
                json: false,
                credentialStore: credStore,
                identityStore: idStore,
                handlerOverride: new ThrowingHandler(),
                tcpProbe: NeverReachableProbe,
                outputWriter: output,
                ct: default);

            var printed = output.ToString();
            Assert.Contains("❌ credentials", printed);
            Assert.Contains("renewal did not succeed", printed);
            Assert.Equal(1, exitCode);
        }
        finally
        {
            Cleanup(tempDir);
        }
    }

    [Fact]
    public async Task Doctor_never_prints_the_refresh_token_either()
    {
        // Обновляющий токен — секрет длиннее самого пропуска: он позволяет выписывать
        // новые пропуска, пока жив. В отчёт он не попадает так же, как и пропуск.
        var (tempDir, credStore, idStore) = NewStores();
        try
        {
            const string refreshToken = "refresh-super-secret-value";
            await credStore.SaveAsync(new CliCredentials(
                AccessToken: "eyJhbG.access.sig",
                Scope: "openid email offline_access",
                ExpiresAt: DateTimeOffset.UtcNow.AddHours(1),
                CloudUrl: "https://cloud.example.com",
                RefreshToken: refreshToken,
                Issuer: "https://id.example.test/"));

            var identity = idStore.LoadOrCreate();
            identity.CloudUrl = "https://cloud.example.com";
            idStore.Save(identity);

            var output = new StringWriter();
            await DoctorCommand.RunAsync(
                json: false,
                credentialStore: credStore,
                identityStore: idStore,
                handlerOverride: HealthyHandler(identity.NodeId),
                tcpProbe: AlwaysReachableProbe,
                outputWriter: output,
                ct: default);

            Assert.DoesNotContain(refreshToken, output.ToString());
        }
        finally
        {
            Cleanup(tempDir);
        }
    }

    [Fact]
    public async Task Doctor_reports_no_credentials_when_the_file_predates_the_sign_in_provider()
    {
        // Файл от прежней версии CLI не несёт пригодного пропуска. Доктор обязан прочитать
        // это как «не входил» и позвать войти, а не как «всё в порядке».
        var (tempDir, credStore, idStore) = NewStores();
        try
        {
            await File.WriteAllTextAsync(
                Path.Combine(tempDir, "credentials"),
                """{"cliToken":"korat_cli_old","scope":"full","expiresAt":"2126-01-01T00:00:00+00:00","cloudUrl":"https://cloud.example.com"}""");

            var output = new StringWriter();
            var exitCode = await DoctorCommand.RunAsync(
                json: false,
                credentialStore: credStore,
                identityStore: idStore,
                handlerOverride: new ThrowingHandler(),
                tcpProbe: NeverReachableProbe,
                outputWriter: output,
                ct: default);

            var printed = output.ToString();
            Assert.Contains("❌ credentials", printed);
            Assert.Contains("korat login", printed);
            Assert.Equal(1, exitCode);
        }
        finally
        {
            Cleanup(tempDir);
        }
    }

    [Fact]
    public async Task Doctor_never_prints_cli_token_value()
    {
        var (tempDir, credStore, idStore) = NewStores();
        try
        {
            const string token = "korat_cli_super_secret_token";
            await credStore.SaveAsync(new CliCredentials(
                AccessToken: token,
                Scope: "full",
                ExpiresAt: DateTimeOffset.UtcNow.AddDays(90),
                CloudUrl: "https://cloud.example.com"));

            var identity = idStore.LoadOrCreate();
            identity.CloudUrl = "https://cloud.example.com";
            idStore.Save(identity);

            var output = new StringWriter();
            await DoctorCommand.RunAsync(
                json: false,
                credentialStore: credStore,
                identityStore: idStore,
                handlerOverride: HealthyHandler(identity.NodeId),
                tcpProbe: AlwaysReachableProbe,
                outputWriter: output,
                ct: default);

            Assert.DoesNotContain(token, output.ToString());
        }
        finally
        {
            Cleanup(tempDir);
        }
    }

    [Fact]
    public async Task Doctor_json_shape_has_ok_and_checks_with_id_status_detail()
    {
        var (tempDir, credStore, idStore) = NewStores();
        try
        {
            await credStore.SaveAsync(new CliCredentials(
                AccessToken: "korat_cli_test_token",
                Scope: "full",
                ExpiresAt: DateTimeOffset.UtcNow.AddDays(90),
                CloudUrl: "https://cloud.example.com"));

            var identity = idStore.LoadOrCreate();
            identity.CloudUrl = "https://cloud.example.com";
            idStore.Save(identity);

            var output = new StringWriter();
            var exitCode = await DoctorCommand.RunAsync(
                json: true,
                credentialStore: credStore,
                identityStore: idStore,
                handlerOverride: HealthyHandler(identity.NodeId),
                tcpProbe: AlwaysReachableProbe,
                outputWriter: output,
                ct: default);

            var doc = JsonDocument.Parse(output.ToString());
            var root = doc.RootElement;
            Assert.True(root.TryGetProperty("ok", out var okProp));
            Assert.True(okProp.GetBoolean());
            Assert.Equal(0, exitCode);

            Assert.True(root.TryGetProperty("checks", out var checksProp));
            Assert.True(checksProp.GetArrayLength() > 0);
            foreach (var check in checksProp.EnumerateArray())
            {
                Assert.True(check.TryGetProperty("id", out _));
                Assert.True(check.TryGetProperty("status", out var statusProp));
                Assert.True(check.TryGetProperty("detail", out _));
                var status = statusProp.GetString();
                Assert.True(status is "ok" or "warn" or "fail");
            }

            Assert.Contains(checksProp.EnumerateArray(), c => c.GetProperty("id").GetString() == "credentials");
            Assert.Contains(checksProp.EnumerateArray(), c => c.GetProperty("id").GetString() == "env-coherence");
            Assert.Contains(checksProp.EnumerateArray(), c => c.GetProperty("id").GetString() == "service");
            Assert.Contains(checksProp.EnumerateArray(), c => c.GetProperty("id").GetString() == "cloud-auth");
            Assert.Contains(checksProp.EnumerateArray(), c => c.GetProperty("id").GetString() == "node-presence");
            Assert.Contains(checksProp.EnumerateArray(), c => c.GetProperty("id").GetString() == "grpc-gateway");
            Assert.Contains(checksProp.EnumerateArray(), c => c.GetProperty("id").GetString() == "version");
        }
        finally
        {
            Cleanup(tempDir);
        }
    }

    [Fact]
    public async Task Doctor_json_output_never_contains_cli_token_value()
    {
        var (tempDir, credStore, idStore) = NewStores();
        try
        {
            const string token = "korat_cli_super_secret_token";
            await credStore.SaveAsync(new CliCredentials(
                AccessToken: token,
                Scope: "full",
                ExpiresAt: DateTimeOffset.UtcNow.AddDays(90),
                CloudUrl: "https://cloud.example.com"));

            var output = new StringWriter();
            await DoctorCommand.RunAsync(
                json: true,
                credentialStore: credStore,
                identityStore: idStore,
                handlerOverride: new ThrowingHandler(),
                tcpProbe: NeverReachableProbe,
                outputWriter: output,
                ct: default);

            Assert.DoesNotContain(token, output.ToString());
        }
        finally
        {
            Cleanup(tempDir);
        }
    }

    // ── HTTP timeouts (final-review LOW fix) ────────────────────────────────────

    [Fact]
    public void BuildAuthenticatedHttpClient_sets_a_bounded_timeout()
    {
        // Final-review LOW fix: the doctor's cloud-facing HttpClient previously had no
        // explicit Timeout, so a blackholed network (no RST, no response) left `korat
        // doctor` hanging for the BCL's 100s default per check. Asserted directly against
        // the client's Timeout property so this test is instant — no real hang required.
        var creds = new CliCredentials(
            AccessToken: "korat_cli_test_token", Scope: "full",
            ExpiresAt: DateTimeOffset.UtcNow.AddDays(90), CloudUrl: "https://cloud.example.com");

        using var http = DoctorCommand.BuildAuthenticatedHttpClient(creds, new ThrowingHandler());

        Assert.True(http.Timeout <= TimeSpan.FromSeconds(10));
        Assert.True(http.Timeout > TimeSpan.Zero);
    }

    // ── cloud-auth ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Doctor_cloud_auth_ok_includes_email_in_detail()
    {
        var (tempDir, credStore, idStore) = NewStores();
        try
        {
            await credStore.SaveAsync(new CliCredentials(
                AccessToken: "korat_cli_test_token", Scope: "full",
                ExpiresAt: DateTimeOffset.UtcNow.AddDays(90), CloudUrl: "https://cloud.example.com"));
            var identity = idStore.LoadOrCreate();
            identity.CloudUrl = "https://cloud.example.com";
            idStore.Save(identity);

            var output = new StringWriter();
            await DoctorCommand.RunAsync(
                json: false, credentialStore: credStore, identityStore: idStore,
                handlerOverride: HealthyHandler(identity.NodeId, email: "owner@korat.dev"),
                tcpProbe: AlwaysReachableProbe, outputWriter: output, ct: default);

            var printed = output.ToString();
            Assert.Contains("✅ cloud-auth", printed);
            Assert.Contains("owner@korat.dev", printed);
        }
        finally
        {
            Cleanup(tempDir);
        }
    }

    [Fact]
    public async Task Doctor_cloud_auth_401_reports_fail_with_relogin_hint()
    {
        var (tempDir, credStore, idStore) = NewStores();
        try
        {
            await credStore.SaveAsync(new CliCredentials(
                AccessToken: "korat_cli_test_token", Scope: "full",
                ExpiresAt: DateTimeOffset.UtcNow.AddDays(90), CloudUrl: "https://cloud.example.com"));
            var identity = idStore.LoadOrCreate();
            identity.CloudUrl = "https://cloud.example.com";
            idStore.Save(identity);

            var handler = new RoutedHandler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized));

            var output = new StringWriter();
            var exitCode = await DoctorCommand.RunAsync(
                json: false, credentialStore: credStore, identityStore: idStore,
                handlerOverride: handler, tcpProbe: AlwaysReachableProbe, outputWriter: output, ct: default);

            var printed = output.ToString();
            Assert.Contains("❌ cloud-auth", printed);
            Assert.Contains("not valid for this cloud", printed);
            Assert.Contains("korat login", printed);
            Assert.Equal(1, exitCode);
        }
        finally
        {
            Cleanup(tempDir);
        }
    }

    [Fact]
    public async Task Doctor_cloud_auth_network_error_reports_fail_unreachable()
    {
        var (tempDir, credStore, idStore) = NewStores();
        try
        {
            await credStore.SaveAsync(new CliCredentials(
                AccessToken: "korat_cli_test_token", Scope: "full",
                ExpiresAt: DateTimeOffset.UtcNow.AddDays(90), CloudUrl: "https://cloud.example.com"));

            var output = new StringWriter();
            var exitCode = await DoctorCommand.RunAsync(
                json: false, credentialStore: credStore, identityStore: idStore,
                handlerOverride: new ThrowingHandler(), tcpProbe: NeverReachableProbe,
                outputWriter: output, ct: default);

            var printed = output.ToString();
            Assert.Contains("❌ cloud-auth", printed);
            Assert.Contains("unreachable", printed);
            Assert.Equal(1, exitCode);
        }
        finally
        {
            Cleanup(tempDir);
        }
    }

    // ── node-presence ────────────────────────────────────────────────────────

    [Fact]
    public async Task Doctor_node_presence_own_node_fresh_reports_ok()
    {
        var (tempDir, credStore, idStore) = NewStores();
        try
        {
            await credStore.SaveAsync(new CliCredentials(
                AccessToken: "korat_cli_test_token", Scope: "full",
                ExpiresAt: DateTimeOffset.UtcNow.AddDays(90), CloudUrl: "https://cloud.example.com"));
            var identity = idStore.LoadOrCreate();
            identity.CloudUrl = "https://cloud.example.com";
            idStore.Save(identity);

            var output = new StringWriter();
            var exitCode = await DoctorCommand.RunAsync(
                json: false, credentialStore: credStore, identityStore: idStore,
                handlerOverride: HealthyHandler(identity.NodeId), tcpProbe: AlwaysReachableProbe,
                outputWriter: output, ct: default);

            var printed = output.ToString();
            Assert.Contains("✅ runtime-presence", printed);
            Assert.Equal(0, exitCode);
        }
        finally
        {
            Cleanup(tempDir);
        }
    }

    [Fact]
    public async Task Doctor_node_presence_absent_reports_fail()
    {
        var (tempDir, credStore, idStore) = NewStores();
        try
        {
            await credStore.SaveAsync(new CliCredentials(
                AccessToken: "korat_cli_test_token", Scope: "full",
                ExpiresAt: DateTimeOffset.UtcNow.AddDays(90), CloudUrl: "https://cloud.example.com"));
            var identity = idStore.LoadOrCreate();
            identity.CloudUrl = "https://cloud.example.com";
            idStore.Save(identity);

            // Own node is NOT the one seeded in the canned /api/space response.
            var handler = HealthyHandler("some-other-node-id-not-this-one");

            var output = new StringWriter();
            var exitCode = await DoctorCommand.RunAsync(
                json: false, credentialStore: credStore, identityStore: idStore,
                handlerOverride: handler, tcpProbe: AlwaysReachableProbe, outputWriter: output, ct: default);

            var printed = output.ToString();
            Assert.Contains("❌ runtime-presence", printed);
            Assert.Contains("publisher runtime is not present in the Space", printed);
            Assert.Contains("korat service install", printed);
            Assert.Equal(1, exitCode);
        }
        finally
        {
            Cleanup(tempDir);
        }
    }

    [Fact]
    public async Task Doctor_node_presence_stale_reports_fail_offline()
    {
        var (tempDir, credStore, idStore) = NewStores();
        try
        {
            await credStore.SaveAsync(new CliCredentials(
                AccessToken: "korat_cli_test_token", Scope: "full",
                ExpiresAt: DateTimeOffset.UtcNow.AddDays(90), CloudUrl: "https://cloud.example.com"));
            var identity = idStore.LoadOrCreate();
            identity.CloudUrl = "https://cloud.example.com";
            idStore.Save(identity);

            // Own node IS in the space, but its lastSeenAt is far older than presenceStaleSeconds.
            var handler = HealthyHandler(identity.NodeId,
                ownLastSeenAt: DateTimeOffset.UtcNow.AddHours(-2), presenceStaleSeconds: 120);

            var output = new StringWriter();
            var exitCode = await DoctorCommand.RunAsync(
                json: false, credentialStore: credStore, identityStore: idStore,
                handlerOverride: handler, tcpProbe: AlwaysReachableProbe, outputWriter: output, ct: default);

            var printed = output.ToString();
            Assert.Contains("❌ runtime-presence", printed);
            Assert.Contains("offline", printed);
            Assert.Equal(1, exitCode);
        }
        finally
        {
            Cleanup(tempDir);
        }
    }

    [Fact]
    public async Task Doctor_node_presence_freshness_uses_server_time_not_local_clock()
    {
        // Final-review LOW fix: node-presence freshness must be computed against the
        // cloud's serverTime, not this machine's local UtcNow — a skewed local clock
        // would otherwise make a genuinely-fresh node look offline (or vice versa).
        //
        // serverTime is pinned an hour in the PAST relative to the real wall clock, and
        // the node's lastSeenAt is only 10s before THAT serverTime (i.e. "just now" per
        // the cloud's own clock). If the fix used local UtcNow instead, the computed age
        // would be ~1h10s — well past presenceStaleSeconds — and the check would
        // (incorrectly) report offline.
        var (tempDir, credStore, idStore) = NewStores();
        try
        {
            await credStore.SaveAsync(new CliCredentials(
                AccessToken: "korat_cli_test_token", Scope: "full",
                ExpiresAt: DateTimeOffset.UtcNow.AddDays(90), CloudUrl: "https://cloud.example.com"));
            var identity = idStore.LoadOrCreate();
            identity.CloudUrl = "https://cloud.example.com";
            idStore.Save(identity);

            var skewedServerTime = DateTimeOffset.UtcNow.AddHours(-1);
            var handler = HealthyHandler(identity.NodeId,
                ownLastSeenAt: skewedServerTime.AddSeconds(-10),
                presenceStaleSeconds: 120,
                serverTime: skewedServerTime);

            var output = new StringWriter();
            var exitCode = await DoctorCommand.RunAsync(
                json: false, credentialStore: credStore, identityStore: idStore,
                handlerOverride: handler, tcpProbe: AlwaysReachableProbe, outputWriter: output, ct: default);

            var printed = output.ToString();
            Assert.Contains("✅ runtime-presence", printed);
            Assert.Contains("online", printed);
            Assert.Equal(0, exitCode);
        }
        finally
        {
            Cleanup(tempDir);
        }
    }

    // ── agents-stale ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Doctor_agents_stale_warns_for_old_agent_node_without_failing()
    {
        var (tempDir, credStore, idStore) = NewStores();
        try
        {
            await credStore.SaveAsync(new CliCredentials(
                AccessToken: "korat_cli_test_token", Scope: "full",
                ExpiresAt: DateTimeOffset.UtcNow.AddDays(90), CloudUrl: "https://cloud.example.com"));
            var identity = idStore.LoadOrCreate();
            identity.CloudUrl = "https://cloud.example.com";
            idStore.Save(identity);

            var staleAgent = NodeJson("agent-node-1", "claude-code", "agent",
                DateTimeOffset.UtcNow.AddDays(-10));
            var handler = HealthyHandler(identity.NodeId, extraNodes: [staleAgent]);

            var output = new StringWriter();
            var exitCode = await DoctorCommand.RunAsync(
                json: false, credentialStore: credStore, identityStore: idStore,
                handlerOverride: handler, tcpProbe: AlwaysReachableProbe, outputWriter: output, ct: default);

            var printed = output.ToString();
            Assert.Contains("⚠️ consumer-runtimes-stale", printed);
            Assert.Contains("claude-code", printed);
            // Warn-only — must NOT flip the exit code when everything else is healthy.
            Assert.Equal(0, exitCode);
        }
        finally
        {
            Cleanup(tempDir);
        }
    }

    [Fact]
    public async Task Doctor_agents_stale_does_not_warn_for_recent_agent_node()
    {
        var (tempDir, credStore, idStore) = NewStores();
        try
        {
            await credStore.SaveAsync(new CliCredentials(
                AccessToken: "korat_cli_test_token", Scope: "full",
                ExpiresAt: DateTimeOffset.UtcNow.AddDays(90), CloudUrl: "https://cloud.example.com"));
            var identity = idStore.LoadOrCreate();
            identity.CloudUrl = "https://cloud.example.com";
            idStore.Save(identity);

            var freshAgent = NodeJson("agent-node-1", "claude-code", "agent", DateTimeOffset.UtcNow.AddHours(-1));
            var handler = HealthyHandler(identity.NodeId, extraNodes: [freshAgent]);

            var output = new StringWriter();
            await DoctorCommand.RunAsync(
                json: false, credentialStore: credStore, identityStore: idStore,
                handlerOverride: handler, tcpProbe: AlwaysReachableProbe, outputWriter: output, ct: default);

            Assert.DoesNotContain("agents-stale", output.ToString());
        }
        finally
        {
            Cleanup(tempDir);
        }
    }

    [Fact]
    public async Task Doctor_agents_stale_uses_server_time_not_local_clock()
    {
        // Final-review LOW fix, agents-stale side of the same serverTime bug: serverTime
        // is pinned 10 days in the PAST relative to the real wall clock, and the agent's
        // lastSeenAt is only 1 hour before THAT serverTime — fresh per the cloud's clock,
        // well under StaleAgentThreshold (7 days). If the fix used local UtcNow instead,
        // the computed age would be ~10 days — past the threshold — and this would
        // (incorrectly) warn.
        var (tempDir, credStore, idStore) = NewStores();
        try
        {
            await credStore.SaveAsync(new CliCredentials(
                AccessToken: "korat_cli_test_token", Scope: "full",
                ExpiresAt: DateTimeOffset.UtcNow.AddDays(90), CloudUrl: "https://cloud.example.com"));
            var identity = idStore.LoadOrCreate();
            identity.CloudUrl = "https://cloud.example.com";
            idStore.Save(identity);

            var skewedServerTime = DateTimeOffset.UtcNow.AddDays(-10);
            var freshPerServerAgent = NodeJson(
                "agent-node-1", "claude-code", "agent", skewedServerTime.AddHours(-1));
            var handler = HealthyHandler(identity.NodeId,
                extraNodes: [freshPerServerAgent], serverTime: skewedServerTime);

            var output = new StringWriter();
            var exitCode = await DoctorCommand.RunAsync(
                json: false, credentialStore: credStore, identityStore: idStore,
                handlerOverride: handler, tcpProbe: AlwaysReachableProbe, outputWriter: output, ct: default);

            Assert.DoesNotContain("agents-stale", output.ToString());
            Assert.Equal(0, exitCode);
        }
        finally
        {
            Cleanup(tempDir);
        }
    }

    // ── grpc-gateway ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Doctor_grpc_gateway_ok_when_probe_succeeds()
    {
        var (tempDir, credStore, idStore) = NewStores();
        try
        {
            await credStore.SaveAsync(new CliCredentials(
                AccessToken: "korat_cli_test_token", Scope: "full",
                ExpiresAt: DateTimeOffset.UtcNow.AddDays(90), CloudUrl: "https://cloud.example.com"));
            var identity = idStore.LoadOrCreate();
            identity.CloudUrl = "https://cloud.example.com";
            idStore.Save(identity);

            var output = new StringWriter();
            var exitCode = await DoctorCommand.RunAsync(
                json: false, credentialStore: credStore, identityStore: idStore,
                handlerOverride: HealthyHandler(identity.NodeId), tcpProbe: AlwaysReachableProbe,
                outputWriter: output, ct: default);

            Assert.Contains("✅ grpc-gateway", output.ToString());
            Assert.Equal(0, exitCode);
        }
        finally
        {
            Cleanup(tempDir);
        }
    }

    [Fact]
    public async Task Doctor_grpc_gateway_fail_when_probe_fails()
    {
        var (tempDir, credStore, idStore) = NewStores();
        try
        {
            await credStore.SaveAsync(new CliCredentials(
                AccessToken: "korat_cli_test_token", Scope: "full",
                ExpiresAt: DateTimeOffset.UtcNow.AddDays(90), CloudUrl: "https://cloud.example.com"));
            var identity = idStore.LoadOrCreate();
            identity.CloudUrl = "https://cloud.example.com";
            idStore.Save(identity);

            var output = new StringWriter();
            var exitCode = await DoctorCommand.RunAsync(
                json: false, credentialStore: credStore, identityStore: idStore,
                handlerOverride: HealthyHandler(identity.NodeId), tcpProbe: NeverReachableProbe,
                outputWriter: output, ct: default);

            var printed = output.ToString();
            Assert.Contains("❌ grpc-gateway", printed);
            Assert.Contains("unreachable", printed);
            Assert.Contains("firewall", printed);
            Assert.Equal(1, exitCode);
        }
        finally
        {
            Cleanup(tempDir);
        }
    }

    // ── version ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task Doctor_version_warn_when_redirect_unresolvable_but_never_fails()
    {
        var (tempDir, credStore, idStore) = NewStores();
        try
        {
            await credStore.SaveAsync(new CliCredentials(
                AccessToken: "korat_cli_test_token", Scope: "full",
                ExpiresAt: DateTimeOffset.UtcNow.AddDays(90), CloudUrl: "https://cloud.example.com"));
            var identity = idStore.LoadOrCreate();
            identity.CloudUrl = "https://cloud.example.com";
            idStore.Save(identity);

            // Handler answers the cloud endpoints but returns 404 (no redirect) for the
            // version-resolve HEAD request — ResolveLatestVersionAsync degrades to null.
            var handler = HealthyHandler(identity.NodeId, includeVersionRedirect: false);

            var output = new StringWriter();
            var exitCode = await DoctorCommand.RunAsync(
                json: false, credentialStore: credStore, identityStore: idStore,
                handlerOverride: handler, tcpProbe: AlwaysReachableProbe, outputWriter: output, ct: default);

            var printed = output.ToString();
            Assert.Contains("⚠️ version", printed);
            Assert.Contains("could not check for updates", printed);
            // Version never fails the report on its own.
            Assert.Equal(0, exitCode);
        }
        finally
        {
            Cleanup(tempDir);
        }
    }

    [Fact]
    public async Task Doctor_version_warn_message_does_not_double_prefix_v()
    {
        // Final-review LOW fix: ResolveLatestVersionAsync already returns "v<version>"
        // (e.g. "v9.9.9") — the warn message must print it AS-IS, not "v{latest}" which
        // produced "vv9.9.9 available".
        var (tempDir, credStore, idStore) = NewStores();
        try
        {
            await credStore.SaveAsync(new CliCredentials(
                AccessToken: "korat_cli_test_token", Scope: "full",
                ExpiresAt: DateTimeOffset.UtcNow.AddDays(90), CloudUrl: "https://cloud.example.com"));
            var identity = idStore.LoadOrCreate();
            identity.CloudUrl = "https://cloud.example.com";
            idStore.Save(identity);

            // "v9.9.9" is guaranteed to differ from whatever CliVersion.Bare() resolves to
            // in a test build, so this deterministically exercises the "warn: newer available"
            // branch (as opposed to the "up to date" branch).
            var handler = HealthyHandler(identity.NodeId, versionRedirectTarget: "v9.9.9");

            var output = new StringWriter();
            await DoctorCommand.RunAsync(
                json: false, credentialStore: credStore, identityStore: idStore,
                handlerOverride: handler, tcpProbe: AlwaysReachableProbe, outputWriter: output, ct: default);

            var printed = output.ToString();
            Assert.Contains("⚠️ version", printed);
            Assert.Contains("v9.9.9 available", printed);
            Assert.DoesNotContain("vv9.9.9", printed);
        }
        finally
        {
            Cleanup(tempDir);
        }
    }

    // ── hosted-agent checks (claude-on-path / claude-login / agents-dir / orphans) ──
    // Review 2026-07-04 "doctor слеп к hosted-agents": all-green even when the node
    // cannot serve hosted agents (claude missing from PATH, subscription logged out, or
    // the per-agent config root not writable). These checks only fire when at least one
    // hosted agent is actually registered (config.json InferencePoints) — a node that
    // never hosts an agent has nothing to diagnose here, mirroring the 0..N "agents-stale"
    // pattern above (no baseline "ok" line clutters a report that has nothing to say).

    private static Task<bool> AlwaysLoggedInProbe(CancellationToken ct) => Task.FromResult(true);
    private static Task<bool> AlwaysLoggedOutProbe(CancellationToken ct) => Task.FromResult(false);

    // ── Offline degradation ──────────────────────────────────────────────────

    [Fact]
    public async Task Doctor_offline_degradation_local_checks_still_reported_cloud_checks_fail()
    {
        var (tempDir, credStore, idStore) = NewStores();
        try
        {
            await credStore.SaveAsync(new CliCredentials(
                AccessToken: "korat_cli_test_token", Scope: "full",
                ExpiresAt: DateTimeOffset.UtcNow.AddDays(90), CloudUrl: "https://cloud.example.com"));
            var identity = idStore.LoadOrCreate();
            identity.CloudUrl = "https://cloud.example.com";
            idStore.Save(identity);

            var output = new StringWriter();
            var exitCode = await DoctorCommand.RunAsync(
                json: false, credentialStore: credStore, identityStore: idStore,
                handlerOverride: new ThrowingHandler(), tcpProbe: NeverReachableProbe,
                outputWriter: output, ct: default);

            var printed = output.ToString();
            // Local checks unaffected by the network being down.
            Assert.Contains("✅ credentials", printed);
            Assert.Contains("✅ env-coherence", printed);
            Assert.Contains("service", printed);
            // Cloud/network checks degrade to failure.
            Assert.Contains("❌ cloud-auth", printed);
            Assert.Contains("❌ runtime-presence", printed);
            Assert.Contains("❌ grpc-gateway", printed);
            Assert.Equal(1, exitCode);
        }
        finally
        {
            Cleanup(tempDir);
        }
    }
}
