// Smoke-test "MCP server" for 006-cli-stdio-bridge.
//
// Reads lines from stdin, writes "echoed: <line>" + newline back to stdout.
// Flushes after each write so the publisher's pump sees data immediately —
// without an explicit Flush() the Console writer can buffer indefinitely
// and the bridge will appear hung.
//
// This is NOT a real MCP server (no JSON-RPC framing, no initialize handshake).
// It exists solely to prove that Korat can bidirectionally pump bytes between
// a subprocess's stdio and a remote agent through the gateway relay.
// A real MCP server would be plugged in via `korat mcp add` the same way.

string? line;
while ((line = Console.In.ReadLine()) is not null)
{
    Console.Out.WriteLine($"echoed: {line}");
    Console.Out.Flush();
}
