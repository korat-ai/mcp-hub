# MVP Acceptance — 2026-05-27

> Historical acceptance record for the original relay slice. Its test counts,
> deferred features, security posture, commands, and next steps describe that
> 2026-05-27 milestone, not the current product. Use
> [../README.md](../README.md), [../ARCHITECTURE.md](../ARCHITECTURE.md), and
> [trust-and-privacy.md](trust-and-privacy.md) for current behavior.

This document is the **formal record of MVP completion** for Korat MCP Hub. It
captures (1) what was built, (2) what was tested, (3) what was deferred
explicitly, and (4) the literal evidence proving the MVP goal.

## MVP goal (verbatim from `docs/mvp-scope.md`)

> Prove that a user can connect an agent on one machine to an MCP server running
> on another machine through Korat, with explicit user permission and no payload
> logging.

## Goal status: ✅ ACHIEVED

A subagent ran `scripts/mvp-real-mcp-demo.sh` and received a literal JSON-RPC
response from `@modelcontextprotocol/server-everything` v2.0.0 routed through
the full Korat relay path.

### Captured proof

```json
{
  "result": {
    "protocolVersion": "2024-11-05",
    "capabilities": {
      "tools":      { "listChanged": true },
      "prompts":    { "listChanged": true },
      "resources":  { "subscribe": true, "listChanged": true },
      "logging":    {},
      "tasks":      { "list": {}, "cancel": {}, "requests": { "tools": { "call": {} } } },
      "completions": {}
    },
    "serverInfo": {
      "name":    "mcp-servers/everything",
      "title":   "Everything Reference Server",
      "version": "2.0.0"
    },
    "instructions": "# Everything Server – Server Instructions\n\n..."
  },
  "jsonrpc": "2.0",
  "id": 1
}
```

### Path traversed

```
subagent (bash)
  → korat connect CLI (agent role)
    → gRPC NodeToGatewayMessage.Frame
      → Korat.Cloud (NodeGatewayService)
        → SessionRoutingTable.ForwardFrameAsync (agent → publisher peer lookup)
          → gRPC GatewayToNodeMessage.Frame
            → korat up CLI (publisher role)
              → SessionBridge.OnFrameReceivedAsync (subprocess stdin pump)
                → npx @modelcontextprotocol/server-everything stdio
                  → real MCP server processes initialize
                ← server writes JSON-RPC response to stdout
              ← McpServerProcess.StdoutPumpAsync wraps in Frame
            ← gRPC frame back to Cloud
          ← ForwardFrameAsync forwards to agent
        ← gRPC GatewayToNodeMessage.Frame
      ← Cloud routes to agent stream
    ← ConnectCommand.RunOneShotExchangeAsync reads, unwraps
  ← prints JSON to stdout
subagent captures, asserts contains "jsonrpc":"2.0" + "result" + "id"
```

11 hops, ~2 seconds wall, zero errors on first run.

## What was built (sub-projects)

| # | Sub-project | Status | Tests |
|---|-------------|--------|-------|
| A | 003-local-dev-access stabilization (CLI / Gateway / Web / Tests + security cluster) | DONE | 93 |
| B | 004-developer-api (`/api/developer/**` HTTP surface, 9 endpoints) | DONE | 165 |
| C | 005-mvp-relay-minimal (`SessionRoutingTable` + frame routing in gateway) | DONE | 166 |
| D | 006-cli-stdio-bridge (real CLI ↔ subprocess pumping, EchoMcp demo) | DONE | 167 |
| E | Real-MCP demo (npx @modelcontextprotocol/server-everything via subagent) | DONE | demo script |

Final test count: **167 automated tests, 0 failures**, plus 1 gated E2E
(`KORAT_E2E_RUN=1`) and 1 standalone real-MCP demo script.

## What was explicitly deferred (MVP cuts)

Documented in `docs/decision-log.md` § "C-Minimal MVP Cut" and § "Stdio-Bridge
Pumping Model":

1. **Constitution II (cloud never sees payload)** — frames are cleartext today.
   The gateway can read frame contents. Follow-on adds E2E key exchange at node
   Hello-time so `RelayFrame.ciphertext` becomes actually encrypted; no gateway
   changes needed for that swap.

2. **Constitution IX (payload size limits)** — `PayloadLimitPolicy` is
   advertised on `SessionOpened` but not enforced inbound. 16 MB message /
   50 MB session warning / 250 MB hard limit deferred to follow-on.

3. **Revoke-during-active-session** — if a grant is revoked while frames flow,
   in-flight frames keep flowing until natural stream disconnect.

4. **Single-silo routing** — `SessionRoutingTable` is in-process. Cross-silo
   peer lookup is a follow-on. Production needs an inter-silo hop (Orleans
   grain or out-of-band gateway-to-gateway stream).

5. **Continuous-stdio bridge mode** (`korat connect --bridge`) — the agent CLI
   today supports `--send <message> --wait-response` (one-shot). For Claude
   Desktop or other MCP clients to use Korat as a transparent stdio adapter,
   the continuous-pipe mode is the next user-facing milestone. All plumbing
   exists; it's an additive `--bridge` flag in `ConnectCommand`.

6. **Real MCP framing (JSON-RPC Content-Length)** — the publisher's stdout
   pump reads raw bytes and forwards them, which works for newline-delimited
   stdio servers. Servers using `Content-Length:` framing will need a parser
   on the publisher side.

7. **G11 Postgres migration** — `tests/FOLLOWUPS.md` `G11-MIGRATION-FOR-POSTGRES`
   describes the EF migration required to alter Status columns to `varchar`
   and add the filtered-unique index. Must be generated before any non-InMemory
   deployment.

## How to reproduce on your machine

### One command (assumes Docker + .NET 10 SDK + npm/npx)

```bash
cd /path/to/mcp-hub
docker compose up -d && dotnet build && bash scripts/mvp-real-mcp-demo.sh
```

Expected: `✅ MVP REAL-MCP DEMO PASSED` plus the JSON above.

### Three-terminal walk-through (manual)

See `docs/mvp-scope.md` § "Running the demo on your machine".

### Automated regression

```bash
KORAT_E2E_RUN=1 dotnet test tests/Korat.EndToEnd.Tests/
```

Runs the same scenario via xUnit so CI can guard the demo as a regression test.

## Trust + privacy posture in the demo

- ✅ Explicit grant required before any frame routes (verified by
  `RelayFrameForwardingTests.FrameRoundTrip_*` failure case: no grant →
  `AccessPending` returned, no frames forward).
- ✅ Owner approval is the grant-creating action (`/api/access-requests/{id}/approve`
  via owner cookie, or dev shortcut via `/api/developer/grants` for the
  agent-driven demo).
- ⚠️ Payload privacy NOT yet structural — see deferral #1 above. Mitigation:
  feature is dev-only, single-machine demo; no third-party traffic.
- ✅ No payload-shaped fields in any logged metadata
  (`PayloadPrivacyTests`, `DeveloperApiPayloadAuditTests`).

## What this is NOT yet

- A productized MVP. Real users will need:
  - Continuous bridge mode (#5)
  - Real MCP framing (#6)
  - Postgres migration applied (#7)
  - E2E encryption (#1)
  - Payload limits enforced (#2)
- A multi-machine demo. Tested only on `localhost` so far.
- Encrypted in transit. Cleartext frames during MVP cut.

## Architecture decisions of record

See `docs/decision-log.md` entries dated 2026-05-27:

- Developer API Surface
- Developer API Gating (env-only, no config-flag)
- OpenAPI as Developer API Contract
- No Auth on Developer Endpoints
- Hybrid Realism for Mock Entities
- C-Minimal MVP Cut: Cleartext Frames for First Demo
- Stdio-Bridge Pumping Model

## Next session entry points

1. **Bridge mode** — extend `ConnectCommand` with `--bridge` (continuous stdio).
   Cleanest next user-facing step. Estimated 1 session.
2. **E2E encryption** — implement key-exchange at Hello, encrypt frames at node
   ends. Restores constitution II. Estimated 2-3 sessions.
3. **Payload limits enforcement** — wire `PayloadLimitPolicy` into the inbound
   Frame handler. Estimated 1 session.
4. **Cross-silo routing** — replace in-process `SessionRoutingTable` with
   Orleans-grain or NATS-backed peer discovery. Estimated 2 sessions.

These four together complete the original 001-remote-mcp-relay spec scope
beyond the MVP cut.
