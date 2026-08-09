# Korat MCP Hub

Korat is a private trust and relay layer for MCP. It lets an MCP client use
approved MCP servers from another runtime without exposing those servers to the
public internet or configuring a general-purpose VPN.

```text
MCP client
  → Korat consumer identity
  → owner-approved permission
  → Korat relay
  → local or HTTP MCP server
```

Korat is not a model provider, chatbot, or hosted-agent platform. The
default CLI and console focus on publishing MCP servers, approving access,
showing real availability, and inspecting relay activity.

## What it does

- Publishes local stdio MCP servers from an always-on publisher runtime.
- Registers cloud-reachable HTTP MCP servers that do not need a publisher
  runtime.
- Gives every MCP client a stable consumer identity.
- Turns an access request into an explicit, revocable permission.
- Relays MCP traffic and records session metadata and byte counts, not payloads.
- Exposes one aggregated Space-MCP endpoint for clients that want all of their
  approved servers behind one MCP connection.
- Terminates active sessions when their permission is revoked.

All of these operations are scoped to one **Space**. Korat does not route or
grant access across Space boundaries.

## Public product model

| User-facing term | Meaning | Current domain/API name |
|---|---|---|
| Space | Tenant and authorization boundary | `Space` |
| Runtime | Live Korat transport endpoint; a publisher runtime is usually a device | `Node` |
| MCP server | A local stdio or cloud-reachable HTTP MCP endpoint | `McpServer` |
| Consumer | Stable identity of the MCP client using a server | `Consumer` |
| Permission | Approval for one consumer to use one MCP server | `Grant` |
| Activity | Active and historical relay sessions | `RelaySession` |

`Node` and `Grant` remain stable internal/API terms for compatibility. The
console and the main CLI path use **runtime** and **permission**, because a
consumer identity is not necessarily a distinct machine and an approved access
record is easier to understand as a permission.

## Quick start

Prerequisites are .NET 10, Node.js 20 or newer, npm, and Docker. The repository
pins the .NET SDK feature band in [`global.json`](global.json).

```bash
cd apps/Korat.App
npm ci
npm run build
cd ../..

docker compose up -d
Bootstrap__AdminEmail=you@example.com \
  dotnet run --project apps/Korat.Cloud
```

Sign in at <http://localhost:5191/app/signin>, then authenticate the CLI in
another terminal:

```bash
dotnet run --project apps/Korat.Cli -- login \
  --cloud http://localhost:5191 \
  --grpc http://localhost:5192

dotnet run --project apps/Korat.Cli -- mcp add everything \
  --command "npx -y @modelcontextprotocol/server-everything"
dotnet run --project apps/Korat.Cli -- up
dotnet run --project apps/Korat.Cli -- mcp list --ids
```

For the complete local relay walkthrough, including a real MCP subprocess and
access approval from a stable consumer identity, see
[Getting started](docs/getting-started.md).

## Inspecting the relay

The CLI and console derive effective status from heartbeats rather than trusting
the last stored `Online` value.

```bash
korat status
korat runtimes
korat runtimes --all       # include internal consumer identities for diagnostics
korat mcp list --ids
korat mcp list --json
```

`nodes` and `node` remain aliases for `runtimes` and `runtime` so existing
scripts keep working.

The default console navigation is:

```text
Overview · MCP servers · Access · Activity · Runtimes
```

Connected OAuth clients live inside **Access**.

## Repository layout

- `apps/Korat.Cloud` — ASP.NET Core API, auth, Orleans silo, relay gateway,
  Space-MCP endpoint, and SPA host.
- `apps/Korat.App` — React/TanStack browser console.
- `apps/Korat.Cli` — operator CLI, publisher runtime, and MCP stdio bridge.
- `src/Korat.Domain` — domain entities, state transitions, and shared presence
  rules.
- `src/Korat.Grains` and `src/Korat.GrainInterfaces` — Orleans control plane.
- `src/Korat.Persistence` — EF Core/PostgreSQL persistence.
- `src/Korat.Protocol` — gRPC relay and optional E2E protocol.
- `tests` — unit, contract, integration, persistence, and end-to-end projects.

Start with the [documentation index](docs/README.md), [developer onboarding](docs/dev/README.md),
and [architecture](ARCHITECTURE.md).

## Development checks

```bash
dotnet build Korat.slnx
dotnet test Korat.slnx

cd apps/Korat.App
npm ci
npm run build
npm test
```

Some persistence suites use Testcontainers and therefore require Docker.
Contribution and security-reporting guidance lives in
[CONTRIBUTING.md](CONTRIBUTING.md) and [SECURITY.md](SECURITY.md).

## License

Copyright 2026 Korat AI. Korat MCP Hub is open source under the **Apache
License 2.0** — see [`LICENSE`](LICENSE).

Use it, modify it, redistribute it, self-host it, build products on it,
commercially included. Keep the license and notices with the code and mark the
files you changed. Apache-2.0 rather than MIT for the explicit patent grant
(§3): contributing code also licenses the patents that code needs.

See [Open-source readiness](OPEN_SOURCE_READINESS.md) for the release checklist
and [licensing notes](docs/licensing-notes.md) for what the choice does and does
not decide.
