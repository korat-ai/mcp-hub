using System.Net;
using System.Text;
using Korat.Cloud.Web.Auth;
using Korat.Domain.Auth;
using Korat.Domain.Persistence;
using Korat.Persistence;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OAuth;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Orleans.Serialization;
using Orleans.TestingHost;

namespace Korat.Cloud.IntegrationTests.Auth;

/// <summary>
/// End-to-end integration tests for the GitHub OAuth sign-in flow.
///
/// These tests prove that:
///   1. GET /signin/github redirects to GitHub's authorize endpoint (not back to our callback).
///   2. The OAuth callback at /signin/github/callback is handled by the GitHub handler
///      (stubbed backchannel — no real GitHub calls) and redirects to /signin/github/finish
///      (NOT re-intercepted by the OAuth handler).
///   3. GET /signin/github/finish provisions an admin User+Space when the signing-in email
///      matches Bootstrap:AdminEmail (no invite required).
///   4. A new user whose email does NOT match Bootstrap:AdminEmail and has no invite code
///      is rejected (redirected to /app/signin).
///
/// The backchannel stub intercepts:
///   POST https://github.com/login/oauth/access_token  → dummy access-token response
///   GET  https://api.github.com/user                  → dummy profile JSON
///   GET  https://api.github.com/user/emails           → verified primary email JSON
/// </summary>
public sealed class GitHubSigninFlowTests : IClassFixture<GitHubSigninFlowFixture>
{
    private readonly GitHubSigninFlowFixture _fixture;

    public GitHubSigninFlowTests(GitHubSigninFlowFixture fixture) => _fixture = fixture;

    // ─── Test 1: Admin bootstrap via GitHub OAuth ───────────────────────────

    [Fact]
    public async Task GitHubSignin_AdminEmail_ProvisionesAdminUserAndDefaultSpace()
    {
        // Use a fresh factory for this test so DB starts clean.
        await using var factory = _fixture.CreateFactory(adminEmail: "admin@example.com", githubEmail: "admin@example.com");
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = true,
        });

        // ── Step 1: Challenge — GET /signin/github ──────────────────────────
        var challengeResp = await client.GetAsync("/signin/github");
        Assert.Equal(HttpStatusCode.Redirect, challengeResp.StatusCode);

        var authorizeUrl = challengeResp.Headers.Location!;
        Assert.Equal("github.com", authorizeUrl.Host);
        Assert.Equal("/login/oauth/authorize", authorizeUrl.AbsolutePath);

        // Extract `state` from the GitHub authorize URL query string.
        var authorizeQuery = System.Web.HttpUtility.ParseQueryString(authorizeUrl.Query);
        var oauthState = authorizeQuery["state"];
        Assert.False(string.IsNullOrEmpty(oauthState), "OAuth state must be present in the authorize URL");

        // ── Step 2: GitHub redirects back — GET /signin/github/callback ─────
        // The cookie jar on `client` already holds the correlation cookie set in Step 1.
        var callbackUri = $"/signin/github/callback?code=testcode&state={Uri.EscapeDataString(oauthState!)}";
        var callbackResp = await client.GetAsync(callbackUri);

        // The handler must redirect to /signin/github/finish, NOT back to /signin/github/callback.
        Assert.Equal(HttpStatusCode.Redirect, callbackResp.StatusCode);
        var finishLocation = callbackResp.Headers.Location!;

        // Key assertion: redirect goes to /finish, not /callback (the bug we fixed).
        var finishLocationStr = finishLocation.ToString();
        Assert.Contains("/signin/github/finish", finishLocationStr,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains("korat_state=", finishLocationStr,
            StringComparison.OrdinalIgnoreCase);

        // ── Step 3: Finalize — GET /signin/github/finish ────────────────────
        // The cookie jar on `client` holds the intermediate auth cookie set in Step 2.
        // Location may be relative ("/signin/github/finish?...") or absolute.
        var finishPath = finishLocation.IsAbsoluteUri
            ? finishLocation.PathAndQuery
            : finishLocationStr;
        var finishResp = await client.GetAsync(finishPath);

        // Must NOT redirect to /app/signin (that would indicate rejection).
        Assert.Equal(HttpStatusCode.Redirect, finishResp.StatusCode);
        var finalLocation = finishResp.Headers.Location?.ToString() ?? "";
        Assert.DoesNotContain("/app/signin", finalLocation, StringComparison.OrdinalIgnoreCase);

        // ── Assert: admin user and default Space are provisioned ─────────────
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<KoratDbContext>();

        var users = await db.Users
            .Where(u => u.PrimaryEmail == "admin@example.com")
            .ToListAsync();
        Assert.Single(users);
        var user = users[0];
        Assert.True(user.IsAdmin, "Provisioned user must be an admin");
        Assert.Equal(UserStatus.Active, user.Status);

        // Verify a default Space was created for the admin user.
        var ownerKey = user.Id.Value.ToString("N");
        var spaces = await db.Spaces
            .Where(s => s.OwnerUserId == ownerKey && s.IsDefault)
            .ToListAsync();
        Assert.Single(spaces);
    }

    // ─── Test 2: Non-admin user is provisioned (open registration) ──────────

    [Fact]
    public async Task GitHubSignin_NonAdminEmail_IsProvisioned_AndLandsInApp()
    {
        // Р15 (open registration) inverted this case. Before the invite gate was removed a
        // non-admin email with no invite was rejected at /signin/github/finish and redirected
        // back to /app/signin with no user row created. Registration is now open, so the same
        // flow must PROVISION the user and land them in the app.
        // The factory still uses a different Bootstrap:AdminEmail so "someone@example.com" is
        // deliberately NOT the admin — this asserts the non-privileged path, not the admin one.
        await using var factory = _fixture.CreateFactory(adminEmail: "admin@example.com", githubEmail: "someone@example.com");
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = true,
        });

        // ── Step 1: Challenge ───────────────────────────────────────────────
        var challengeResp = await client.GetAsync("/signin/github");
        Assert.Equal(HttpStatusCode.Redirect, challengeResp.StatusCode);
        var authorizeUrl = challengeResp.Headers.Location!;
        var oauthState = System.Web.HttpUtility.ParseQueryString(authorizeUrl.Query)["state"];
        Assert.False(string.IsNullOrEmpty(oauthState));

        // ── Step 2: Callback ────────────────────────────────────────────────
        var callbackResp = await client.GetAsync(
            $"/signin/github/callback?code=testcode&state={Uri.EscapeDataString(oauthState!)}");
        Assert.Equal(HttpStatusCode.Redirect, callbackResp.StatusCode);
        var finishLocation = callbackResp.Headers.Location!;
        Assert.Contains("/signin/github/finish", finishLocation.ToString(), StringComparison.OrdinalIgnoreCase);

        // ── Step 3: Finish ──────────────────────────────────────────────────
        var finishResp = await client.GetAsync(finishLocation.IsAbsoluteUri
            ? finishLocation.PathAndQuery
            : finishLocation.ToString());

        // Open registration: the non-admin lands in the app, NOT back on the sign-in page.
        Assert.Equal(HttpStatusCode.Redirect, finishResp.StatusCode);
        var finalLocation = finishResp.Headers.Location?.ToString() ?? "";
        Assert.Contains("/app/", finalLocation, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("/app/signin", finalLocation, StringComparison.OrdinalIgnoreCase);

        // Assert: the user IS provisioned, with exactly one Space.
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<KoratDbContext>();
        var user = await db.Users.SingleAsync(u => u.PrimaryEmail == "someone@example.com");
        Assert.False(user.IsAdmin, "a non-admin email must not be provisioned as admin");
        var ownerKey = user.Id.Value.ToString("N");
        var spaces = await db.Spaces
            .Where(sp => sp.OwnerUserId == ownerKey && sp.IsDefault)
            .ToListAsync();
        Assert.Single(spaces);
    }
}

/// <summary>
/// xUnit fixture for GitHub OAuth flow integration tests.
/// Boots a TestCluster (Orleans) and creates factories with GitHub OAuth stubbed.
/// Keeps factory creation lazy so each test can configure its own email / admin settings.
/// </summary>
public sealed class GitHubSigninFlowFixture : IAsyncLifetime
{
    private TestCluster? _cluster;

    public IClusterClient ClusterClient => _cluster!.Client;

    public async Task InitializeAsync()
    {
        var builder = new TestClusterBuilder();
        builder.AddSiloBuilderConfigurator<GitHubFlowSiloConfigurator>();
        builder.AddClientBuilderConfigurator<GitHubFlowClientConfigurator>();
        _cluster = builder.Build();
        await _cluster.DeployAsync();
    }

    /// <summary>
    /// Creates a fresh <see cref="GitHubOAuthTestFactory"/> each time, with its own
    /// in-memory database, configured with the given admin and GitHub stub email.
    /// </summary>
    public GitHubOAuthTestFactory CreateFactory(string adminEmail, string githubEmail)
    {
        // Each factory gets its own DB name so tests are fully isolated.
        var dbName = $"github-flow-{Guid.NewGuid():N}";
        var dbRoot = new InMemoryDatabaseRoot();
        return new GitHubOAuthTestFactory(_cluster!.Client, dbName, dbRoot, adminEmail, githubEmail);
    }

    public async Task DisposeAsync()
    {
        if (_cluster is not null)
            await _cluster.StopAllSilosAsync();
    }

    private sealed class GitHubFlowSiloConfigurator : ISiloConfigurator
    {
        public void Configure(ISiloBuilder siloBuilder)
        {
            siloBuilder.AddMemoryGrainStorage("korat");
            siloBuilder.ConfigureServices(services =>
            {
                services.AddSerializer(sb =>
                    sb.AddJsonSerializer(t => t.Namespace?.StartsWith("Korat") == true));
                // Use a shared DB root that the silo and web host can both access.
                // Since we're giving each factory its own root, the silo uses a
                // well-known name here; the web host overrides to the per-factory name.
                services.AddDbContextFactory<KoratDbContext>(opts =>
                    opts.UseInMemoryDatabase("github-flow-silo-shared"));
                services.AddSingleton<IMetadataRepository, EfMetadataRepository>();
                services.TryAddSingleton(TimeProvider.System);
            });
        }
    }

    private sealed class GitHubFlowClientConfigurator : IClientBuilderConfigurator
    {
        public void Configure(IConfiguration configuration, IClientBuilder clientBuilder)
        {
            clientBuilder.ConfigureServices(services =>
            {
                services.AddSerializer(sb =>
                    sb.AddJsonSerializer(t => t.Namespace?.StartsWith("Korat") == true));
            });
        }
    }
}

/// <summary>
/// WebApplicationFactory that boots the Korat.Cloud app in Testing environment
/// with GitHub OAuth stubbed and Bootstrap:AdminEmail configured.
/// The backchannel stub returns controlled responses so no real GitHub calls are made.
/// </summary>
public sealed class GitHubOAuthTestFactory : WebApplicationFactory<Program>
{
    private readonly IClusterClient _clusterClient;
    private readonly string _dbName;
    private readonly InMemoryDatabaseRoot _dbRoot;
    private readonly string _adminEmail;
    private readonly string _githubEmail;

    public GitHubOAuthTestFactory(
        IClusterClient clusterClient,
        string dbName,
        InMemoryDatabaseRoot dbRoot,
        string adminEmail,
        string githubEmail)
    {
        _clusterClient = clusterClient;
        _dbName = dbName;
        _dbRoot = dbRoot;
        _adminEmail = adminEmail;
        _githubEmail = githubEmail;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");

        // Provide GitHub OAuth credentials so the handler registers.
        // In Testing env the startup guard is skipped, but AddGitHubOAuth is gated on
        // non-empty ClientId/ClientSecret (see Program.cs).
        builder.UseSetting("GitHubAuth:ClientId", "test-client-id");
        builder.UseSetting("GitHubAuth:ClientSecret", "test-client-secret");

        // Configure bootstrap admin email.
        builder.UseSetting("Bootstrap:AdminEmail", _adminEmail);

        builder.ConfigureServices(services =>
        {
            // Inject the pre-started TestCluster client.
            services.RemoveAll<IClusterClient>();
            services.AddSingleton(_clusterClient);

            // Replace EF with in-memory database for test isolation.
            services.RemoveAll<IDbContextFactory<KoratDbContext>>();
            services.RemoveAll<DbContextOptions<KoratDbContext>>();
            services.RemoveAll<IDbContextOptionsConfiguration<KoratDbContext>>();
            services.RemoveAll<IMetadataRepository>();
            services.AddDbContextFactory<KoratDbContext>(opts =>
                opts.UseInMemoryDatabase(_dbName, _dbRoot));
            services.AddSingleton<IMetadataRepository, EfMetadataRepository>();

            // Override antiforgery secure policy — test client uses plain HTTP.
            services.PostConfigure<AntiforgeryOptions>(opts =>
            {
                opts.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
            });

            // Override the intermediate cookie's SecurePolicy so it is sent back
            // over plain HTTP by the test client (the production setting is Always).
            services.PostConfigure<CookieAuthenticationOptions>(
                CookieAuthenticationDefaults.AuthenticationScheme,
                opts =>
                {
                    opts.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
                });

            // ── Stub the GitHub OAuth backchannel ──────────────────────────
            // PostConfigure<OAuthOptions> runs AFTER Program.cs registers AddGitHubOAuth,
            // so we can replace BackchannelHttpHandler here.
            //
            // The stub handles:
            //   POST https://github.com/login/oauth/access_token  → fake access-token
            //   GET  https://api.github.com/user                  → fake profile
            //   GET  https://api.github.com/user/emails           → verified primary email
            //
            // We also override the correlation cookie's SecurePolicy so it is sent over
            // plain HTTP by the test client (the ASP.NET default is Secure=Always).
            var githubEmail = _githubEmail;
            services.PostConfigure<OAuthOptions>(GitHubOAuthExtensions.Scheme, opts =>
            {
                var stub = new GitHubBackchannelStub(githubEmail);
                // Set both BackchannelHttpHandler and Backchannel to ensure the stub is used
                // regardless of whether ASP.NET Core has already resolved the Backchannel
                // HttpClient from the handler during options validation.
                opts.BackchannelHttpHandler = stub;
                opts.Backchannel = new HttpClient(stub)
                {
                    Timeout = TimeSpan.FromSeconds(30),
                };
                // The correlation cookie is set by the challenge and must be returned
                // at the callback. Override Secure policy so it is NOT dropped on plain HTTP.
                opts.CorrelationCookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
            });
        });
    }
}

/// <summary>
/// Fake <see cref="HttpMessageHandler"/> that intercepts the three GitHub API calls
/// the OAuth handler makes during the sign-in flow:
///   1. Token endpoint (POST /login/oauth/access_token)
///   2. User profile (GET /user)
///   3. User emails (GET /user/emails)
/// </summary>
file sealed class GitHubBackchannelStub : HttpMessageHandler
{
    private readonly string _email;

    public GitHubBackchannelStub(string email) => _email = email;

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var uri = request.RequestUri!;

        // 1. Token exchange: POST https://github.com/login/oauth/access_token
        if (request.Method == HttpMethod.Post
            && uri.Host == "github.com"
            && uri.AbsolutePath == "/login/oauth/access_token")
        {
            // The ASP.NET Core OAuth handler sets Accept: application/json on the token request.
            var tokenJson = """{"access_token":"gho_test_token","token_type":"bearer","scope":"read:user,user:email"}""";
            var resp = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(tokenJson, Encoding.UTF8, "application/json"),
            };
            return Task.FromResult(resp);
        }

        // 2. User profile: GET https://api.github.com/user
        if (request.Method == HttpMethod.Get
            && uri.Host == "api.github.com"
            && uri.AbsolutePath == "/user")
        {
            var profileJson = """{"id":4242,"login":"testuser","name":"Test User"}""";
            var resp = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(profileJson, Encoding.UTF8, "application/json"),
            };
            return Task.FromResult(resp);
        }

        // 3. User emails: GET https://api.github.com/user/emails
        if (request.Method == HttpMethod.Get
            && uri.Host == "api.github.com"
            && uri.AbsolutePath == "/user/emails")
        {
            var emailsJson = $"[{{\"email\":\"{_email}\",\"primary\":true,\"verified\":true}}]";
            var resp = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(emailsJson, Encoding.UTF8, "application/json"),
            };
            return Task.FromResult(resp);
        }

        // Unexpected request — fail loudly so the test surfaces the issue.
        throw new InvalidOperationException(
            $"GitHubBackchannelStub: unexpected request {request.Method} {uri}");
    }
}
