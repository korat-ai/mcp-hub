# MVP Scope

Last updated: 2026-05-25

> Historical scope and acceptance record. Several items described below as
> deferred were implemented after the MVP. Current behavior is documented in
> [../ARCHITECTURE.md](../ARCHITECTURE.md).

## MVP Goal

Prove that a user can connect an agent on one machine to an MCP server running on another machine through Korat, with explicit user permission and no payload logging.

## Primary Scenario

Home machine:

```bash
korat login
korat service install
korat mcp add ableton --command "..."
korat service status
```

Second machine:

```bash
korat login
korat mcp list
korat connect ableton
```

Product expectation:

- the home machine appears as a Node;
- the local MCP appears as a published MCP Server;
- the second machine appears as another Node;
- an Agent Client on the second machine can request access;
- the user explicitly approves access;
- the remote agent can make a successful MCP call.

## In Scope

- Personal account.
- Private Space per user.
- Device login.
- Node registration.
- Local MCP server publication.
- Remote agent-client connection.
- Explicit access grants.
- Revoke or disable access.
- Online/offline status.
- Last seen timestamp.
- Active session visibility.
- Transport and connection error visibility.
- No payload logging.
- CLI-first setup.
- Minimal web UI for visibility and approval.
- Explicit payload size limits for the first relay-based version.
- Clear user-facing errors when a payload is too large for the current version.
- Transfer metadata such as byte counts and large-transfer warnings, without payload inspection.

## Out Of Scope

- Team accounts.
- Enterprise admin controls.
- Billing.
- Hosted MCP servers.
- MCP marketplace.
- Public sharing links.
- Cross-user sharing.
- Complex policy language.
- Full audit log of tool payloads.
- Agent runtime.
- Model routing.
- Prompt inspection.
- Unlimited file transfer.
- Bulk data movement workflows.
- Direct node-to-node transport.
- Peer-to-peer file transfer.
- Cloud-hosted aggregated MCP endpoint.
- Built-in cloud connectors such as Gmail, calendar, storage, or other SaaS tools.

## Payload Limits

Korat v1 should be explicit that it supports remote MCP tool calls, not unlimited data transfer.

Initial proposed limits:

- maximum size of one MCP message: 16 MB;
- large-transfer warning per session: 50 MB;
- hard transfer limit per session: 250 MB.

These numbers are starting product defaults and may change after testing.

Expected behavior:

- small and medium MCP tool calls should work normally;
- oversized responses should fail with a clear explanation;
- the web UI should show transfer metadata, not payload contents;
- payload limits should be described as a version-1 relay limitation, not as a permanent product boundary.

Large payload and file transfer support is strategically important but deferred. A later phase should support this better, likely through direct encrypted node-to-node transport or another peer-to-peer-style mode with cloud relay fallback.

## Success Metrics

Activation:

- Time to first remote MCP call.
- Percent of users who successfully publish first MCP server.
- Percent of users who successfully connect a second machine.

Retention:

- Weekly active routed MCP sessions.
- Users with at least one active Node after 7 days.
- Users who connect more than one MCP server.

Reliability:

- Session connection success rate.
- Reconnect success rate.
- Failed remote MCP calls.
- Median and p95 call latency added by Korat.

North Star for early product validation:

> Successful remote MCP calls per active user per week.

## Current MVP Status (2026-05-27)

### What works today

As of `006-cli-stdio-bridge`, the **full demo runs end-to-end with real CLI binaries** —
publisher CLI spawns an MCP-server subprocess, the agent CLI sends bytes through
Korat, the subprocess writes a reply, and the agent prints it. See
"Running the demo on your machine" below for the three-terminal walkthrough and
`tests/Korat.EndToEnd.Tests/MvpDemoEndToEndTests.cs` for the automated regression.

The MVP architecture is **proven** — an automated integration test demonstrates the full agent → cloud → publisher → cloud → agent round-trip path:

```bash
cd /path/to/mcp-hub
docker compose up -d                      # Postgres
dotnet test --filter "FullyQualifiedName~RelayFrameForwardingTests"
```

Result: `FrameRoundTrip_AgentToPublisher_AndBack` passes in <1s, demonstrating:

1. Two `Korat.Node` gRPC streams (publisher + agent) connect to Cloud and complete Hello.
2. Publisher publishes a virtual MCP server (via `ISpaceGrain.PublishMcpServerAsync`).
3. Agent-client identity registered, grant created via grain layer.
4. Agent sends `RequestSession` → receives `SessionOpened` (trust gate enforced).
5. Agent sends `RelayFrame { session_id, ciphertext="hello" }` → publisher stream receives it through `SessionRoutingTable` forwarding inside `NodeGatewayService`.
6. Publisher sends `RelayFrame { session_id, ciphertext="world" }` back → agent stream receives it.

Plus the full `/api/developer/**` HTTP surface (9 endpoints, 165/165 contract+integration tests) lets an implementing agent set up the entire trust scenario via curl without any UI.

### What's deferred (explicit MVP cuts)

Documented in `docs/decision-log.md` § "C-Minimal MVP Cut":

- **Cleartext frames** — constitution II (cloud-never-sees-payload) is deferred. Frame.ciphertext is plaintext bytes for the relay slice; the gateway can read them. Follow-on adds end-to-end key exchange at node-Hello time.
- **No payload size limits** — constitution IX deferred. PayloadLimitPolicy is advertised on SessionOpened but not enforced inbound.
- **No revoke-during-active-session** — in-flight frames keep flowing until stream disconnect.
- **Single-silo routing** — `SessionRoutingTable` is in-process; cross-silo routing is a follow-on.
- **No real-CLI / real-MCP-server demo** — the integration test simulates both ends. Wiring `apps/Korat.Cli` and `apps/Korat.Node` to spawn an actual MCP-server subprocess and pump bytes between its stdio and the gateway stream is a productization step, not a proof-of-concept step. See `apps/Korat.Cli/Commands/UpCommand.cs` and `apps/Korat.Cli/Commands/ConnectCommand.cs` — both already speak gRPC to the gateway; the missing piece is the stdio↔frame pump.

### How to demonstrate it yourself

The Developer API quickstart at `specs/004-developer-api/quickstart.md` walks through setting up nodes/servers/agents/grants via `curl`. The relay test above is the architectural smoke. Together they cover the "agent on machine A calls MCP server on machine B" scenario at the protocol level.

## Running the demo on your machine

The 006 stdio bridge wires real subprocesses to relay frames. To exercise it end-to-end on a single machine:

### Terminal 1 — Cloud

```bash
cd /path/to/mcp-hub
docker compose up -d                # Postgres on :5432
dotnet run --project apps/Korat.Cloud
# Listens on http://localhost:5191 (REST/UI) and http://localhost:5192 (gRPC, HTTP/2)
```

The dev cloud now binds **two ports**: 5191 for REST + browser UI (HTTP/1.1) and
5192 for the gRPC node-gateway (HTTP/2 prior-knowledge over plaintext). This is
required because Kestrel cannot multiplex HTTP/1.1 and HTTP/2 on a single plain
TCP endpoint. Production swaps in TLS and collapses back to one port.

### Terminal 2 — Publisher (the MCP-server side)

```bash
cd /path/to/mcp-hub
dotnet build                        # ensures the echo demo DLL exists
korat login
korat mcp add echo --command "dotnet exec apps/Korat.Demo.EchoMcp/bin/Debug/net10.0/Korat.Demo.EchoMcp.dll"
korat up
# Output:
#   Node <node-id> (<machine-name>) online -> http://localhost:5191
#   Serving MCP 'echo' via stdio bridge.
#   Press Ctrl+C to stop.
```

`korat mcp add` persists the launch command in the local config. `korat up` (no
flags) runs **all** registered servers in the foreground — useful for debugging.
For production use, `korat service install` installs an always-on daemon that
picks up new servers automatically. The node stays online and heartbeats the
cloud until interrupted.

### Terminal 3 — Agent (the requester side)

The agent needs a separate `KORAT_CONFIG` so it gets a fresh NodeId (otherwise
both terminals share the publisher's identity).

```bash
export KORAT_CONFIG=$HOME/.config/korat/agent-config.json
korat login

# In dev mode, mint an agent-client + grant via the developer API:
AGENT_ID=$(curl -sS -X POST http://localhost:5191/api/developer/agent-clients \
  -H "X-Korat-Owner-Token: dev-owner-secret" -H "Content-Type: application/json" \
  -d '{"displayName":"my-agent"}' | jq -r .id)
SERVER_ID=$(curl -sS http://localhost:5191/api/space \
  -H "X-Korat-Owner-Token: dev-owner-secret" \
  | jq -r '.mcpServers[] | select(.displayName=="echo") | .id')
curl -sS -X POST http://localhost:5191/api/developer/grants \
  -H "X-Korat-Owner-Token: dev-owner-secret" -H "Content-Type: application/json" \
  -d "{\"agentClientId\":\"$AGENT_ID\",\"mcpServerId\":\"$SERVER_ID\"}"

korat connect echo --send "hello korat" --wait-response --agent-client-id "$AGENT_ID"
# Expected output (the publisher subprocess echoes the input back):
#   Access granted. Session <session-id> ready.
#   echoed: hello korat
# Exit code: 0
```

### Automated regression

`tests/Korat.EndToEnd.Tests/MvpDemoEndToEndTests.cs` runs this exact flow with
`Process.Start`, spawning a real Cloud and two CLI processes and asserting the
agent prints `echoed: ping`. The test is gated behind `KORAT_E2E_RUN=1` because
it spawns subprocesses (~5–10s):

```bash
KORAT_E2E_RUN=1 dotnet test --filter MvpDemo
```

This is the regression-protected proof of the Internal Alpha gate.

## Real-MCP demo (verified 2026-05-27)

### What was proved

A live run of `scripts/mvp-real-mcp-demo.sh` on 2026-05-27 demonstrated the full
agent → Korat relay → real MCP server → relay → agent round-trip using
`@modelcontextprotocol/server-everything` (an officially published real MCP server,
not a mock).

This is the **formal acceptance proof** of the MVP goal: "agent communicates with a
real MCP server through Korat".

### How to run it again

```bash
cd /path/to/mcp-hub
docker compose up -d          # Postgres on :5432
dotnet build                  # ensure binaries are current
bash scripts/mvp-real-mcp-demo.sh
```

Expected output concludes with:

```
============================================================
MVP REAL-MCP DEMO PASSED
============================================================

Captured MCP response from @modelcontextprotocol/server-everything through Korat relay:

{"result":{"protocolVersion":"2024-11-05","capabilities":{"tools":{"listChanged":true},...},"serverInfo":{"name":"mcp-servers/everything","title":"Everything Reference Server","version":"2.0.0"},...},"jsonrpc":"2.0","id":1}
```

### Actual captured response (2026-05-27 run)

```json
{"result":{"protocolVersion":"2024-11-05","capabilities":{"tools":{"listChanged":true},"prompts":{"listChanged":true},"resources":{"subscribe":true,"listChanged":true},"logging":{},"tasks":{"list":{},"cancel":{},"requests":{"tools":{"call":{}}}},"completions":{}},"serverInfo":{"name":"mcp-servers/everything","title":"Everything Reference Server","version":"2.0.0"},"instructions":"# Everything Server – Server Instructions\n\nAudience: These instructions are written for an LLM or autonomous agent integrating with the Everything MCP Server.\n..."},"jsonrpc":"2.0","id":1}
```

The response contains `"jsonrpc":"2.0"` and a full `"result"` object from the real
MCP server, confirming the initialize request was processed by the real subprocess
spawned by the publisher CLI and the response was relayed back through Korat to the
agent CLI.

### What the demo exercises

- `korat up` (with `everything-mcp` registered via `korat mcp add`) spawns `npx -y @modelcontextprotocol/server-everything stdio` as a subprocess.
- The publisher node bridges the subprocess stdio to Korat relay frames via `SessionBridge` + `McpServerProcess`.
- `korat connect --send '<MCP initialize JSON-RPC>' --wait-response` sends one frame through Cloud relay.
- Cloud routes the frame from the agent's gRPC stream to the publisher's gRPC stream.
- The publisher writes the frame bytes to the MCP subprocess stdin; the subprocess writes its JSON-RPC response to stdout.
- The stdout bytes are forwarded back through Cloud to the agent's stream and printed.
- Agent exit code 0; response contains `"jsonrpc":"2.0"` and `"result"` fields.

## Launch Phases

### Internal Alpha

Audience: project team and trusted technical users.

Gate:

- one successful end-to-end remote MCP call **with a real MCP server binary** (the integration test demonstrates the relay; this gate requires CLI/Node stdio wiring on top);
- revoke works (revoke-during-active-session needed; not yet implemented);
- no payload logging in local or server logs (verified by constitution-II tests today; will need re-verification once E2E encryption lands);
- status is visible (verified — `GET /api/sessions` returns metadata).

### Closed Beta

Audience: MCP power users and agent developers.

Gate:

- users can onboard without live help;
- connection reliability is acceptable for real workflows;
- users understand the trust model.

### Public Source-Available Release

Audience: broader developer community.

Gate:

- license is selected (Apache-2.0);
- README is clear;
- security posture is documented;
- basic install path is stable.
