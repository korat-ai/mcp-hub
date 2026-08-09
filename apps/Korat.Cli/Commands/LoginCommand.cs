using System.CommandLine;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using Korat.Cli.Auth;

namespace Korat.Cli.Commands;

public static class LoginCommand
{
    public static Command Create()
    {
        var command = new Command("login", "Authenticate with Korat Cloud via the device flow");

        var cloudOption = new Option<string>(
            "--cloud",
            getDefaultValue: () => "https://my.korat.ai",
            "Korat Cloud base URL");

        var grpcOption = new Option<string?>(
            "--grpc",
            getDefaultValue: () => null,
            "Override the gRPC relay-gateway URL. Defaults to <cloud-host>:8443 for https " +
            "clouds (Fly/Caddy gateway) or <cloud-host>:5192 for local http dev.");

        var issuerOption = new Option<string?>(
            "--issuer",
            getDefaultValue: () => null,
            $"Sign-in provider to authenticate against. Defaults to ${SsoSettings.IssuerEnvVar} " +
            $"or {SsoSettings.DefaultIssuer}.");

        var noBrowserOption = new Option<bool>(
            "--no-browser",
            "Print the URL instead of opening a browser window");

        command.AddOption(cloudOption);
        command.AddOption(grpcOption);
        command.AddOption(issuerOption);
        command.AddOption(noBrowserOption);

        command.SetHandler(async (string cloud, string? grpc, string? issuer, bool noBrowser) =>
        {
            try
            {
                await ExecuteAsync(
                    cloudUrl: cloud,
                    noBrowser: noBrowser,
                    credentialStore: null,
                    handlerOverride: null,
                    outputWriter: null,
                    ct: default,
                    grpcUrl: grpc,
                    identityStore: null,
                    issuerUrl: issuer);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Login failed: {ex.Message}");
                Environment.ExitCode = 1;
            }
        }, cloudOption, grpcOption, issuerOption, noBrowserOption);

        return command;
    }

    /// <summary>
    /// Testable core of the login flow. Parameters with <see langword="null"/> defaults
    /// use real production objects; tests pass stubs via the override parameters.
    /// </summary>
    /// <remarks>
    /// Права запрашивает провайдер, а не команда: набор фиксирован
    /// (<see cref="SsoSettings.DefaultScope"/>), и флага для него нет — просить больше,
    /// чем нужно хабу, незачем, а просить меньше значит остаться без обновляющего токена.
    /// </remarks>
    internal static async Task ExecuteAsync(
        string cloudUrl,
        bool noBrowser,
        CredentialStore? credentialStore,
        HttpMessageHandler? handlerOverride,
        TextWriter? outputWriter,
        CancellationToken ct,
        string? grpcUrl = null,
        LocalIdentityStore? identityStore = null,
        string? issuerUrl = null)
    {
        var output = outputWriter ?? Console.Out;
        var store = credentialStore ?? new CredentialStore();

        var normalizedUrl = cloudUrl.TrimEnd('/');
        var issuer = SsoSettings.ResolveIssuer(issuerUrl);

        // Два адресата, один обработчик. Вход идёт к провайдеру, а «кто я» спрашивается у
        // хаба — это разные хосты, поэтому и клиента два; в тестах оба ходят через один
        // подставной обработчик.
        var handler = handlerOverride ?? new HttpClientHandler();
        using var providerHttp = new HttpClient(handler, disposeHandler: false);
        using var cloudHttp = new HttpClient(handler, disposeHandler: handlerOverride is null)
        {
            BaseAddress = new Uri(normalizedUrl + "/"),
        };

        // ── Run device flow ───────────────────────────────────────────────────
        await output.WriteLineAsync($"  Signing in through {issuer}");
        var deviceClient = new DeviceFlowClient(
            providerHttp, issuer: issuer, output: output, noBrowser: noBrowser);
        var creds = await deviceClient.LoginAsync(normalizedUrl, ct);

        // ── Save credentials at mode 0600 ─────────────────────────────────────
        await store.SaveAsync(creds, ct);

        // ── Fetch account info via Bearer ─────────────────────────────────────
        try
        {
            using var meReq = new HttpRequestMessage(HttpMethod.Get, "/api/auth/me");
            meReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", creds.AccessToken);
            using var meResp = await cloudHttp.SendAsync(meReq, ct);
            if (meResp.IsSuccessStatusCode)
            {
                var me = (JsonObject?) await JsonNode.ParseAsync(
                    await meResp.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
                var email = me?["email"]?.GetValue<string>();
                if (!string.IsNullOrEmpty(email))
                    await output.WriteLineAsync($"  Logged in as: {email}");
            }
            else if (meResp.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            {
                // Пропуск настоящий, а вот человека за ним хаб пока не знает: связь между
                // учётной записью провайдера и учётной записью хаба заводится один раз,
                // входом в браузере. Промолчать здесь — значит отдать человеку рабочий на
                // вид пропуск, который назавтра ответит 401 на каждой команде.
                await output.WriteLineAsync(
                    $"  Signed in, but {normalizedUrl} does not recognize this account yet.");
                await output.WriteLineAsync(
                    $"  Sign in once at {normalizedUrl} in a browser to link it, then re-run `korat login`.");
            }
        }
        catch
        {
            // Не смертельно — учётные данные уже сохранены.
        }

        // ── Stitch the cloud host into the local node config ──────────────────
        // login authenticates AND points this machine's node at the cloud it logged
        // into. Without this, `korat up`/`connect` would dial the localhost dev default
        // baked into config.json and never reach the real cloud.
        var idStore = identityStore ?? new LocalIdentityStore();
        var identity = idStore.LoadOrCreate();
        identity.CloudUrl = normalizedUrl;
        identity.CloudGrpcUrl = ResolveGrpcUrl(normalizedUrl, grpcUrl);
        idStore.Save(identity);

        await output.WriteLineAsync($"  Cloud: {normalizedUrl} · gateway: {identity.CloudGrpcUrl}");

        // Дата тут больше не годится: пропуск живёт часы, и «Expires: 2026-08-09» читалось
        // бы как «до конца дня». Важно другое — обновится ли он сам.
        var renewal = string.IsNullOrWhiteSpace(creds.RefreshToken)
            ? "no refresh token — `korat login` will be needed again when it expires"
            : "renews automatically";
        await output.WriteLineAsync(
            $"  Credentials saved. Scope: {creds.Scope}. Access token expires " +
            $"{creds.ExpiresAt:yyyy-MM-dd HH:mm} UTC ({renewal}).");

        // #93/#100: surface the golden-path next steps so a fresh login has somewhere to go.
        await output.WriteLineAsync(string.Empty);
        await output.WriteLineAsync("Next: run `korat service install` to keep this machine online, or");
        await output.WriteLineAsync("      `korat connect <server-name> --bridge` to consume servers.");
    }

    /// <summary>
    /// Derives the gRPC node-gateway URL from the REST cloud URL. The gateway runs on a
    /// dedicated port, separate from REST: on Fly (https) it's TCP <c>:8443</c> behind a
    /// Caddy reverse-proxy — Fly's edge can't speak h2c upstream, so gRPC can't share the
    /// REST <c>:443</c>. For local plaintext dev it's <c>:5192</c> (Kestrel HTTP/2-only)
    /// alongside REST <c>:5191</c>. An explicit <c>--grpc</c> always wins.
    /// </summary>
    internal static string ResolveGrpcUrl(string cloudUrl, string? grpcOverride)
    {
        if (!string.IsNullOrWhiteSpace(grpcOverride))
            return grpcOverride.TrimEnd('/');

        var u = new Uri(cloudUrl);
        return u.Scheme == Uri.UriSchemeHttps
            ? $"https://{u.Host}:8443"
            : $"http://{u.Host}:5192";
    }
}
