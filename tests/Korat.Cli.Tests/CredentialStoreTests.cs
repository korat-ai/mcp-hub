using System.Text.Json;
using Korat.Cli.Auth;

namespace Korat.Cli.Tests;

/// <summary>
/// Tests for <see cref="CredentialStore"/>: как пропуск ложится на диск и как истёкший
/// пропуск обновляется при чтении.
///
/// Шов обновления всегда подставной — ни один тест здесь не ходит в сеть.
/// </summary>
public class CredentialStoreTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());

    private CredentialStore BuildStore(CredentialStore.RefreshDelegate? refresh = null) =>
        new(_tempDir, refresh ?? NeverRefresh);

    /// <summary>Умолчание для тестов, которым обновление неинтересно: «не вышло», без сети.</summary>
    private static Task<CliCredentials?> NeverRefresh(CliCredentials _, CancellationToken __) =>
        Task.FromResult<CliCredentials?>(null);

    private static CliCredentials Fresh(
        string accessToken = "eyJhbG.access.sig",
        string? refreshToken = "refresh-1") =>
        new(accessToken, "openid email offline_access", DateTimeOffset.UtcNow.AddHours(1),
            "https://cloud.example.com", refreshToken, "https://id.example.test/");

    private static CliCredentials Expired(
        string accessToken = "eyJhbG.stale.sig",
        string? refreshToken = "refresh-1") =>
        new(accessToken, "openid email offline_access", DateTimeOffset.UtcNow.AddMinutes(-5),
            "https://cloud.example.com", refreshToken, "https://id.example.test/");

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    // ── Диск ───────────────────────────────────────────────────────────────────

    [Fact]
    public async Task SaveAsync_then_LoadAsync_round_trips_all_fields()
    {
        var store = BuildStore();
        var creds = new CliCredentials(
            AccessToken: "eyJhbG.access.sig",
            Scope: "openid email offline_access",
            ExpiresAt: DateTimeOffset.Parse("2126-08-28T00:00:00Z"),
            CloudUrl: "https://cloud.example.com",
            RefreshToken: "refresh-1",
            Issuer: "https://id.example.test/");

        await store.SaveAsync(creds);
        var loaded = await store.LoadAsync();

        Assert.NotNull(loaded);
        Assert.Equal("eyJhbG.access.sig", loaded!.AccessToken);
        // Обновляющий токен обязан пережить круг: без него следующий запуск CLI останется
        // с одним пропуском на несколько часов и потребует нового входа руками.
        Assert.Equal("refresh-1", loaded.RefreshToken);
        Assert.Equal("openid email offline_access", loaded.Scope);
        Assert.Equal(DateTimeOffset.Parse("2126-08-28T00:00:00Z"), loaded.ExpiresAt);
        Assert.Equal("https://cloud.example.com", loaded.CloudUrl);
        Assert.Equal("https://id.example.test/", loaded.Issuer);
    }

    [Fact]
    public async Task LoadAsync_returns_null_when_file_does_not_exist()
    {
        var store = BuildStore();
        Assert.Null(await store.LoadAsync());
        Assert.False(store.Exists);
    }

    [Fact]
    public async Task SaveAsync_creates_file_with_mode_0600_on_unix()
    {
        if (OperatingSystem.IsWindows())
            return; // Windows uses ACLs, not Unix file mode — skip.

        var store = BuildStore();
        await store.SaveAsync(Fresh());

        var mode = File.GetUnixFileMode(Path.Combine(_tempDir, "credentials"));

        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, mode);
    }

    [Fact]
    public async Task Delete_removes_credentials_file()
    {
        var store = BuildStore();
        await store.SaveAsync(Fresh());
        Assert.True(store.Exists);

        store.Delete();

        Assert.False(store.Exists);
        Assert.Null(await store.LoadAsync());
    }

    [Fact]
    public async Task SaveAsync_overwrites_existing_file_and_preserves_0600_mode()
    {
        if (OperatingSystem.IsWindows())
            return;

        var store = BuildStore();
        await store.SaveAsync(Fresh(accessToken: "eyJhbG.v1.sig"));
        await store.SaveAsync(Fresh(accessToken: "eyJhbG.v2.sig"));

        var mode = File.GetUnixFileMode(Path.Combine(_tempDir, "credentials"));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, mode);

        var loaded = await store.LoadAsync();
        Assert.Equal("eyJhbG.v2.sig", loaded!.AccessToken);
    }

    [Fact]
    public async Task SaveAsync_leaves_no_temp_files_on_success()
    {
        if (OperatingSystem.IsWindows())
            return;

        var store = BuildStore();
        await store.SaveAsync(Fresh());

        var files = Directory.GetFiles(_tempDir);
        Assert.Single(files);
        Assert.EndsWith("credentials", files[0]);
    }

    // ── Обновление при чтении ──────────────────────────────────────────────────

    [Fact]
    public async Task LoadAsync_does_not_refresh_a_token_that_is_still_good()
    {
        var refreshed = 0;
        var store = BuildStore((_, _) =>
        {
            refreshed++;
            return Task.FromResult<CliCredentials?>(Fresh(accessToken: "eyJhbG.should.not.happen"));
        });
        await store.SaveAsync(Fresh());

        var loaded = await store.LoadAsync();

        Assert.Equal(0, refreshed);
        Assert.Equal("eyJhbG.access.sig", loaded!.AccessToken);
    }

    [Fact]
    public async Task LoadAsync_refreshes_an_expired_token_and_returns_the_fresh_one()
    {
        var store = BuildStore((_, _) =>
            Task.FromResult<CliCredentials?>(Fresh(accessToken: "eyJhbG.renewed.sig", refreshToken: "refresh-2")));
        await store.SaveAsync(Expired());

        var loaded = await store.LoadAsync();

        Assert.Equal("eyJhbG.renewed.sig", loaded!.AccessToken);
        Assert.True(loaded.ExpiresAt > DateTimeOffset.UtcNow);
    }

    [Fact]
    public async Task LoadAsync_persists_the_refreshed_token_so_the_next_run_does_not_refresh_again()
    {
        // Не сохранить обновлённое — значит ходить к провайдеру на каждую команду, и это
        // видно только по нагрузке на провайдера, а не по поведению CLI.
        var refreshCalls = 0;
        var store = BuildStore((_, _) =>
        {
            refreshCalls++;
            return Task.FromResult<CliCredentials?>(Fresh(accessToken: "eyJhbG.renewed.sig", refreshToken: "refresh-2"));
        });
        await store.SaveAsync(Expired());

        await store.LoadAsync();
        var second = await store.LoadAsync();

        Assert.Equal(1, refreshCalls);
        Assert.Equal("eyJhbG.renewed.sig", second!.AccessToken);
        Assert.Equal("refresh-2", second.RefreshToken);
    }

    [Fact]
    public async Task LoadAsync_refreshes_shortly_before_expiry_not_after()
    {
        // Пропуск, которому осталось десять секунд, для команды уже бесполезен: она успеет
        // начать работу и получить 401 на середине. Обновление обязано случиться до, а не
        // после — иначе ошибка выглядит как случайная и не воспроизводится.
        var store = BuildStore((_, _) =>
            Task.FromResult<CliCredentials?>(Fresh(accessToken: "eyJhbG.renewed.sig")));
        await store.SaveAsync(new CliCredentials(
            "eyJhbG.almost.gone", "openid", DateTimeOffset.UtcNow.AddSeconds(10),
            "https://cloud.example.com", "refresh-1", "https://id.example.test/"));

        var loaded = await store.LoadAsync();

        Assert.Equal("eyJhbG.renewed.sig", loaded!.AccessToken);
    }

    [Fact]
    public async Task LoadAsync_keeps_the_expired_token_when_the_refresh_fails()
    {
        // Провайдер недоступен — не повод стирать чужой файл. Команда наверху сама решит,
        // что делать; доктор в этом состоянии обязан сказать «истёк», а не «не входил».
        var store = BuildStore(NeverRefresh);
        await store.SaveAsync(Expired());

        var loaded = await store.LoadAsync();

        Assert.NotNull(loaded);
        Assert.Equal("eyJhbG.stale.sig", loaded!.AccessToken);
        Assert.True(loaded.ExpiresAt <= DateTimeOffset.UtcNow);
        Assert.True(store.Exists);
    }

    [Fact]
    public async Task LoadAsync_does_not_try_to_refresh_when_there_is_no_refresh_token()
    {
        var attempted = false;
        var store = BuildStore((_, _) =>
        {
            attempted = true;
            return Task.FromResult<CliCredentials?>(null);
        });
        await store.SaveAsync(Expired(refreshToken: null));

        var loaded = await store.LoadAsync();

        Assert.False(attempted);
        Assert.Equal("eyJhbG.stale.sig", loaded!.AccessToken);
    }

    [Fact]
    public async Task LoadAsync_hands_the_stored_credentials_to_the_refresher_unchanged()
    {
        CliCredentials? seen = null;
        var store = BuildStore((creds, _) =>
        {
            seen = creds;
            return Task.FromResult<CliCredentials?>(null);
        });
        await store.SaveAsync(Expired());

        await store.LoadAsync();

        // Обновлять надо у того провайдера, который выдал, а не у того, что сейчас записан
        // умолчанием в CLI: умолчание могло поменяться с версией.
        Assert.Equal("refresh-1", seen!.RefreshToken);
        Assert.Equal("https://id.example.test/", seen.Issuer);
    }

    // ── Файл от прежней версии ────────────────────────────────────────────────

    [Fact]
    public async Task LoadAsync_treats_a_pre_sso_credentials_file_as_not_logged_in()
    {
        // Так выглядел файл, пока пропуск выдавал хаб: поле называлось cliToken, и после
        // разбора новая запись остаётся без пропуска вообще. Отдать её наверх значило бы
        // слать пустой Bearer в каждую команду и получать невнятные 401.
        Directory.CreateDirectory(_tempDir);
        await File.WriteAllTextAsync(
            Path.Combine(_tempDir, "credentials"),
            """{"cliToken":"korat_cli_old","scope":"full","expiresAt":"2126-01-01T00:00:00+00:00","cloudUrl":"https://cloud.example.com"}""");

        var loaded = await BuildStore().LoadAsync();

        Assert.Null(loaded);
    }

    [Fact]
    public async Task LoadAsync_treats_an_unreadable_credentials_file_as_not_logged_in()
    {
        Directory.CreateDirectory(_tempDir);
        await File.WriteAllTextAsync(Path.Combine(_tempDir, "credentials"), "not json at all");

        var loaded = await BuildStore().LoadAsync();

        Assert.Null(loaded);
    }

    [Fact]
    public async Task Saved_file_carries_both_tokens()
    {
        // Страховка от беззвучной потери поля при смене сериализатора: обновляющий токен
        // должен реально оказаться в файле, а не только в возвращённой записи.
        var store = BuildStore();
        await store.SaveAsync(Fresh());

        var raw = await File.ReadAllTextAsync(Path.Combine(_tempDir, "credentials"));
        using var document = JsonDocument.Parse(raw);

        var properties = document.RootElement.EnumerateObject()
            .ToDictionary(p => p.Name, p => p.Value.ToString(), StringComparer.OrdinalIgnoreCase);

        Assert.Equal("eyJhbG.access.sig", properties["AccessToken"]);
        Assert.Equal("refresh-1", properties["RefreshToken"]);
    }
}
