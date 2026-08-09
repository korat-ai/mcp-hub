using System.Text.Json.Nodes;
using Korat.Cli.Util;

namespace Korat.Cli.Auth;

/// <summary>
/// Клиент провайдера входа Korat: поток устройства (RFC 8628) и обновление токена
/// (RFC 6749 §6). Пропуск выдаёт провайдер, а не хаб, — хаб только проверяет подпись.
///
/// Конечные точки берутся из документа discovery провайдера, а не зашиты: CLI знает
/// наизусть только адрес самого провайдера (<see cref="SsoSettings"/>).
///
/// Клиент публичный: секрета нет, и в запросах его нет. Всё, чем CLI подтверждает, кто он
/// такой, — <c>client_id</c>; безопасность потока держится на том, что человек глазами
/// сверяет код на экране с кодом в браузере.
/// </summary>
public sealed class DeviceFlowClient
{
    /// <summary>
    /// RFC 8628 §3.2: поле <c>interval</c> в ответе необязательное, и провайдер Korat его
    /// не присылает. Пять секунд — умолчание из того же параграфа. Без подстановки цикл
    /// опроса пошёл бы без паузы вообще.
    /// </summary>
    internal const int DefaultPollIntervalSeconds = 5;

    /// <summary>RFC 8628 §3.5: после <c>slow_down</c> пауза растёт ровно на столько.</summary>
    internal const int SlowDownIncrementSeconds = 5;

    /// <summary>Страховка на случай ответа без <c>expires_in</c> — те же 10 минут, что присылает провайдер.</summary>
    private const int FallbackExpiresInSeconds = 600;

    /// <summary>
    /// Насколько заранее пропуск считается истёкшим. Команда, начавшая работу с пропуском,
    /// которому осталось три секунды, получила бы 401 на середине — обновление должно
    /// случиться до, а не после.
    /// </summary>
    internal static readonly TimeSpan ExpiryLeeway = TimeSpan.FromSeconds(60);

    private const string DeviceCodeGrantType = "urn:ietf:params:oauth:grant-type:device_code";

    private readonly HttpClient _http;
    private readonly string _issuer;
    private readonly string _clientId;
    private readonly string _scope;
    private readonly TextWriter _output;
    private readonly TimeProvider _time;
    private readonly bool _noBrowser;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    /// <summary>
    /// Создаёт клиента провайдера.
    /// </summary>
    /// <param name="http">
    ///   HTTP-клиент. <see cref="HttpClient.BaseAddress"/> не используется: адреса конечных
    ///   точек приходят из discovery абсолютными.
    /// </param>
    /// <param name="issuer">Адрес провайдера; <see langword="null"/> — разрешить через <see cref="SsoSettings"/>.</param>
    /// <param name="clientId">Имя клиента; <see langword="null"/> — разрешить через <see cref="SsoSettings"/>.</param>
    /// <param name="scope">Запрашиваемые права; <see langword="null"/> — <see cref="SsoSettings.DefaultScope"/>.</param>
    /// <param name="output">Куда печатать адрес подтверждения и код. По умолчанию <see cref="Console.Out"/>.</param>
    /// <param name="time">Часы для сроков; по умолчанию системные.</param>
    /// <param name="noBrowser">Не открывать браузер самому.</param>
    /// <param name="delay">
    ///   Пауза цикла опроса — шов для тестов. Подделка завершается мгновенно и запоминает
    ///   запрошенный интервал, так что подстановку пяти секунд и рост после <c>slow_down</c>
    ///   можно проверить без ожидания по настоящим часам.
    /// </param>
    public DeviceFlowClient(
        HttpClient http,
        string? issuer = null,
        string? clientId = null,
        string? scope = null,
        TextWriter? output = null,
        TimeProvider? time = null,
        bool noBrowser = false,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _http = http;
        _issuer = SsoSettings.ResolveIssuer(issuer);
        _clientId = SsoSettings.ResolveClientId(clientId);
        _scope = string.IsNullOrWhiteSpace(scope) ? SsoSettings.DefaultScope : scope;
        _output = output ?? Console.Out;
        _time = time ?? TimeProvider.System;
        _noBrowser = noBrowser;
        _delay = delay ?? Task.Delay;
    }

    /// <summary>Адрес провайдера, к которому обращается этот клиент (со слешом на конце).</summary>
    public string Issuer => _issuer;

    /// <summary>
    /// Проводит вход целиком: код устройства → показ кода человеку → опрос до решения.
    /// </summary>
    /// <param name="cloudUrl">
    ///   Хаб, к которому пропуск будет предъявляться. Провайдер о нём не знает — это поле
    ///   учётных данных, а не часть протокола.
    /// </param>
    /// <param name="ct">Отмена.</param>
    /// <exception cref="InvalidOperationException">Человек отказал, либо провайдер ответил не по протоколу.</exception>
    /// <exception cref="TimeoutException">Код устройства истёк раньше подтверждения.</exception>
    public async Task<CliCredentials> LoginAsync(string cloudUrl, CancellationToken ct)
    {
        var endpoints = await DiscoverAsync(ct);
        if (endpoints.DeviceAuthorization is null)
            throw new InvalidOperationException(
                $"The sign-in provider at {_issuer} does not advertise a device authorization endpoint.");

        // ── 1. Запросить код устройства ───────────────────────────────────────
        var devicePayload = await PostFormAsync(
            endpoints.DeviceAuthorization,
            new Dictionary<string, string>
            {
                ["client_id"] = _clientId,
                ["scope"] = _scope,
            },
            ct);

        if (devicePayload.Payload is null)
            throw new InvalidOperationException("The sign-in provider returned an empty device-code response.");

        if (!devicePayload.Success)
        {
            var error = devicePayload.Payload["error"]?.GetValue<string>() ?? "unknown_error";
            var description = devicePayload.Payload["error_description"]?.GetValue<string>();
            throw new InvalidOperationException(
                error == "invalid_client"
                    // Самая частая причина на живом провайдере — клиент просто не объявлен.
                    // Общая формулировка «invalid_client» тут ничего не подсказывает.
                    ? $"The sign-in provider at {_issuer} does not know the client '{_clientId}'. " +
                      "Check KORAT_SSO_CLIENT_ID, or ask the operator to register this client."
                    : $"The sign-in provider refused the device request ({error}"
                      + (description is null ? ")" : $": {description})"));
        }

        var deviceCode = devicePayload.Payload["device_code"]?.GetValue<string>()
            ?? throw new InvalidOperationException("Device-code response missing 'device_code'.");
        var userCode = devicePayload.Payload["user_code"]?.GetValue<string>()
            ?? throw new InvalidOperationException("Device-code response missing 'user_code'.");
        var verificationUri = devicePayload.Payload["verification_uri"]?.GetValue<string>()
            ?? throw new InvalidOperationException("Device-code response missing 'verification_uri'.");

        // Провайдер Korat не присылает 'interval' — RFC разрешает, но тогда пауза наша.
        var intervalSec = devicePayload.Payload["interval"]?.GetValue<int>() ?? DefaultPollIntervalSeconds;
        var expiresIn = devicePayload.Payload["expires_in"]?.GetValue<int>() ?? FallbackExpiresInSeconds;

        // ── 2. Показать код человеку (годится и без графики) ──────────────────
        await _output.WriteLineAsync();
        await _output.WriteLineAsync("  To authorize the CLI, open the following URL in your browser:");
        await _output.WriteLineAsync($"    {verificationUri}");
        await _output.WriteLineAsync();
        await _output.WriteLineAsync($"  Then enter the code: {userCode}");
        await _output.WriteLineAsync();

        // ── 3. По возможности открыть браузер ─────────────────────────────────
        if (!_noBrowser)
        {
            var verificationUriComplete =
                devicePayload.Payload["verification_uri_complete"]?.GetValue<string>() ?? verificationUri;
            BrowserLauncher.TryOpen(verificationUriComplete);
        }

        // ── 4. Опрашивать точку токена ────────────────────────────────────────
        // Одна строка состояния перед циклом: в CI и по ssh иначе не отличить ожидание
        // от зависания.
        await _output.WriteLineAsync("  Waiting for approval in your browser...");
        await _output.WriteLineAsync();

        var deadline = _time.GetUtcNow().AddSeconds(expiresIn);
        var pollInterval = TimeSpan.FromSeconds(intervalSec);

        while (_time.GetUtcNow() < deadline)
        {
            if (pollInterval > TimeSpan.Zero)
                await _delay(pollInterval, ct);

            var tokenResult = await PostFormAsync(
                endpoints.Token,
                new Dictionary<string, string>
                {
                    ["grant_type"] = DeviceCodeGrantType,
                    ["device_code"] = deviceCode,
                    ["client_id"] = _clientId,
                },
                ct);

            if (tokenResult.Success)
            {
                var creds = ReadTokenResponse(tokenResult.Payload, cloudUrl, previousRefreshToken: null);
                await _output.WriteLineAsync("  Authorized.");
                await _output.WriteLineAsync();
                return creds;
            }

            // RFC 8628 §3.5. Четыре исхода, и человеку про них надо сказать разное:
            // «жду» — молча ждать дальше, «медленнее» — увеличить паузу, «отказано» —
            // это решение человека, «истёк» — начать заново.
            var error = tokenResult.Payload?["error"]?.GetValue<string>() ?? "expired_token";

            switch (error)
            {
                case "authorization_pending":
                    break;

                case "slow_down":
                    pollInterval += TimeSpan.FromSeconds(SlowDownIncrementSeconds);
                    break;

                case "access_denied":
                    throw new InvalidOperationException(
                        "Authorization denied. Run 'korat login' again if you change your mind.");

                case "expired_token":
                    throw new TimeoutException(
                        "The device code expired before it was approved. Run 'korat login' again.");

                default:
                    // Живой провайдер отвечает 'invalid_grant' на код, которого не знает,
                    // — это не «истёк», и делать вид, что истёк, нечестно.
                    var description = tokenResult.Payload?["error_description"]?.GetValue<string>();
                    throw new InvalidOperationException(
                        $"The sign-in provider refused the device code ({error}"
                        + (description is null ? ")" : $": {description})")
                        + " Run 'korat login' again.");
            }
        }

        throw new TimeoutException(
            "The device code expired before the request was approved. Run 'korat login' again.");
    }

    /// <summary>
    /// Меняет обновляющий токен на свежий пропуск (RFC 6749 §6).
    /// </summary>
    /// <returns>
    ///   Новые учётные данные, либо <see langword="null"/>, если обновиться не вышло —
    ///   провайдер недоступен, либо обновляющий токен больше не действует. Разницу между
    ///   этими двумя случаями вызывающему знать незачем: делать он будет одно и то же —
    ///   работать со старым пропуском, пока тот жив, и просить войти заново, когда нет.
    /// </returns>
    public async Task<CliCredentials?> RefreshAsync(CliCredentials creds, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(creds.RefreshToken))
            return null;

        DiscoveredEndpoints endpoints;
        try
        {
            endpoints = await DiscoverAsync(ct);
        }
        catch
        {
            return null;
        }

        FormResult result;
        try
        {
            result = await PostFormAsync(
                endpoints.Token,
                new Dictionary<string, string>
                {
                    ["grant_type"] = "refresh_token",
                    ["refresh_token"] = creds.RefreshToken,
                    ["client_id"] = _clientId,
                },
                ct);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return null;
        }

        if (!result.Success || result.Payload is null)
            return null;

        try
        {
            // Провайдер прокручивает обновляющий токен: в ответе приходит новый, а старый
            // после обмена мёртв. Не сохранить новый — значит сломать следующее обновление,
            // и сломать молча: этот обмен пройдёт, а следующий уже нет.
            return ReadTokenResponse(result.Payload, creds.CloudUrl, previousRefreshToken: creds.RefreshToken);
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>
    /// Собирает учётные данные из ответа точки токена. Срок считается от наших часов и
    /// <c>expires_in</c>: <c>exp</c> внутри пропуска пришлось бы разбирать, а разбирать
    /// чужой JWT ради того, что и так лежит рядом в ответе, незачем.
    /// </summary>
    private CliCredentials ReadTokenResponse(JsonObject? payload, string cloudUrl, string? previousRefreshToken)
    {
        if (payload is null)
            throw new InvalidOperationException("The sign-in provider returned an empty token response.");

        var accessToken = payload["access_token"]?.GetValue<string>()
            ?? throw new InvalidOperationException("Token response missing 'access_token'.");
        var refreshToken = payload["refresh_token"]?.GetValue<string>() ?? previousRefreshToken;
        var scope = payload["scope"]?.GetValue<string>() ?? _scope;
        var expiresIn = payload["expires_in"]?.GetValue<int>() ?? 0;

        return new CliCredentials(
            AccessToken: accessToken,
            Scope: scope,
            ExpiresAt: _time.GetUtcNow().AddSeconds(expiresIn),
            CloudUrl: cloudUrl.TrimEnd('/'),
            RefreshToken: refreshToken,
            Issuer: _issuer);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Discovery и формы
    // ─────────────────────────────────────────────────────────────────────────

    private sealed record DiscoveredEndpoints(string Token, string? DeviceAuthorization);

    private sealed record FormResult(bool Success, JsonObject? Payload);

    private async Task<DiscoveredEndpoints> DiscoverAsync(CancellationToken ct)
    {
        using var response = await _http.GetAsync(SsoSettings.DiscoveryUrl(_issuer), ct);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(
                $"The sign-in provider at {_issuer} is not answering discovery (HTTP {(int) response.StatusCode}).");

        var document = (JsonObject?) await JsonNode.ParseAsync(
            await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct)
            ?? throw new InvalidOperationException($"The sign-in provider at {_issuer} returned an empty discovery document.");

        var token = document["token_endpoint"]?.GetValue<string>()
            ?? throw new InvalidOperationException($"The sign-in provider at {_issuer} advertises no token endpoint.");

        return new DiscoveredEndpoints(token, document["device_authorization_endpoint"]?.GetValue<string>());
    }

    /// <summary>
    /// POST формой. И точка устройства, и точка токена по OAuth принимают
    /// <c>application/x-www-form-urlencoded</c> — JSON провайдер там не читает.
    /// Ошибки протокола приезжают телом с кодом 4xx, поэтому тело разбирается всегда.
    /// </summary>
    private async Task<FormResult> PostFormAsync(
        string endpoint, Dictionary<string, string> fields, CancellationToken ct)
    {
        using var content = new FormUrlEncodedContent(fields);
        using var response = await _http.PostAsync(endpoint, content, ct);

        JsonObject? payload;
        try
        {
            payload = (JsonObject?) await JsonNode.ParseAsync(
                await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        }
        catch
        {
            // Не-JSON в ответе (страница шлюза, пустое тело) — тот же исход, что и пустой
            // ответ: понять из него нечего.
            payload = null;
        }

        return new FormResult(response.IsSuccessStatusCode, payload);
    }
}
