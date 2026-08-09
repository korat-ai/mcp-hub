# Getting started

This guide runs one Korat Cloud, publishes a real stdio MCP server, approves a
consumer, and sends a JSON-RPC request through the relay.

## Prerequisites

- .NET 10 SDK
- Node.js 20 or newer and npm
- Docker with Compose v2
- a browser

Clone the repository and work from its root:

```bash
git clone https://github.com/korat-ai/mcp-hub.git
cd mcp-hub
```

## 1. Build the console and start Cloud

The React console is embedded in the Cloud host. Build it once before starting
a Development host:

```bash
cd apps/Korat.App
npm ci
npm run build
cd ../..

docker compose up -d
dotnet build
```

Choose the email for the first local administrator and start Cloud:

```bash
Bootstrap__AdminEmail=you@example.com \
  dotnet run --project apps/Korat.Cloud
```

Cloud serves the REST API and console at <http://localhost:5191> and the local
plaintext gRPC gateway at <http://localhost:5192>.

Open <http://localhost:5191/app/signin> and sign in with the configured email.
OAuth works when its provider credentials are configured. In Development, a
magic-link URL is written to the Cloud log when no Resend API key is present.
The matching bootstrap email creates the first administrator without an invite.

Leave Cloud running.

## 2. Authenticate the CLI

In a second terminal, run the device flow:

```bash
dotnet run --project apps/Korat.Cli -- login \
  --cloud http://localhost:5191 \
  --grpc http://localhost:5192
```

The CLI opens an approval page in the signed-in browser. After approval it
stores the CLI token separately from the browser session and points the local
runtime at this Cloud.

## 3. Publish a real MCP server

Register the MCP reference server and run the publisher in the foreground:

```bash
dotnet run --project apps/Korat.Cli -- mcp add everything \
  --command "npx -y @modelcontextprotocol/server-everything"

dotnet run --project apps/Korat.Cli -- up
```

The first request may take longer while `npx` downloads the package. `korat up`
keeps the publisher runtime online and publishes every locally registered
server. Leave it running.

For an installed `korat` binary, `korat service install` can run the publisher
as a background service. During repository development, use `dotnet run ... up`
so the service does not capture a transient `dotnet run` command.

## 4. Request and approve access

In a third terminal, send a one-shot MCP request:

```bash
dotnet run --project apps/Korat.Cli -- connect everything \
  --agent smoke-test \
  --send '{"jsonrpc":"2.0","id":1,"method":"tools/list"}'
```

The first call creates a permission request and opens its approval page. Choose
**Allow access**. The waiting CLI reuses the same connection, completes the MCP
`initialize` handshake, sends `tools/list`, and prints the JSON-RPC response.

`smoke-test` is a stable consumer identity. Reusing that name reuses its
permissions; a different `--agent` name creates an independently permissioned
consumer. Omitting `--agent` consistently reuses the identity named `default`.

## 5. Connect an MCP client

For a real MCP client, configure Korat as a local stdio command:

```json
{
  "mcpServers": {
    "korat-space": {
      "command": "korat",
      "args": [
        "connect",
        "--space",
        "--bridge",
        "--agent",
        "my-client"
      ]
    }
  }
}
```

`--space` exposes all servers granted to that consumer through one MCP
connection. To expose one server instead, use:

```bash
korat connect everything --bridge --agent my-client
```

## Inspect and troubleshoot

```bash
korat status
korat runtimes
korat mcp list --ids
korat mcp list --json
```

- If Cloud cannot reach PostgreSQL, run `docker compose ps` and confirm port
  `5432` is free.
- If the CLI reports `HTTP_1_1_REQUIRED`, its gateway URL points to the REST
  port. Local gRPC must use `http://localhost:5192`.
- If access stays pending, open the printed approval URL or use **Access** in the
  console.
- If a server is unavailable, keep its publisher `korat up` process running and
  check `korat runtimes`; availability is derived from recent heartbeats.
- If `/app/signin` says the SPA is missing, rerun `npm run build` in
  `apps/Korat.App`.

For implementation details, continue with [the developer guide](dev/README.md)
and [the current architecture](../ARCHITECTURE.md).
