namespace Korat.Cli.Auth;

/// <summary>
/// Где живёт провайдер входа Korat и под каким именем клиента к нему обращается CLI.
///
/// Одно место на весь CLI. Адрес провайдера не должен встречаться строкой ни в командах,
/// ни в хранилище учётных данных: команда получает его отсюда, а обновление токена — из
/// самих учётных данных (<see cref="CliCredentials.Issuer"/>), куда он записан при входе.
/// Так вход и обновление никогда не расходятся, даже если умолчание поменяется между
/// версиями CLI.
///
/// Порядок разрешения — явный аргумент (флаг команды), затем переменная окружения, затем
/// умолчание. Переменная окружения нужна для разработки против другого провайдера: у CLI
/// нет файла настроек, и <c>KORAT_CONFIG</c> рядом устроен так же.
/// </summary>
public static class SsoSettings
{
    /// <summary>Боевой провайдер входа. Со слешом на конце — к нему приклеивается путь discovery.</summary>
    public const string DefaultIssuer = "https://id.korat.dev/";

    /// <summary>
    /// Имя клиента, под которым CLI приходит к провайдеру. Клиент публичный: секрета нет и
    /// быть не может — CLI живёт на чужой машине и не удержит его.
    /// </summary>
    public const string DefaultClientId = "korat-cli";

    /// <summary>
    /// <c>openid</c> — сам факт входа, <c>email</c> — чтобы хаб мог назвать вошедшего,
    /// <c>offline_access</c> — чтобы провайдер выдал обновляющий токен. Без последнего
    /// доступ живёт часы, и <c>korat login</c> пришлось бы повторять каждый день.
    /// </summary>
    public const string DefaultScope = "openid email offline_access";

    public const string IssuerEnvVar = "KORAT_SSO_ISSUER";
    public const string ClientIdEnvVar = "KORAT_SSO_CLIENT_ID";

    /// <summary>Адрес провайдера: явный аргумент → переменная окружения → умолчание.</summary>
    public static string ResolveIssuer(string? explicitIssuer = null) =>
        Normalize(FirstNonBlank(
            explicitIssuer,
            Environment.GetEnvironmentVariable(IssuerEnvVar),
            DefaultIssuer));

    /// <summary>Имя клиента: явный аргумент → переменная окружения → умолчание.</summary>
    public static string ResolveClientId(string? explicitClientId = null) =>
        FirstNonBlank(
            explicitClientId,
            Environment.GetEnvironmentVariable(ClientIdEnvVar),
            DefaultClientId).Trim();

    /// <summary>
    /// Адрес документа discovery провайдера. Единственный путь, который CLI знает наизусть:
    /// остальные конечные точки (устройство, токен) он читает оттуда, поэтому провайдер
    /// волен их переносить.
    /// </summary>
    public static string DiscoveryUrl(string issuer) =>
        Normalize(issuer) + ".well-known/openid-configuration";

    /// <summary>Слеш на конце обязателен: к адресу приклеивается путь. Значение из настроек может прийти без него.</summary>
    private static string Normalize(string issuer)
    {
        var trimmed = issuer.Trim();
        return trimmed.EndsWith('/') ? trimmed : trimmed + "/";
    }

    private static string FirstNonBlank(params string?[] candidates)
    {
        foreach (var candidate in candidates)
            if (!string.IsNullOrWhiteSpace(candidate))
                return candidate;

        // Последний кандидат — всегда константа-умолчание, так что сюда не попасть.
        throw new InvalidOperationException("No SSO setting value and no default.");
    }
}
