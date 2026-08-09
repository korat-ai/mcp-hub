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
/// </summary>
public sealed class FreshBearerHandler(
    CredentialStore store,
    CliCredentials fallback,
    HttpMessageHandler inner) : DelegatingHandler(inner)
{
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        // Отказ чтения не должен рушить запрос: у нас на руках есть пропуск, с которым
        // мост уже работает. Если он истёк — ответит сервер, и это честнее, чем исключение
        // из места, которое человек не связывает с входом.
        CliCredentials current;
        try
        {
            current = await store.LoadAsync(cancellationToken) ?? fallback;
        }
        catch (Exception)
        {
            current = fallback;
        }

        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", current.AccessToken);
        return await base.SendAsync(request, cancellationToken);
    }
}
