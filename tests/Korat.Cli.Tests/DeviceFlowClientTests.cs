using System.Net;
using Korat.Cli.Auth;

namespace Korat.Cli.Tests;

/// <summary>
/// Tests for <see cref="DeviceFlowClient"/> against the Korat sign-in provider (RFC 8628 +
/// RFC 6749 §6).
///
/// Проверяются свойства потока, а не последовательность вызовов: подставной провайдер
/// отвечает по адресу конечной точки, как настоящий, и тесты спрашивают «что получилось»,
/// а не «кого позвали». Все ответы — той формы, что наблюдалась на живом id.korat.dev,
/// включая отсутствие поля <c>interval</c>.
/// </summary>
public class DeviceFlowClientTests
{
    private const string Issuer = "https://id.example.test/";
    private const string DiscoveryUrl = Issuer + ".well-known/openid-configuration";
    private const string DeviceUrl = Issuer + "connect/device";
    private const string TokenUrl = Issuer + "connect/token";
    private const string CloudUrl = "https://cloud.example.com";

    private static HttpResponseMessage Json(object body, HttpStatusCode status = HttpStatusCode.OK) =>
        LoginCommandTests.Json(body, status);

    private static HttpResponseMessage BadRequest(object body) =>
        LoginCommandTests.BadRequest(body);

    /// <summary>Документ discovery в той форме, в какой его отдаёт живой провайдер.</summary>
    internal static object DiscoveryDocument(bool withDeviceEndpoint = true) => withDeviceEndpoint
        ? new
        {
            issuer = Issuer,
            token_endpoint = TokenUrl,
            device_authorization_endpoint = DeviceUrl,
        }
        : new
        {
            issuer = Issuer,
            token_endpoint = TokenUrl,
        };

    /// <summary>
    /// Ответ точки устройства РОВНО как у живого провайдера: без поля <c>interval</c>.
    /// Именно его отсутствие клиент обязан закрыть своей паузой.
    /// </summary>
    private static object DeviceResponse(string userCode = "3695-0837-7448") => new
    {
        device_code = "dev-abc",
        user_code = userCode,
        verification_uri = Issuer + "connect/verify",
        verification_uri_complete = Issuer + $"connect/verify?user_code={userCode}",
        expires_in = 600,
    };

    private static object TokenResponse(
        string accessToken = "eyJhbG.access.sig",
        string? refreshToken = "refresh-1",
        int expiresIn = 3600) => new
    {
        access_token = accessToken,
        refresh_token = refreshToken,
        token_type = "Bearer",
        scope = "openid email offline_access",
        expires_in = expiresIn,
    };

    /// <summary>
    /// Подставной провайдер: discovery отвечает всегда, точка токена — по очереди из
    /// заготовленных ответов (последний повторяется, когда очередь кончилась).
    /// </summary>
    private sealed class ProviderStub(
        object discovery,
        HttpResponseMessage deviceResponse,
        IEnumerable<HttpResponseMessage> tokenResponses)
    {
        private readonly Queue<HttpResponseMessage> _tokens = new(tokenResponses);
        private HttpResponseMessage? _last;

        public List<string> TokenRequestBodies { get; } = [];
        public List<string> DeviceRequestBodies { get; } = [];

        public HttpMessageHandler Handler => new LoginCommandTests.CallbackHandler(request =>
        {
            var url = request.RequestUri!.ToString();

            if (url == DiscoveryUrl)
                return Json(discovery);

            var body = request.Content?.ReadAsStringAsync().GetAwaiter().GetResult() ?? string.Empty;

            if (url == DeviceUrl)
            {
                DeviceRequestBodies.Add(body);
                return deviceResponse;
            }

            if (url == TokenUrl)
            {
                TokenRequestBodies.Add(body);
                if (_tokens.Count > 0)
                    _last = _tokens.Dequeue();
                return _last ?? BadRequest(new { error = "expired_token" });
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });
    }

    private static (DeviceFlowClient Client, StringWriter Output, List<TimeSpan> Delays, ProviderStub Stub) Build(
        HttpResponseMessage? deviceResponse = null,
        IEnumerable<HttpResponseMessage>? tokenResponses = null,
        object? discovery = null,
        TimeProvider? time = null)
    {
        var stub = new ProviderStub(
            discovery ?? DiscoveryDocument(),
            deviceResponse ?? Json(DeviceResponse()),
            tokenResponses ?? [Json(TokenResponse())]);

        var delays = new List<TimeSpan>();
        var http = new HttpClient(stub.Handler);
        var output = new StringWriter();
        var client = new DeviceFlowClient(
            http,
            issuer: Issuer,
            clientId: "korat-cli",
            output: output,
            time: time ?? TimeProvider.System,
            noBrowser: true,
            delay: (interval, _) =>
            {
                delays.Add(interval);
                return Task.CompletedTask;
            });

        return (client, output, delays, stub);
    }

    // ── Поток устройства ───────────────────────────────────────────────────────

    [Fact]
    public async Task LoginAsync_prints_verification_uri_and_user_code()
    {
        var (client, output, _, _) = Build();

        var creds = await client.LoginAsync(CloudUrl, default);

        var printed = output.ToString();
        Assert.Contains(Issuer + "connect/verify", printed);
        Assert.Contains("3695-0837-7448", printed);
        Assert.Equal("eyJhbG.access.sig", creds.AccessToken);
        Assert.Equal("refresh-1", creds.RefreshToken);
        Assert.Equal("openid email offline_access", creds.Scope);
        Assert.Equal(CloudUrl, creds.CloudUrl);
        Assert.Equal(Issuer, creds.Issuer);
    }

    [Fact]
    public async Task LoginAsync_defaults_the_poll_interval_to_five_seconds_when_the_provider_omits_it()
    {
        // Свойство, ради которого этот тест и написан: живой провайдер поле 'interval' не
        // присылает (проверено на id.korat.dev). Клиент обязан подставить пять секунд из
        // RFC 8628 §3.2 сам — иначе цикл опроса пойдёт без паузы вообще и будет долбить
        // сервер сотнями запросов в секунду.
        var (client, _, delays, _) = Build(tokenResponses:
        [
            BadRequest(new { error = "authorization_pending" }),
            Json(TokenResponse()),
        ]);

        await client.LoginAsync(CloudUrl, default);

        Assert.NotEmpty(delays);
        Assert.All(delays, d => Assert.Equal(TimeSpan.FromSeconds(5), d));
    }

    [Fact]
    public async Task LoginAsync_honours_an_interval_the_provider_does_send()
    {
        // Обратная сторона того же свойства: подстановка не должна затирать значение,
        // если провайдер его всё-таки прислал.
        var withInterval = Json(new
        {
            device_code = "dev-abc",
            user_code = "1111-2222-3333",
            verification_uri = Issuer + "connect/verify",
            interval = 2,
            expires_in = 600,
        });

        var (client, _, delays, _) = Build(
            deviceResponse: withInterval,
            tokenResponses: [Json(TokenResponse())]);

        await client.LoginAsync(CloudUrl, default);

        Assert.Equal(TimeSpan.FromSeconds(2), delays[0]);
    }

    [Fact]
    public async Task LoginAsync_polls_through_authorization_pending_then_returns_the_token()
    {
        var (client, _, delays, _) = Build(tokenResponses:
        [
            BadRequest(new { error = "authorization_pending" }),
            BadRequest(new { error = "authorization_pending" }),
            Json(TokenResponse(accessToken: "eyJhbG.after.waiting")),
        ]);

        var creds = await client.LoginAsync(CloudUrl, default);

        Assert.Equal("eyJhbG.after.waiting", creds.AccessToken);
        // «Жду» паузу не удлиняет: три опроса — три одинаковые паузы.
        Assert.Equal(3, delays.Count);
        Assert.All(delays, d => Assert.Equal(TimeSpan.FromSeconds(5), d));
    }

    [Fact]
    public async Task LoginAsync_slow_down_grows_the_poll_interval_by_five_seconds()
    {
        // RFC 8628 §3.5. Проверяется само свойство «пауза выросла ровно на 5 с и осталась
        // выросшей», а не то, что клиент не упал.
        var (client, _, delays, _) = Build(tokenResponses:
        [
            BadRequest(new { error = "slow_down" }),
            BadRequest(new { error = "authorization_pending" }),
            Json(TokenResponse()),
        ]);

        await client.LoginAsync(CloudUrl, default);

        Assert.Equal(
            new[] { TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10) },
            delays);
    }

    [Fact]
    public async Task LoginAsync_slow_down_compounds_across_repeats()
    {
        // Два подряд — плюс десять, а не «в два раза» и не «плюс пять один раз».
        var (client, _, delays, _) = Build(tokenResponses:
        [
            BadRequest(new { error = "slow_down" }),
            BadRequest(new { error = "slow_down" }),
            Json(TokenResponse()),
        ]);

        await client.LoginAsync(CloudUrl, default);

        Assert.Equal(TimeSpan.FromSeconds(15), delays[^1]);
    }

    [Fact]
    public async Task LoginAsync_reports_a_human_refusal_as_a_refusal()
    {
        var (client, _, _, _) = Build(tokenResponses: [BadRequest(new { error = "access_denied" })]);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.LoginAsync(CloudUrl, default));

        // Человеку надо понять, что это его собственное решение, а не поломка.
        Assert.Contains("denied", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("korat login", ex.Message);
        Assert.DoesNotContain("expired", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task LoginAsync_reports_an_expired_device_code_as_expiry_not_refusal()
    {
        var (client, _, _, _) = Build(tokenResponses: [BadRequest(new { error = "expired_token" })]);

        var ex = await Assert.ThrowsAsync<TimeoutException>(
            () => client.LoginAsync(CloudUrl, default));

        Assert.Contains("expired", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("denied", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task LoginAsync_does_not_dress_an_unknown_grant_error_up_as_expiry()
    {
        // Живой провайдер на код, которого не знает, отвечает invalid_grant, а не
        // expired_token (проверено на id.korat.dev). Раньше любая незнакомая ошибка
        // превращалась в «код истёк» — неправда, которую человек не может опровергнуть.
        var (client, _, _, _) = Build(tokenResponses:
        [
            BadRequest(new
            {
                error = "invalid_grant",
                error_description = "The specified device code is invalid.",
            }),
        ]);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.LoginAsync(CloudUrl, default));

        Assert.Contains("invalid_grant", ex.Message);
        Assert.Contains("The specified device code is invalid.", ex.Message);
    }

    [Fact]
    public async Task LoginAsync_gives_the_client_id_back_when_the_provider_does_not_know_it()
    {
        // Ровно то, что вернул живой провайдер для незарегистрированного korat-cli: 401 и
        // invalid_client. Сообщение обязано назвать клиента — иначе человеку негде искать.
        var (client, _, _, _) = Build(deviceResponse: Json(
            new
            {
                error = "invalid_client",
                error_description = "The specified 'client_id' is invalid.",
            },
            HttpStatusCode.Unauthorized));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.LoginAsync(CloudUrl, default));

        Assert.Contains("korat-cli", ex.Message);
        Assert.Contains(Issuer, ex.Message);
    }

    [Fact]
    public async Task LoginAsync_stops_when_the_provider_advertises_no_device_endpoint()
    {
        var (client, _, _, _) = Build(discovery: DiscoveryDocument(withDeviceEndpoint: false));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.LoginAsync(CloudUrl, default));

        Assert.Contains("device authorization endpoint", ex.Message);
    }

    [Fact]
    public async Task LoginAsync_sends_no_client_secret_because_the_client_is_public()
    {
        // CLI живёт на чужой машине и секрет не удержит. Если он когда-нибудь начнёт его
        // слать, это будет означать, что секрет откуда-то взялся — и он в бинарнике.
        var (client, _, _, stub) = Build();

        await client.LoginAsync(CloudUrl, default);

        Assert.All(stub.DeviceRequestBodies, b => Assert.DoesNotContain("client_secret", b));
        Assert.All(stub.TokenRequestBodies, b => Assert.DoesNotContain("client_secret", b));
        Assert.All(stub.TokenRequestBodies, b => Assert.Contains("client_id=korat-cli", b));
    }

    [Fact]
    public async Task LoginAsync_asks_for_offline_access_so_a_refresh_token_is_issued()
    {
        // Без offline_access провайдер обновляющий токен не выдаёт, и korat login пришлось
        // бы повторять каждый день — весь смысл второго токена держится на этом праве.
        var (client, _, _, stub) = Build();

        await client.LoginAsync(CloudUrl, default);

        Assert.Contains("offline_access", Uri.UnescapeDataString(stub.DeviceRequestBodies[0]));
    }

    [Fact]
    public async Task LoginAsync_derives_expiry_from_the_providers_expires_in()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-08-09T12:00:00Z"));
        var (client, _, _, _) = Build(
            tokenResponses: [Json(TokenResponse(expiresIn: 3600))],
            time: clock);

        var creds = await client.LoginAsync(CloudUrl, default);

        Assert.Equal(DateTimeOffset.Parse("2026-08-09T13:00:00Z"), creds.ExpiresAt);
    }

    [Fact]
    public async Task LoginAsync_never_prints_either_token()
    {
        var (client, output, _, _) = Build();

        var creds = await client.LoginAsync(CloudUrl, default);

        Assert.DoesNotContain(creds.AccessToken, output.ToString());
        Assert.DoesNotContain(creds.RefreshToken!, output.ToString());
    }

    // ── Обновление ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task RefreshAsync_exchanges_the_refresh_token_for_a_fresh_access_token()
    {
        var (client, _, _, stub) = Build(tokenResponses:
        [
            Json(TokenResponse(accessToken: "eyJhbG.fresh.sig", refreshToken: "refresh-2")),
        ]);

        var stale = new CliCredentials(
            AccessToken: "eyJhbG.stale.sig",
            Scope: "openid email offline_access",
            ExpiresAt: DateTimeOffset.UtcNow.AddMinutes(-5),
            CloudUrl: CloudUrl,
            RefreshToken: "refresh-1",
            Issuer: Issuer);

        var refreshed = await client.RefreshAsync(stale, default);

        Assert.NotNull(refreshed);
        Assert.Equal("eyJhbG.fresh.sig", refreshed!.AccessToken);
        Assert.True(refreshed.ExpiresAt > DateTimeOffset.UtcNow);
        Assert.Equal(CloudUrl, refreshed.CloudUrl);
        Assert.Contains("grant_type=refresh_token", stub.TokenRequestBodies[0]);
    }

    [Fact]
    public async Task RefreshAsync_keeps_the_rolled_refresh_token_not_the_spent_one()
    {
        // Провайдер прокручивает обновляющий токен: старый после обмена мёртв. Сохранить
        // старый — значит сломать СЛЕДУЮЩЕЕ обновление, а это отказ через несколько часов,
        // который на самом обмене ничем себя не выдаёт.
        var (client, _, _, _) = Build(tokenResponses:
        [
            Json(TokenResponse(accessToken: "eyJhbG.fresh.sig", refreshToken: "refresh-2")),
        ]);

        var refreshed = await client.RefreshAsync(
            new CliCredentials("stale", "openid", DateTimeOffset.UtcNow.AddMinutes(-5), CloudUrl, "refresh-1", Issuer),
            default);

        Assert.Equal("refresh-2", refreshed!.RefreshToken);
    }

    [Fact]
    public async Task RefreshAsync_keeps_the_old_refresh_token_when_the_provider_does_not_roll_it()
    {
        // Прокрутка — не обязанность провайдера. Если нового токена в ответе нет, старый
        // ещё жив и обязан пережить обмен: иначе одно успешное обновление стало бы
        // последним.
        var (client, _, _, _) = Build(tokenResponses:
        [
            Json(TokenResponse(accessToken: "eyJhbG.fresh.sig", refreshToken: null)),
        ]);

        var refreshed = await client.RefreshAsync(
            new CliCredentials("stale", "openid", DateTimeOffset.UtcNow.AddMinutes(-5), CloudUrl, "refresh-1", Issuer),
            default);

        Assert.Equal("refresh-1", refreshed!.RefreshToken);
    }

    [Fact]
    public async Task RefreshAsync_returns_null_when_the_refresh_token_is_no_longer_valid()
    {
        var (client, _, _, _) = Build(tokenResponses:
        [
            BadRequest(new { error = "invalid_grant", error_description = "The refresh token is no longer valid." }),
        ]);

        var refreshed = await client.RefreshAsync(
            new CliCredentials("stale", "openid", DateTimeOffset.UtcNow.AddMinutes(-5), CloudUrl, "refresh-1", Issuer),
            default);

        Assert.Null(refreshed);
    }

    [Fact]
    public async Task RefreshAsync_returns_null_without_reaching_the_provider_when_there_is_nothing_to_exchange()
    {
        var (client, _, _, stub) = Build();

        var refreshed = await client.RefreshAsync(
            new CliCredentials("stale", "openid", DateTimeOffset.UtcNow.AddMinutes(-5), CloudUrl),
            default);

        Assert.Null(refreshed);
        Assert.Empty(stub.TokenRequestBodies);
    }

    [Fact]
    public async Task RefreshAsync_returns_null_when_the_provider_is_unreachable()
    {
        // Недоступный провайдер не должен ронять команду: наверху это читается как «не
        // обновилось», и решение принимает вызывающий.
        var http = new HttpClient(new LoginCommandTests.CallbackHandler(
            _ => throw new HttpRequestException("stub offline")));
        var client = new DeviceFlowClient(http, issuer: Issuer, output: TextWriter.Null);

        var refreshed = await client.RefreshAsync(
            new CliCredentials("stale", "openid", DateTimeOffset.UtcNow.AddMinutes(-5), CloudUrl, "refresh-1", Issuer),
            default);

        Assert.Null(refreshed);
    }

    /// <summary>Часы, которые стоят: срок пропуска должен считаться от них, а не от настоящих.</summary>
    internal sealed class FakeTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
