using System.Net;
using System.Text;
using System.Text.Json;
using Korat.Cli.Auth;
using Korat.Cli.Commands;

namespace Korat.Cli.Tests;

/// <summary>
/// Tests for <c>korat login</c> and <c>korat logout</c>.
///
/// Вход идёт к провайдеру, а «кто я» спрашивается у хаба — два разных адресата на одном
/// подставном обработчике, как и в жизни на одном сетевом стеке. Хранилище — во временном
/// каталоге, так что настоящий <c>~/.korat</c> в прогонах не трогается.
/// </summary>
public class LoginCommandTests
{
    private const string Issuer = "https://id.example.test/";
    private const string CloudUrl = "https://cloud.example.com";

    // ──────────────────────────────────────────────────────────────────────────
    // Shared test helpers (also used by DeviceFlowClientTests / DoctorCommandTests)
    // ──────────────────────────────────────────────────────────────────────────

    internal static HttpResponseMessage Json(object body, HttpStatusCode status = HttpStatusCode.OK)
    {
        var json = JsonSerializer.Serialize(body);
        return new HttpResponseMessage(status)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
    }

    internal static HttpResponseMessage BadRequest(object body) =>
        Json(body, HttpStatusCode.BadRequest);

    internal sealed class QueueHandler(Queue<HttpResponseMessage> queue) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(queue.Count > 0
                ? queue.Dequeue()
                : new HttpResponseMessage(HttpStatusCode.InternalServerError));
    }

    internal sealed class CallbackHandler(Func<HttpRequestMessage, HttpResponseMessage> respond)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }

    /// <summary>
    /// Подставные провайдер и хаб на одном обработчике: discovery → устройство → токен на
    /// адресах провайдера, <c>/api/auth/me</c> — на адресе хаба. Маршрутизация по адресу,
    /// а не по порядку: очередь ответов сломалась бы от одного лишнего запроса.
    /// </summary>
    private static HttpMessageHandler ProviderAndCloud(
        HttpResponseMessage? me = null,
        string userCode = "3695-0837-7448",
        string? refreshToken = "refresh-1") =>
        new CallbackHandler(request =>
        {
            var url = request.RequestUri!.ToString();

            if (url == Issuer + ".well-known/openid-configuration")
                return Json(new
                {
                    issuer = Issuer,
                    token_endpoint = Issuer + "connect/token",
                    device_authorization_endpoint = Issuer + "connect/device",
                });

            if (url == Issuer + "connect/device")
                return Json(new
                {
                    device_code = "dev-test",
                    user_code = userCode,
                    verification_uri = Issuer + "connect/verify",
                    verification_uri_complete = Issuer + $"connect/verify?user_code={userCode}",
                    expires_in = 600,
                    // 'interval' отсутствует — ровно как у живого провайдера.
                });

            if (url == Issuer + "connect/token")
                return Json(new
                {
                    access_token = "eyJhbG.login.sig",
                    refresh_token = refreshToken,
                    token_type = "Bearer",
                    scope = "openid email offline_access",
                    expires_in = 3600,
                });

            if (request.RequestUri.AbsolutePath == "/api/auth/me")
                return me ?? Json(new { email = "user@example.com" });

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

    private static async Task WithTempDir(Func<string, Task> body)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            await body(tempDir);
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }

    private static CredentialStore StoreIn(string tempDir) =>
        new(tempDir, (_, _) => Task.FromResult<CliCredentials?>(null));

    // ──────────────────────────────────────────────────────────────────────────
    // korat login
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task LoginCommand_stores_both_tokens_from_the_provider() =>
        await WithTempDir(async tempDir =>
        {
            var credStore = StoreIn(tempDir);

            await LoginCommand.ExecuteAsync(
                cloudUrl: CloudUrl,
                noBrowser: true,
                credentialStore: credStore,
                handlerOverride: ProviderAndCloud(),
                outputWriter: new StringWriter(),
                ct: default,
                identityStore: new LocalIdentityStore(Path.Combine(tempDir, "config.json")),
                issuerUrl: Issuer);

            var creds = await credStore.LoadAsync();
            Assert.NotNull(creds);
            Assert.Equal("eyJhbG.login.sig", creds!.AccessToken);
            // Без обновляющего токена вход пришлось бы повторять каждый день — сохранить
            // его так же обязательно, как и сам пропуск.
            Assert.Equal("refresh-1", creds.RefreshToken);
            Assert.Equal("openid email offline_access", creds.Scope);
            Assert.Equal(CloudUrl, creds.CloudUrl);
            // Обновлять надо там же, где выдали: провайдер запоминается вместе с токенами.
            Assert.Equal(Issuer, creds.Issuer);
        });

    [Fact]
    public async Task LoginCommand_prints_verification_url_and_user_code() =>
        await WithTempDir(async tempDir =>
        {
            var output = new StringWriter();

            await LoginCommand.ExecuteAsync(
                cloudUrl: CloudUrl,
                noBrowser: true,
                credentialStore: StoreIn(tempDir),
                handlerOverride: ProviderAndCloud(),
                outputWriter: output,
                ct: default,
                identityStore: new LocalIdentityStore(Path.Combine(tempDir, "config.json")),
                issuerUrl: Issuer);

            var printed = output.ToString();
            Assert.Contains(Issuer + "connect/verify", printed);
            Assert.Contains("3695-0837-7448", printed);
        });

    [Fact]
    public async Task LoginCommand_names_the_provider_it_is_signing_in_through() =>
        await WithTempDir(async tempDir =>
        {
            // Вход теперь уводит человека на чужой хост. Не назвать его — значит попросить
            // ввести код на странице, о которой он не просил.
            var output = new StringWriter();

            await LoginCommand.ExecuteAsync(
                cloudUrl: CloudUrl,
                noBrowser: true,
                credentialStore: StoreIn(tempDir),
                handlerOverride: ProviderAndCloud(),
                outputWriter: output,
                ct: default,
                identityStore: new LocalIdentityStore(Path.Combine(tempDir, "config.json")),
                issuerUrl: Issuer);

            Assert.Contains(Issuer, output.ToString());
        });

    [Fact]
    public async Task LoginCommand_falls_back_to_the_default_provider_when_none_is_given() =>
        await WithTempDir(async tempDir =>
        {
            // Умолчание живёт в одном месте, и команда обязана брать его оттуда: адрес
            // провайдера, вписанный в команду руками, разъехался бы с обновлением.
            var reached = new List<string>();
            var handler = new CallbackHandler(request =>
            {
                reached.Add(request.RequestUri!.ToString());
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            });

            await Assert.ThrowsAnyAsync<Exception>(() => LoginCommand.ExecuteAsync(
                cloudUrl: CloudUrl,
                noBrowser: true,
                credentialStore: StoreIn(tempDir),
                handlerOverride: handler,
                outputWriter: new StringWriter(),
                ct: default,
                identityStore: new LocalIdentityStore(Path.Combine(tempDir, "config.json"))));

            Assert.Contains(reached, u => u.StartsWith(SsoSettings.DefaultIssuer, StringComparison.Ordinal));
        });

    [Fact]
    public async Task LoginCommand_prints_account_email_after_success() =>
        await WithTempDir(async tempDir =>
        {
            var output = new StringWriter();

            await LoginCommand.ExecuteAsync(
                cloudUrl: CloudUrl,
                noBrowser: true,
                credentialStore: StoreIn(tempDir),
                handlerOverride: ProviderAndCloud(),
                outputWriter: output,
                ct: default,
                identityStore: new LocalIdentityStore(Path.Combine(tempDir, "config.json")),
                issuerUrl: Issuer);

            Assert.Contains("user@example.com", output.ToString());
        });

    [Fact]
    public async Task LoginCommand_says_so_when_the_cloud_does_not_know_this_account_yet() =>
        await WithTempDir(async tempDir =>
        {
            // Пропуск настоящий, но хаб связывает учётную запись провайдера со своей только
            // после входа в браузере. Промолчать здесь — отдать человеку пропуск, который
            // будет отвечать 401 на каждую команду, и не сказать почему.
            var output = new StringWriter();

            await LoginCommand.ExecuteAsync(
                cloudUrl: CloudUrl,
                noBrowser: true,
                credentialStore: StoreIn(tempDir),
                handlerOverride: ProviderAndCloud(me: new HttpResponseMessage(HttpStatusCode.Unauthorized)),
                outputWriter: output,
                ct: default,
                identityStore: new LocalIdentityStore(Path.Combine(tempDir, "config.json")),
                issuerUrl: Issuer);

            var printed = output.ToString();
            Assert.Contains("does not recognize this account", printed);
            Assert.Contains(CloudUrl, printed);
        });

    [Fact]
    public async Task LoginCommand_still_saves_credentials_when_the_cloud_rejects_them() =>
        await WithTempDir(async tempDir =>
        {
            // Пропуск выдал провайдер, и он действителен независимо от того, признал ли
            // его хаб. Выбросить его из-за 401 значило бы заставить проходить вход заново
            // после того, как человек свяжет учётные записи.
            var credStore = StoreIn(tempDir);

            await LoginCommand.ExecuteAsync(
                cloudUrl: CloudUrl,
                noBrowser: true,
                credentialStore: credStore,
                handlerOverride: ProviderAndCloud(me: new HttpResponseMessage(HttpStatusCode.Unauthorized)),
                outputWriter: new StringWriter(),
                ct: default,
                identityStore: new LocalIdentityStore(Path.Combine(tempDir, "config.json")),
                issuerUrl: Issuer);

            Assert.NotNull(await credStore.LoadAsync());
        });

    [Fact]
    public async Task LoginCommand_never_writes_either_token_to_output() =>
        await WithTempDir(async tempDir =>
        {
            // Страховка: ошибка, печатающая пропуск в stdout, прошла бы все остальные тесты
            // входа — и попала бы в логи CI, откуда её уже не забрать.
            var credStore = StoreIn(tempDir);
            var output = new StringWriter();

            await LoginCommand.ExecuteAsync(
                cloudUrl: CloudUrl,
                noBrowser: true,
                credentialStore: credStore,
                handlerOverride: ProviderAndCloud(),
                outputWriter: output,
                ct: default,
                identityStore: new LocalIdentityStore(Path.Combine(tempDir, "config.json")),
                issuerUrl: Issuer);

            var creds = await credStore.LoadAsync();
            Assert.NotNull(creds);
            Assert.DoesNotContain(creds!.AccessToken, output.ToString());
            Assert.DoesNotContain(creds.RefreshToken!, output.ToString());
        });

    [Fact]
    public async Task LoginCommand_tells_the_user_the_token_renews_itself() =>
        await WithTempDir(async tempDir =>
        {
            var output = new StringWriter();

            await LoginCommand.ExecuteAsync(
                cloudUrl: CloudUrl,
                noBrowser: true,
                credentialStore: StoreIn(tempDir),
                handlerOverride: ProviderAndCloud(),
                outputWriter: output,
                ct: default,
                identityStore: new LocalIdentityStore(Path.Combine(tempDir, "config.json")),
                issuerUrl: Issuer);

            Assert.Contains("renews automatically", output.ToString());
        });

    [Fact]
    public async Task LoginCommand_warns_when_no_refresh_token_was_issued() =>
        await WithTempDir(async tempDir =>
        {
            // Пропуск на час без обновления — это «войди заново к обеду», и человек должен
            // узнать об этом при входе, а не когда команда откажет.
            var output = new StringWriter();

            await LoginCommand.ExecuteAsync(
                cloudUrl: CloudUrl,
                noBrowser: true,
                credentialStore: StoreIn(tempDir),
                handlerOverride: ProviderAndCloud(refreshToken: null),
                outputWriter: output,
                ct: default,
                identityStore: new LocalIdentityStore(Path.Combine(tempDir, "config.json")),
                issuerUrl: Issuer);

            Assert.Contains("no refresh token", output.ToString());
        });

    [Fact]
    public async Task LoginCommand_writes_cloud_and_derived_grpc_url_to_config() =>
        await WithTempDir(async tempDir =>
        {
            // Вход не только удостоверяет, но и нацеливает эту машину на тот хаб, в который
            // вошли: без этого `korat up`/`connect` продолжали бы звонить в localhost.
            var configPath = Path.Combine(tempDir, "config.json");

            await LoginCommand.ExecuteAsync(
                cloudUrl: "https://my.korat.dev",
                noBrowser: true,
                credentialStore: StoreIn(tempDir),
                handlerOverride: ProviderAndCloud(),
                outputWriter: new StringWriter(),
                ct: default,
                identityStore: new LocalIdentityStore(configPath),
                issuerUrl: Issuer);

            var identity = new LocalIdentityStore(configPath).LoadOrCreate();
            Assert.Equal("https://my.korat.dev", identity.CloudUrl);
            Assert.Equal("https://my.korat.dev:8443", identity.CloudGrpcUrl);
        });

    [Fact]
    public async Task LoginCommand_grpc_override_is_honored_in_config() =>
        await WithTempDir(async tempDir =>
        {
            var configPath = Path.Combine(tempDir, "config.json");

            await LoginCommand.ExecuteAsync(
                cloudUrl: "http://localhost:5191",
                noBrowser: true,
                credentialStore: StoreIn(tempDir),
                handlerOverride: ProviderAndCloud(),
                outputWriter: new StringWriter(),
                ct: default,
                grpcUrl: "http://localhost:5192",
                identityStore: new LocalIdentityStore(configPath),
                issuerUrl: Issuer);

            var identity = new LocalIdentityStore(configPath).LoadOrCreate();
            Assert.Equal("http://localhost:5191", identity.CloudUrl);
            Assert.Equal("http://localhost:5192", identity.CloudGrpcUrl);
        });

    // ──────────────────────────────────────────────────────────────────────────
    // korat logout
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task LogoutCommand_deletes_the_local_credentials() =>
        await WithTempDir(async tempDir =>
        {
            var credStore = StoreIn(tempDir);
            await credStore.SaveAsync(new CliCredentials(
                AccessToken: "eyJhbG.existing.sig",
                Scope: "openid email offline_access",
                ExpiresAt: DateTimeOffset.UtcNow.AddHours(1),
                CloudUrl: CloudUrl,
                RefreshToken: "refresh-1",
                Issuer: Issuer));

            await LogoutCommand.ExecuteAsync(credentialStore: credStore, outputWriter: new StringWriter());

            Assert.False(credStore.Exists);
            Assert.Null(await credStore.LoadAsync());
        });

    [Fact]
    public async Task LogoutCommand_does_not_claim_the_token_was_revoked() =>
        await WithTempDir(async tempDir =>
        {
            // У провайдера нет конечной точки отзыва вовсе — сказать «отозвано» здесь было
            // бы прямой неправдой, и человек считал бы доступ закрытым, когда он открыт.
            var credStore = StoreIn(tempDir);
            await credStore.SaveAsync(new CliCredentials(
                "eyJhbG.existing.sig", "openid", DateTimeOffset.UtcNow.AddHours(1), CloudUrl, "refresh-1", Issuer));
            var output = new StringWriter();

            await LogoutCommand.ExecuteAsync(credentialStore: credStore, outputWriter: output);

            var printed = output.ToString();
            Assert.DoesNotContain("revoked.", printed);
            Assert.Contains("not revoked", printed);
            Assert.Contains("until it expires", printed);
        });

    [Fact]
    public async Task LogoutCommand_does_not_reach_the_network_at_all() =>
        await WithTempDir(async tempDir =>
        {
            // Выход должен работать, когда ни хаб, ни провайдер не отвечают: файл лежит на
            // этой машине, и убрать его — единственное, что тут вообще можно сделать.
            // Отсутствие сетевого шва в сигнатуре — это и есть доказательство.
            var credStore = StoreIn(tempDir);
            await credStore.SaveAsync(new CliCredentials(
                "eyJhbG.existing.sig", "openid", DateTimeOffset.UtcNow.AddHours(-1), CloudUrl, "refresh-1", Issuer));

            await LogoutCommand.ExecuteAsync(credentialStore: credStore, outputWriter: TextWriter.Null);

            Assert.False(credStore.Exists);
        });

    [Fact]
    public async Task LogoutCommand_without_credentials_prints_not_logged_in() =>
        await WithTempDir(async tempDir =>
        {
            var output = new StringWriter();

            await LogoutCommand.ExecuteAsync(credentialStore: StoreIn(tempDir), outputWriter: output);

            Assert.Contains("not logged in", output.ToString(), StringComparison.OrdinalIgnoreCase);
        });
}
