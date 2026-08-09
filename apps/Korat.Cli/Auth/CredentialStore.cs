using System.Text.Json;
using Korat.Cli.Config;
using Korat.Cli;

namespace Korat.Cli.Auth;

/// <summary>
/// Пропуск в хаб, лежащий в <c>~/.korat/credentials</c> после <c>korat login</c>.
/// Файл на Unix — 0600.
/// </summary>
/// <param name="AccessToken">
///   Предъявляется хабу как <c>Authorization: Bearer</c>. Живёт часы, а не вечно.
/// </param>
/// <param name="Scope">Права, выданные провайдером (строка OAuth: <c>openid email offline_access</c>).</param>
/// <param name="ExpiresAt">Когда <see cref="AccessToken"/> перестанет действовать.</param>
/// <param name="CloudUrl">Хаб, которому этот пропуск предъявляется.</param>
/// <param name="RefreshToken">
///   Обменивается на новый <see cref="AccessToken"/>, когда тот истёк. <see langword="null"/>,
///   если провайдер его не выдал (не было права <c>offline_access</c>) — тогда конец срока
///   означает новый <c>korat login</c> руками.
/// </param>
/// <param name="Issuer">
///   Провайдер, выдавший пропуск. Хранится рядом с токенами, а не берётся из настроек при
///   обновлении: обновлять надо там же, где выдали, даже если умолчание CLI с тех пор
///   поменялось.
/// </param>
public sealed record CliCredentials(
    string AccessToken,
    string Scope,
    DateTimeOffset ExpiresAt,
    string CloudUrl,
    string? RefreshToken = null,
    string? Issuer = null);

/// <summary>
/// Читает и пишет <see cref="CliCredentials"/> в <c>~/.korat/credentials</c>. Тесты передают
/// свой <paramref name="dir"/>, поэтому настоящий домашний каталог в прогонах не трогается.
///
/// <see cref="LoadAsync"/> обновляет истёкший пропуск сам и сохраняет обновлённый. Так это
/// работает во всех командах разом: каждая из них и так вызывает <see cref="LoadAsync"/>, и
/// ни одной не приходится помнить про срок.
/// </summary>
public sealed class CredentialStore
{
    /// <summary>Меняет истёкшие учётные данные на свежие. <see langword="null"/> — не вышло.</summary>
    public delegate Task<CliCredentials?> RefreshDelegate(CliCredentials expired, CancellationToken ct);

    private readonly string _dir;
    private readonly RefreshDelegate _refresh;
    private string FilePath => Path.Combine(_dir, "credentials");

    /// <summary>
    /// Хранилище в каталоге <paramref name="dir"/>; <see langword="null"/> — каталог по
    /// умолчанию (<see cref="KoratConfigPaths.BaseDir"/>, то есть <c>~/.korat</c>).
    /// </summary>
    /// <param name="refresh">
    ///   Шов обновления. По умолчанию — настоящий обмен у провайдера, названного в самих
    ///   учётных данных. Тесты передают подделку, поэтому ни один тест не ходит в сеть.
    /// </param>
    public CredentialStore(string? dir = null, RefreshDelegate? refresh = null)
    {
        _dir = dir ?? KoratConfigPaths.BaseDir;
        _refresh = refresh ?? RefreshAtProviderAsync;
    }

    /// <summary>Есть ли вообще файл с учётными данными. Не читает и не обновляет его.</summary>
    public bool Exists => File.Exists(FilePath);

    /// <summary>
    /// Пишет учётные данные, создавая каталог при необходимости. На Unix каталог — 0700,
    /// файл — 0600. Запись атомарная: на Unix содержимое уходит во временный файл, уже
    /// ограниченный до 0600, и только потом переименовывается на место, так что пропуск
    /// ни мгновения не лежит с более широкими правами.
    /// </summary>
    public async Task SaveAsync(CliCredentials creds, CancellationToken ct = default)
    {
        KoratConfigPaths.EnsureDirSecure(_dir);

        var json = JsonSerializer.Serialize(creds, KoratCliJsonContext.Default.CliCredentials);

        if (!OperatingSystem.IsWindows())
        {
            var tmp = Path.Combine(_dir, $".credentials.{Path.GetRandomFileName()}.tmp");
            try
            {
                await using (var fs = new FileStream(
                    tmp,
                    new FileStreamOptions
                    {
                        Mode   = FileMode.Create,
                        Access = FileAccess.Write,
                        Share  = FileShare.None,
                        UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite,
                    }))
                await using (var sw = new StreamWriter(fs))
                {
                    await sw.WriteAsync(json);
                }

                File.Move(tmp, FilePath, overwrite: true);
            }
            catch
            {
                // По возможности прибрать за собой, если что-то пошло не так.
                try { File.Delete(tmp); } catch { /* ignored */ }
                throw;
            }
        }
        else
        {
            await File.WriteAllTextAsync(FilePath, json, ct);
        }
    }

    /// <summary>
    /// Читает учётные данные, обновляя пропуск, если тот истёк (или истечёт вот-вот) и есть
    /// чем обновлять. Обновлённый пропуск тут же сохраняется.
    ///
    /// Возвращает <see langword="null"/>, когда файла нет или он написан несовместимой
    /// версией CLI. Когда обновление не удалось, возвращаются прежние — истёкшие — данные:
    /// решение, что с этим делать, принимает команда. Молча стирать чужой файл из-за того,
    /// что провайдер сейчас недоступен, хранилище не вправе.
    /// </summary>
    public async Task<CliCredentials?> LoadAsync(CancellationToken ct = default)
    {
        var stored = await ReadAsync(ct);
        if (stored is null)
            return null;

        if (!IsExpiring(stored))
            return stored;

        if (string.IsNullOrWhiteSpace(stored.RefreshToken))
            return stored;

        var refreshed = await _refresh(stored, ct);
        if (refreshed is null)
            return stored;

        await SaveAsync(refreshed, ct);
        return refreshed;
    }

    /// <summary>Удаляет файл с учётными данными, если он есть.</summary>
    public void Delete()
    {
        if (File.Exists(FilePath))
            File.Delete(FilePath);
    }

    /// <summary>Истёк ли пропуск — с запасом, чтобы команда не осталась без него на середине.</summary>
    internal static bool IsExpiring(CliCredentials creds) =>
        creds.ExpiresAt - DeviceFlowClient.ExpiryLeeway <= DateTimeOffset.UtcNow;

    private async Task<CliCredentials?> ReadAsync(CancellationToken ct)
    {
        if (!File.Exists(FilePath))
            return null;

        WarnIfLoose();

        CliCredentials? creds;
        try
        {
            creds = JsonSerializer.Deserialize(
                await File.ReadAllTextAsync(FilePath, ct),
                KoratCliJsonContext.Default.CliCredentials);
        }
        catch (JsonException)
        {
            Console.Error.WriteLine(
                $"warning: {FilePath} is not readable as credentials. Run `korat login`.");
            return null;
        }

        if (creds is null || string.IsNullOrWhiteSpace(creds.AccessToken))
        {
            // Так выглядит файл, написанный CLI до перехода на провайдера входа: поле с
            // пропуском там называлось иначе, и после разбора остаётся пустым. Это не
            // «сломанный файл», а «войди заново», и сказать надо именно это.
            Console.Error.WriteLine(
                $"warning: {FilePath} was written by an older CLI and no longer carries a usable token. " +
                "Run `korat login`.");
            return null;
        }

        return creds;
    }

    /// <summary>
    /// Обмен по умолчанию: сходить к провайдеру, названному в самих учётных данных. Свой
    /// <see cref="HttpClient"/> со сроком — обновление стоит на пути любой команды, и висеть
    /// на нём сто секунд по умолчанию BCL команда не должна.
    /// </summary>
    private static async Task<CliCredentials?> RefreshAtProviderAsync(CliCredentials creds, CancellationToken ct)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            var client = new DeviceFlowClient(http, issuer: creds.Issuer, output: TextWriter.Null);
            return await client.RefreshAsync(creds, ct);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return null;
        }
    }

    private void WarnIfLoose()
    {
        if (OperatingSystem.IsWindows())
            return;

        var mode = File.GetUnixFileMode(FilePath);
        if ((mode & ~(UnixFileMode.UserRead | UnixFileMode.UserWrite)) != 0)
        {
            Console.Error.WriteLine(
                $"warning: {FilePath} is more permissive than 0600 (mode {mode}). " +
                $"Run: chmod 600 \"{FilePath}\"");
        }
    }
}
