using System.CommandLine;
using Korat.Cli.Commands;
using Korat.Cli.Telemetry;
using Sentry;

// ---------------------------------------------------------------------------
// Initialize optional error telemetry before command dispatch.
// - No-op when KORAT_TELEMETRY=0 or no DSN is configured.
// - Opt-out: export KORAT_TELEMETRY=0 to disable entirely.
// - DSN is never hardcoded; it is either passed via KORAT_SENTRY_DSN env
//   or baked at publish time (-p:KoratSentryDsn=... in release.yml).
// ---------------------------------------------------------------------------

// Try to load the anonymous NodeId for non-PII Sentry tagging.
string? nodeId = null;
try
{
    var identity = new LocalIdentityStore().LoadOrCreate();
    nodeId = identity.NodeId;
}
catch { /* best-effort; do not block startup */ }

using var _sentry = SentryInit.TryInit(nodeId);

// ---------------------------------------------------------------------------
// Command tree
// ---------------------------------------------------------------------------

var root = new RootCommand("Korat CLI — publish and securely consume MCP servers through a private Space");

// ── Golden-path hint + glossary (#93/#100/#97) ──────────────────────────────
// Printed when the user runs bare `korat` with no subcommand. Keep the step order
// in sync with the NodeSetupSteps shown in the web UI.
root.SetHandler(() =>
{
    bool authenticated;
    try
    {
        // Authentication is tracked by the saved CLI credentials file (~/.korat/credentials).
        authenticated = System.IO.File.Exists(
            System.IO.Path.Combine(Korat.Cli.Config.KoratConfigPaths.BaseDir, "credentials"));
    }
    catch { authenticated = false; }

    Console.WriteLine("Korat CLI — publish and securely consume MCP servers through a private Space");
    Console.WriteLine();

    // Public relay vocabulary. Domain type names such as Node and Grant remain stable in APIs,
    // while the operator-facing terms describe what users actually manage.
    Console.WriteLine("Concepts:");
    Console.WriteLine("  Space       Your isolated tenant. Relay access never crosses Space boundaries.");
    Console.WriteLine("  Runtime     A live Korat transport endpoint; a publisher runtime is usually a device.");
    Console.WriteLine("  MCP server  A local or HTTP MCP endpoint published into the Space.");
    Console.WriteLine("  Consumer    A stable MCP client identity created by `korat connect --agent`.");
    Console.WriteLine("  Permission  Owner approval for one Consumer to use one MCP server.");
    Console.WriteLine("  Session          One active or historical relay connection.");
    Console.WriteLine();

    Console.WriteLine("Getting started — recommended order:");
    Console.WriteLine();
    Console.WriteLine("  1. korat login              Authenticate the CLI with your Korat Space");
    Console.WriteLine("  2. korat service install    Keep a publisher runtime online");
    Console.WriteLine("     or: korat up             Run it in the foreground for debugging");
    Console.WriteLine("  3. korat mcp add <name>     Register a local MCP server");
    Console.WriteLine("  4. korat mcp list --ids     Inspect real availability and stable server IDs");
    Console.WriteLine("  5. korat connect <server> --agent my-client");
    Console.WriteLine("                              Connect with a stable consumer identity");
    Console.WriteLine();

    if (!authenticated)
    {
        Console.WriteLine("Hint: you are not authenticated yet — run `korat login` to get started.");
        Console.WriteLine();
    }

    Console.WriteLine("Run `korat <command> --help` for full options.");
});

root.AddCommand(LoginCommand.Create());
root.AddCommand(LogoutCommand.Create());
root.AddCommand(UpCommand.Create());
root.AddCommand(ConnectCommand.Create());

var mcp = new Command("mcp", "Manage local MCP servers");
mcp.AddCommand(McpAddCommand.Create());
// Increment 1 (HTTP MCP direct-to-Space, Task 7): registers a cloud-hosted HTTP MCP server
// directly via POST /api/mcp-servers — NOT a local-node publish (see McpAddHttpCommand doc).
mcp.AddCommand(McpAddHttpCommand.Create());
mcp.AddCommand(McpRemoveCommand.Create());
mcp.AddCommand(McpListCommand.Create());
root.AddCommand(mcp);


root.AddCommand(ServiceCommand.Create());
root.AddCommand(StatusCommand.Create());
root.AddCommand(DoctorCommand.Create());
root.AddCommand(VersionCommand.Create());
root.AddCommand(UpgradeCommand.Create());

// `nodes` / `node` are retained as compatibility aliases for released scripts.
root.AddCommand(NodesCommand.Create());
var runtime = new Command("runtime", "Manage publisher runtime settings (owner notes)");
runtime.AddAlias("node");
runtime.AddCommand(NodeNoteCommand.Create());
root.AddCommand(runtime);

// ---------------------------------------------------------------------------
// Invocation with top-level error capture.
// Tags the event with the first non-option token as the command name so
// GlitchTip groups errors by command.  Argv values are NEVER attached
// (they may carry secrets/paths).
// ---------------------------------------------------------------------------

int exitCode;
try
{
    exitCode = await root.InvokeAsync(args);
}
catch (Exception ex)
{
    // Capture unhandled exceptions that escape System.CommandLine dispatch.
    var commandName = args.FirstOrDefault(a => !a.StartsWith('-')) ?? "unknown";
    SentrySdk.ConfigureScope(scope => scope.SetTag("command", commandName));
    SentrySdk.CaptureException(ex);
    await SentrySdk.FlushAsync(TimeSpan.FromSeconds(3));
    throw;
}

// Flush before exit so events are not lost on a short-lived CLI run.
await SentrySdk.FlushAsync(TimeSpan.FromSeconds(3));

return exitCode;
