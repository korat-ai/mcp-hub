using System.CommandLine;
using Korat.Cli.Auth;
using Korat.Cli.Service;

namespace Korat.Cli.Commands;

public static class McpAddCommand
{
    public static Command Create()
    {
        var command = new Command("add", "Register a local MCP server");
        var nameArg = new Argument<string>("name", "Server display name");
        var commandOption = new Option<string>("--command",
            "Full launch command (the executable plus optional args, " +
            "e.g. \"dotnet run --project apps/Korat.Demo.EchoMcp\")");
        commandOption.IsRequired = true;
        command.AddArgument(nameArg);
        command.AddOption(commandOption);
        command.SetHandler(AddAsync, nameArg, commandOption);
        return command;
    }

    /// <summary>
    /// #105: validates a server display name. Names must be 1–64 characters and may only
    /// contain letters, digits, hyphens, underscores, and spaces — no leading/trailing
    /// whitespace. Mirrors the agent-add UX so both surfaces reject the same inputs.
    /// </summary>
    internal static bool TryValidateName(string name, out string error)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            error = "Server name must not be empty or whitespace.";
            return false;
        }
        if (name.Length > 64)
        {
            error = $"Server name must be 64 characters or fewer (got {name.Length}).";
            return false;
        }
        if (name != name.Trim())
        {
            error = "Server name must not have leading or trailing whitespace.";
            return false;
        }
        foreach (var ch in name)
        {
            if (!char.IsLetterOrDigit(ch) && ch != '-' && ch != '_' && ch != ' ')
            {
                error = $"Server name contains invalid character '{ch}'. Allowed: letters, digits, hyphens, underscores, spaces.";
                return false;
            }
        }
        error = string.Empty;
        return true;
    }

    private static async Task AddAsync(string name, string commandLine)
    {
        // #105: validate the name before doing any I/O (mirrors agent-add UX).
        if (!TryValidateName(name, out var nameError))
        {
            Console.Error.WriteLine($"Error: {nameError}");
            Environment.ExitCode = 1;
            return;
        }

        var store = new LocalIdentityStore();
        var identity = store.LoadOrCreate();

        var (launchCommand, launchArguments) = ShellSplit(commandLine);

        var existingIndex = identity.McpServers.FindIndex(s =>
            string.Equals(s.DisplayName, name, StringComparison.OrdinalIgnoreCase));
        var isUpdate = existingIndex >= 0;
        var local = new LocalMcpServer
        {
            DisplayName = name,
            LaunchCommand = launchCommand,
            LaunchArguments = launchArguments
        };
        if (isUpdate)
            identity.McpServers[existingIndex] = local;
        else
            identity.McpServers.Add(local);
        store.Save(identity);

        // #96: distinguish a silent overwrite from a fresh registration.
        if (isUpdate)
            Console.WriteLine($"Updated existing MCP server '{name}' ({commandLine}).");
        else
            Console.WriteLine($"Registered MCP server '{name}' ({commandLine}).");

        // Check whether the node service is running and hint accordingly.
        var ctrl = GetController();
        if (ctrl is not null)
        {
            try
            {
                var status = await ctrl.GetStatusAsync();
                if (status.IsRunning)
                    Console.WriteLine("The publisher runtime is running and will pick it up automatically.");
                else
                    Console.WriteLine("Hint: run `korat service install` to start serving it automatically.");
            }
            catch
            {
                Console.WriteLine("Hint: run `korat service install` to start serving it automatically.");
            }
        }
        else
        {
            Console.WriteLine("Hint: run `korat service install` to start serving it automatically.");
        }
    }

    private static IServiceController? GetController()
    {
        if (OperatingSystem.IsMacOS()) return new LaunchdController();
        if (OperatingSystem.IsLinux()) return new SystemdController();
        if (OperatingSystem.IsWindows()) return new ScheduledTaskController();
        return null;
    }

    /// <summary>
    /// Splits a command line into (executable, remaining-arguments-as-one-string).
    /// Honors a single layer of double-quoting for the executable token.
    /// Examples:
    ///   "dotnet run --project foo"  → ("dotnet", "run --project foo")
    ///   "\"my prog.exe\" --flag"    → ("my prog.exe", "--flag")
    /// </summary>
    internal static (string Command, string Arguments) ShellSplit(string commandLine)
    {
        if (string.IsNullOrWhiteSpace(commandLine))
            return (string.Empty, string.Empty);

        var s = commandLine.Trim();
        if (s.StartsWith('"'))
        {
            var end = s.IndexOf('"', 1);
            if (end < 0) return (s.TrimStart('"'), string.Empty);
            var exe = s.Substring(1, end - 1);
            var rest = end + 1 < s.Length ? s.Substring(end + 1).TrimStart() : string.Empty;
            return (exe, rest);
        }

        var space = s.IndexOf(' ');
        if (space < 0) return (s, string.Empty);
        return (s.Substring(0, space), s.Substring(space + 1).TrimStart());
    }

    /// <summary>
    /// Tokenizes an argument string into individual argument tokens, honoring
    /// double-quoted segments (which may contain spaces). Unmatched opening
    /// quotes cause the remainder of the string to be treated as a single token.
    /// Examples:
    ///   "run --project foo"          → ["run", "--project", "foo"]
    ///   "--name \"my server\""       → ["--name", "my server"]
    ///   ""                           → []
    ///   "  a  b  "                   → ["a", "b"]
    ///   "\"unterminated"             → ["unterminated"]
    /// </summary>
    internal static string[] TokenizeArgs(string arguments)
    {
        if (string.IsNullOrWhiteSpace(arguments))
            return [];

        var tokens = new List<string>();
        var current = new System.Text.StringBuilder();
        var inQuote = false;
        var s = arguments.AsSpan();

        for (int i = 0; i < s.Length; i++)
        {
            var c = s[i];
            if (c == '"')
            {
                inQuote = !inQuote;
                // Don't include the quote character in the token.
                continue;
            }

            if (c == ' ' && !inQuote)
            {
                if (current.Length > 0)
                {
                    tokens.Add(current.ToString());
                    current.Clear();
                }
                continue;
            }

            current.Append(c);
        }

        // Flush any remaining token (also handles unterminated quote).
        if (current.Length > 0)
            tokens.Add(current.ToString());

        return [.. tokens];
    }
}
