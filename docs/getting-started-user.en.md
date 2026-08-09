# Using Korat MCP Hub

This guide assumes an operator has deployed Korat and given you its HTTPS URL.
Contributors running it locally should use [getting-started.md](getting-started.md).

## Sign in

Open `<cloud-url>/app/signin`. A deployment can enable GitHub, Google, or
email magic-link sign-in. New accounts normally need an invite; the configured
bootstrap administrator does not.

The default console contains:

- **Overview** — relay health and counts;
- **MCP servers** — published local and registered HTTP servers;
- **Access** — pending requests, permissions, and connected OAuth clients;
- **Activity** — active and historical relay sessions;
- **Runtimes** — live publisher transport endpoints.

Hosted agents, inference, channels, and rooms are optional deployment modules
and are not part of the default MCP relay navigation.

## Authenticate the CLI

```bash
korat login --cloud <cloud-url>
```

The CLI opens a device-approval page. Its revocable token is separate from the
browser session. Use `korat logout` to remove the local credential.

## Publish a local MCP server

```bash
korat mcp add my-server --command "<server launch command>"
korat up
```

`korat up` is the foreground publisher runtime. An installed CLI can use
`korat service install` for an always-on background publisher.

Inspect effective heartbeat-derived availability with:

```bash
korat status
korat runtimes
korat mcp list --ids
```

## Connect an MCP client

Configure one aggregated Space bridge:

```json
{
  "mcpServers": {
    "korat-space": {
      "command": "korat",
      "args": ["connect", "--space", "--bridge", "--agent", "my-client"]
    }
  }
}
```

`my-client` is a stable consumer identity. Reusing it preserves its permissions;
different names get independent permissions. Omitting `--agent` consistently
uses `default`.

The first connection to a server creates an access request. The owner approves
or denies it in **Access**. Revoking the resulting permission closes affected
active sessions immediately.

## Account security

Use **Account** to review browser sessions, connected sign-in providers, and CLI
tokens. Revoke any credential you do not recognize.
