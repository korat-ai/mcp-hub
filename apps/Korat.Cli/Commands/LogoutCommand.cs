using System.CommandLine;
using Korat.Cli.Auth;

namespace Korat.Cli.Commands;

/// <summary>
/// <c>korat logout</c> — убирает пропуск с этой машины.
///
/// Дальше этого команда не идёт, и не притворяется, что идёт. Пропуск теперь выдаёт
/// провайдер входа, а у него нет конечной точки отзыва: в его документе discovery нет
/// <c>revocation_endpoint</c>, и отозвать выданный пропуск «по строке токена» снаружи
/// нельзя вообще. Прежние <c>/api/auth/cli/revoke</c> и <c>/revoke-all</c> отзывали
/// собственные пропуска хаба — те, которые CLI больше не получает, — так что звать их
/// теперь значило бы получить 401 и напечатать «отозвано» после него.
///
/// Поэтому: файл удаляется, и человеку прямо говорится, что пропуск на сервере остаётся
/// действительным до конца своего срока. Что действительно закрывает доступ — завершение
/// сессии у провайдера; это делается там, а не отсюда.
/// </summary>
public static class LogoutCommand
{
    public static Command Create()
    {
        var command = new Command("logout", "Remove the stored credentials from this machine");

        command.SetHandler(async () =>
        {
            try
            {
                await ExecuteAsync(credentialStore: null, outputWriter: null);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Logout failed: {ex.Message}");
                Environment.ExitCode = 1;
            }
        });

        return command;
    }

    /// <summary>
    /// Testable core of the logout flow. Parameters with <see langword="null"/> defaults
    /// use real production objects; tests pass stubs via the override parameters.
    ///
    /// Сети здесь нет ни одного вызова — потому и нет параметра для подставного обработчика.
    /// </summary>
    internal static async Task ExecuteAsync(
        CredentialStore? credentialStore,
        TextWriter? outputWriter)
    {
        var output = outputWriter ?? Console.Out;
        var store = credentialStore ?? new CredentialStore();

        // Файл проверяется, а не читается: читать его — значит пытаться обновить истёкший
        // пропуск ровно перед тем, как его выбросить.
        if (!store.Exists)
        {
            await output.WriteLineAsync("Not logged in — no credentials found.");
            return;
        }

        store.Delete();

        await output.WriteLineAsync("Credentials removed from this machine.");
        await output.WriteLineAsync(
            "Note: the access token itself is not revoked — it stays valid at the cloud until it " +
            "expires. To end the session everywhere, sign out at the sign-in provider.");
    }
}
