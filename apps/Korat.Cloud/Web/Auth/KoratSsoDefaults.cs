namespace Korat.Cloud.Web.Auth;

/// <summary>
/// Имя схемы аутентификации для входа через провайдер Korat.
///
/// Константой, а не строкой по месту: имя схемы упоминается при регистрации, в вызове
/// Challenge и в разборе результата, и опечатка в любом из трёх мест даёт не ошибку сборки,
/// а молчаливый отказ во время выполнения.
/// </summary>
public static class KoratSsoDefaults
{
    public const string Scheme = "KoratSso";
}
