using System.CommandLine;
using Korat.Cli.Service;

namespace Korat.Cli.Commands;

public static class McpRemoveCommand
{
    public static Command Create()
    {
        var command = new Command("remove", "Remove a locally-registered MCP server");
        var nameArg = new Argument<string>("name", "Server display name (case-insensitive)");
        command.AddArgument(nameArg);
        command.SetHandler(RemoveAsync, nameArg);
        return command;
    }

    internal static async Task RemoveAsync(string name)
    {
        var store = new LocalIdentityStore();
        var identity = store.LoadOrCreate();

        var idx = identity.McpServers.FindIndex(s =>
            string.Equals(s.DisplayName, name, StringComparison.OrdinalIgnoreCase));

        if (idx < 0)
        {
            Console.Error.WriteLine(
                $"No registered server named '{name}'. " +
                "Use `korat mcp list` to see registered servers.");
            Environment.ExitCode = 1;
            return;
        }

        identity.McpServers.RemoveAt(idx);
        store.Save(identity);

        Console.WriteLine($"Removed MCP server '{name}'.");

        // Check whether the node service is running and hint accordingly.
        var ctrl = GetController();
        if (ctrl is not null)
        {
            try
            {
                var status = await ctrl.GetStatusAsync();
                if (status.IsRunning)
                    Console.WriteLine("The publisher runtime is running and will unpublish it automatically.");
                else
                    Console.WriteLine("Hint: run `korat service install` to manage your servers automatically.");
            }
            catch
            {
                Console.WriteLine("Hint: run `korat service install` to manage your servers automatically.");
            }
        }
        else
        {
            Console.WriteLine("Hint: run `korat service install` to manage your servers automatically.");
        }
    }

    private static IServiceController? GetController()
    {
        if (OperatingSystem.IsMacOS()) return new LaunchdController();
        if (OperatingSystem.IsLinux()) return new SystemdController();
        return null;
    }
}
