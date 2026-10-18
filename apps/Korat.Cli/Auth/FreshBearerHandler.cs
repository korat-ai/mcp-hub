using System.Net.Http.Headers;

namespace Korat.Cli.Auth;

/// <summary>
/// Ставит пропуск на КАЖДЫЙ запрос, читая его заново, а не один раз при создании клиента.
///
/// Разница появилась вместе с переездом на провайдера входа. Прежний пропуск хаба жил 90
/// дней, и снимок при старте был безобиден. Токен провайдера живёт часами, а мост в Claude
/// Desktop — сутками: со снимком он через час переставал обновлять список серверов и прав,
/// молча. Сессии при этом работают, поэтому отказ выглядит как «новые гранты не доезжают»,
/// а не как «пора войти заново».
///
/// Чтение заодно обновляет истёкший пропуск — это делает <c>CredentialStore.LoadAsync</c>.
/// Обновление принимается только для того же хаба и того же провайдера входа, с которыми
/// создан клиент; абсолютный запрос на другой origin отклоняется до отправки.
/// </summary>
public sealed class FreshBearerHandler : DelegatingHandler
{
    private readonly CredentialStore _store;
    private readonly CliCredentials _fallback;
    private readonly Uri _boundCloud;

    public FreshBearerHandler(
        CredentialStore store,
        CliCredentials fallback,
        HttpMessageHandler inner) : base(inner)
    {
        _store = store;
        _fallback = fallback;
        _boundCloud = ParseCloudUri(fallback.CloudUrl, nameof(fallback));
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.RequestUri is not { IsAbsoluteUri: true } target || !SameOrigin(_boundCloud, target))
        {
            throw new InvalidOperationException(
                "The authenticated HTTP client cannot send requests outside its credential's cloud origin.");
        }

        // Отказ чтения не должен рушить запрос: у нас на руках есть пропуск, с которым
        // мост уже работает. Если он истёк — ответит сервер, и это честнее, чем исключение
        // из места, которое человек не связывает с входом.
        CliCredentials current;
        try
        {
            var loaded = await _store.LoadAsync(cancellationToken);
            current = loaded is not null && IsCompatible(loaded) ? loaded : _fallback;
        }
        catch (Exception exception) when (
            exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            // Таймаут провайдера тоже приходит как отмена, но не отменяет запрос к хабу.
            // Только отмена переданного сюда токена должна остановить этот запрос.
            current = _fallback;
        }

        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", current.AccessToken);
        return await base.SendAsync(request, cancellationToken);
    }

    private bool IsCompatible(CliCredentials candidate) =>
        TryParseCloudUri(candidate.CloudUrl, out var candidateCloud) &&
        SameOrigin(_boundCloud, candidateCloud) &&
        SameIssuer(_fallback.Issuer, candidate.Issuer);

    private static Uri ParseCloudUri(string value, string paramName)
    {
        if (TryParseCloudUri(value, out var uri))
            return uri;

        throw new ArgumentException("CloudUrl must be an absolute HTTP or HTTPS URI.", paramName);
    }

    private static bool TryParseCloudUri(string value, out Uri uri)
    {
        if (Uri.TryCreate(value, UriKind.Absolute, out var parsed) &&
            (parsed.Scheme == Uri.UriSchemeHttp || parsed.Scheme == Uri.UriSchemeHttps) &&
            !string.IsNullOrEmpty(parsed.Host))
        {
            uri = parsed;
            return true;
        }

        uri = null!;
        return false;
    }

    private static bool SameOrigin(Uri left, Uri right) =>
        string.Equals(left.Scheme, right.Scheme, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(left.IdnHost, right.IdnHost, StringComparison.OrdinalIgnoreCase) &&
        left.Port == right.Port;

    private static bool SameIssuer(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
            return string.IsNullOrWhiteSpace(left) && string.IsNullOrWhiteSpace(right);

        if (!Uri.TryCreate(left, UriKind.Absolute, out var leftUri) ||
            !Uri.TryCreate(right, UriKind.Absolute, out var rightUri))
        {
            return string.Equals(left.TrimEnd('/'), right.TrimEnd('/'), StringComparison.Ordinal);
        }

        return SameOrigin(leftUri, rightUri) &&
               string.Equals(
                   leftUri.AbsolutePath.TrimEnd('/'),
                   rightUri.AbsolutePath.TrimEnd('/'),
                   StringComparison.Ordinal) &&
               string.Equals(leftUri.Query, rightUri.Query, StringComparison.Ordinal) &&
               string.Equals(leftUri.Fragment, rightUri.Fragment, StringComparison.Ordinal);
    }
}
