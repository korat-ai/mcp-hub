# Korat.Demo.EchoMcp

Smoke-test binary for the 006-cli-stdio-bridge MVP relay demo.

This is **not a real MCP server**. It has no JSON-RPC framing, no `initialize`
handshake, no tool/resource surface. It simply reads lines from stdin and
writes `echoed: <line>` back to stdout, flushing after every write.

Its sole purpose is to prove that Korat can bidirectionally pump raw bytes
between a subprocess's stdio and a remote agent through the gateway relay.
A real MCP server would be plugged in via `korat mcp add` exactly the same
way once the stdio bridge itself is verified.

## Usage

```bash
# Manually (for local sanity check):
dotnet run --project apps/Korat.Demo.EchoMcp
# Type "hello" + Enter → prints "echoed: hello"

# Through Korat (the actual demo):
korat mcp add echo --command "dotnet run --project apps/Korat.Demo.EchoMcp"
korat up --serve echo                       # in another terminal
korat connect echo --send "ping" --wait-response   # in a third terminal
# Expected output: "echoed: ping"
```
