# Hooking Claude Desktop up to a remote MCP server via Korat

**Audience**: a user who runs Claude Desktop on one machine and wants it to
talk to an MCP server (e.g. `@modelcontextprotocol/server-everything`, a
filesystem server, a tool you wrote yourself) that lives on a *different*
machine — without a VPN, public IP, or port forwarding.

The mechanism is `korat connect --bridge`: a long-lived stdio bridge that
Claude Desktop spawns as if it were a local MCP server, but which forwards
JSON-RPC through the Korat cloud relay to the real server on the other
machine.

## 1. Install korat

**macOS** (via Homebrew):
```bash
brew tap korat-ai/tap
brew install korat
korat version
```

**Linux** (via install script):
```bash
curl -fsSL https://get.korat.ai/install.sh | sh
export PATH="$HOME/.korat/bin:$PATH"
korat version
```

## Prerequisites

- A deployed Korat cloud you control (e.g. `https://my.korat.ai`). See
  docs/deployment-fly.md for the one-time setup.
- The owner secret for that cloud (`KORAT_DEV_OWNER_SECRET` — retrievable
  with `flyctl ssh console -a korat -C 'printenv KORAT_DEV_OWNER_SECRET'`).
- A **publisher** machine with the real MCP server registered via
  `korat mcp add <name> --command "..."` and the background service running
  (`korat service install`). (See `specs/016-cli-node-service/spec.md` for
  the publisher side; `korat up` also works as a foreground debug alternative.)
- A **grant** linking your agent-client to that MCP server. Created either
  through the access-request flow in the web UI or, for fast setup, via
  the developer API (see [quickstart](#one-time-grant-setup) below).
- Claude Desktop installed on the machine where you want to *use* the tool.
- A `korat` CLI binary on the **same** machine as Claude Desktop — installed
  in step 1 above.

## Paste-ready Claude Desktop config

Open `~/Library/Application Support/Claude/claude_desktop_config.json` and
add (or merge) the following block. **Replace `<placeholders>`**.

```json
{
  "mcpServers": {
    "everything-via-korat": {
      "command": "/usr/local/bin/korat",
      "args": [
        "connect",
        "everything-mcp",
        "--bridge",
        "--agent-client-id", "<your-agent-client-id>"
      ],
      "env": {
        "KORAT_CLOUD_URL":        "https://my.korat.ai",
        "KORAT_CLOUD_GRPC_URL":   "https://my.korat.ai:8443",
        "KORAT_DEV_OWNER_SECRET": "<your-owner-secret>"
      }
    }
  }
}
```

Restart Claude Desktop. The remote MCP server's tools should appear in
Claude Desktop's tool list on your next conversation.

### Why each piece matters

- **`command`** must be an **absolute path**. Claude Desktop's subprocess
  spawn does not go through your interactive shell PATH. Run `which korat`
  on a fresh terminal and paste that path here.
- **`--bridge`** activates the long-lived stdio mode (FR-001/FR-002 of
  spec 007). Without it, `korat connect` exits after `Access granted` and
  Claude Desktop cannot drive it. (`--send` is a one-shot test/smoke-check
  primitive and is not suitable for real MCP usage.)
- **`--agent-client-id`** ties this bridge to a specific Grant in your
  Space. Different Claude Desktop installs should use different
  agent-client ids so revoking one doesn't kill the others.
- **`env`** does NOT inherit your interactive shell environment when Claude
  Desktop spawns the bridge. Every variable the CLI needs MUST be listed
  here explicitly.

### If you only have a `dotnet` build, not a published binary

For development setups where the CLI lives inside the repo, use:

```json
"command": "/usr/local/bin/dotnet",
"args": [
  "run",
  "--no-build",
  "--project", "/Users/<you>/Korat MCP Hub/apps/Korat.Cli",
  "--",
  "connect", "everything-mcp",
  "--bridge",
  "--agent-client-id", "<your-agent-client-id>"
]
```

This works but adds 2-5 seconds of JIT startup to every Claude Desktop
restart. Publish a self-contained binary (above) for daily use.

## One-time grant setup

Skip this section if a grant already exists for the agent-client you want
to use. Otherwise: temporarily enable the developer API and create the
mcp-server / agent-client / grant via three POSTs. The developer API is
off in production by default — flip on, set up, flip off.

```bash
export KORAT_CLOUD_URL=https://my.korat.ai
export KORAT_DEV_OWNER_SECRET="$(flyctl ssh console -a korat -C 'printenv KORAT_DEV_OWNER_SECRET' | tr -d '\r\n ')"
flyctl secrets set KORAT_ENABLE_DEVELOPER_API=1 -a korat   # wait ~30 s for machine restart

PUB_NODE_ID=<the-publisher-machine's-NodeId-from-its-config.json>
TOKEN="$KORAT_DEV_OWNER_SECRET"
H="X-Korat-Owner-Token: $TOKEN"

SERVER_ID=$(curl -s -X POST "$KORAT_CLOUD_URL/api/developer/mcp-servers" \
  -H "$H" -H "Content-Type: application/json" \
  -d "{\"displayName\":\"everything-mcp\",\"nodeId\":\"$PUB_NODE_ID\"}" | jq -r .id)

AGENT_NODE_ID=$(curl -s -X POST "$KORAT_CLOUD_URL/api/developer/nodes" \
  -H "$H" -H "Content-Type: application/json" \
  -d '{"displayName":"my-laptop"}' | jq -r .id)

AGENT_CLIENT_ID=$(curl -s -X POST "$KORAT_CLOUD_URL/api/developer/agent-clients" \
  -H "$H" -H "Content-Type: application/json" \
  -d "{\"displayName\":\"my-laptop\",\"sourceNodeId\":\"$AGENT_NODE_ID\"}" | jq -r .id)

curl -s -X POST "$KORAT_CLOUD_URL/api/developer/grants" \
  -H "$H" -H "Content-Type: application/json" \
  -d "{\"agentClientId\":\"$AGENT_CLIENT_ID\",\"mcpServerId\":\"$SERVER_ID\"}"

echo "Paste this into claude_desktop_config.json under args:"
echo "  --agent-client-id $AGENT_CLIENT_ID"

flyctl secrets unset KORAT_ENABLE_DEVELOPER_API -a korat   # lock the API back down
```

In the regular alpha workflow this whole block becomes one click in the web
UI's "Approve" flow — the dev API path is for ops and tests, not end users.

## Troubleshooting

**"Server failed to initialize"** in Claude Desktop's UI.

Look at `~/Library/Logs/Claude/mcp-server-everything-via-korat.log`. Common
causes:

- `command` is not an absolute path. Claude Desktop literally can't find
  the binary. Run `which korat` and use that path.
- `env` block is missing the cloud URLs or owner secret. They DO NOT
  inherit from your shell. Paste them explicitly.
- The publisher side isn't running. On the publisher machine, ensure the
  background service is up (`korat service status`; install with
  `korat service install` if needed) or run `korat up` in the foreground
  for debugging. Wait for `Node ... online ->` before reopening Claude Desktop.
- The agent-client doesn't have a grant for this MCP server. Re-run the
  one-time grant setup above.

**"Access pending owner approval"** loops forever.

If you use the standard owner-approval flow (no developer API), the access
request lands at `https://my.korat.ai/space` and waits for you (the owner)
to approve it in the browser. Open that URL with your owner token and
approve the request. The bridge will pick it up on the next 2-second poll
and proceed to `Access granted`.

**Claude Desktop shows the tool but every call errors out.**

That usually means the publisher's MCP server crashed or hasn't started.
Check the publisher process logs:

```bash
tail -f /tmp/korat-publisher.log   # whatever path your publisher logs to
```

**The bridge prints noise on stdout (not JSON-RPC).**

This is a spec violation — file an issue. The bridge MUST emit only
verbatim relay-frame bytes on stdout (spec 007 FR-005 / SC-003). Stderr is
the right home for status lines.

## How this is wired internally

```
┌────────────────────┐  spawns                ┌─────────────────────┐
│   Claude Desktop   │ ───── stdio ─────────► │ korat connect       │
│  (machine A)       │                        │ --bridge subprocess │
└────────────────────┘                        └──────────┬──────────┘
                                                         │ TLS h2
                                                         ▼
                                              ┌────────────────────┐
                                              │ my.korat.ai:8443  │
                                              │ Caddy → Kestrel    │
                                              │ (Fly machine, fra) │
                                              └──────────┬─────────┘
                                                         │ relay frame
                                                         ▼
                                              ┌────────────────────┐
                                              │ korat service run  │
                                              │ (publisher daemon, │
                                              │  machine B)        │
                                              └──────────┬─────────┘
                                                         │ stdio
                                                         ▼
                                              ┌────────────────────┐
                                              │ real MCP server    │
                                              │ (npx server-…)     │
                                              └────────────────────┘
```

The bridge process on machine A is **transparent** — it does not parse,
inspect, or transform the JSON-RPC payload bytes (constitution principle
II, spec 007 FR-008). Bytes from stdin go straight onto the relay frame's
ciphertext field; bytes from inbound frames go straight to stdout. Claude
Desktop and the real MCP server effectively talk to each other directly,
just with the relay sitting between them.

## Spec / design references

- Spec: `specs/007-cli-bridge-mode/spec.md`
- Plan: `specs/007-cli-bridge-mode/plan.md`
- Research notes (MCP stdio framing, Claude Desktop schema):
  `specs/007-cli-bridge-mode/research.md`
- Quickstart (5-min validation without Claude Desktop):
  `specs/007-cli-bridge-mode/quickstart.md`

## Building from source (for contributors)

Most users should NOT need this — use brew or the install script above. This appendix is for contributors who want to build the CLI locally from a checkout of `korat-ai/mcp-hub`.

```bash
dotnet publish apps/Korat.Cli -c Release -r osx-arm64 --self-contained \
  -o /usr/local/lib/korat-cli
ln -sf /usr/local/lib/korat-cli/Korat.Cli /usr/local/bin/korat
which korat   # → /usr/local/bin/korat
```
