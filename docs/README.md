# Korat MCP Hub — Documentation Index

**Audience**: new contributors, operators, and reviewers landing in this repo for the first time.

Korat is an application-layer trust and relay network for MCP servers. Read
this index, then pick the document that matches what you are about to do.

## Start here

- **[../README.md](../README.md)** — current product model, quick start, default
  console surface, and license.
- **[../OPEN_SOURCE_READINESS.md](../OPEN_SOURCE_READINESS.md)** — what is
  already cleaned up and what still blocks a public open-source release.
- **[getting-started.md](getting-started.md)** — build the console, bootstrap a
  local administrator, publish a real MCP server, approve a stable consumer,
  and verify the relay.
- **[dev/architecture.md](dev/architecture.md)** and
  **[../ARCHITECTURE.md](../ARCHITECTURE.md)** — topology, component breakdown,
  data model, relay flow, and trust model. Read before changing protocol,
  gateway, or grain code.

## Product framing (read once, then refer back)

- **[product-brief.md](product-brief.md)** — what Korat is, who it is for, and the "Tailscale for MCP" metaphor.
- **[public-roadmap.md](public-roadmap.md)** — directional future roadmap;
  optional agent/channel work is not part of the default MCP relay surface.
- **[mvp-scope.md](mvp-scope.md)** — what was promised for the MVP and what was explicitly out of scope.
- **[mvp-acceptance.md](mvp-acceptance.md)** — formal record of MVP completion (2026-05-27), including the literal JSON-RPC response captured from `@modelcontextprotocol/server-everything` through the Korat relay.
- **[trust-and-privacy.md](trust-and-privacy.md)** — the no-payload-logging promise and the metadata Korat is allowed to see.
- **[security/threat-model.md](security/threat-model.md)** — what Korat protects, and which boundaries it deliberately does not enforce.
- **[licensing-notes.md](licensing-notes.md)** — why Apache-2.0, what it
  settles, and the release gate it does not close.

## Operations

- **deployment-fly.md** — reference Fly.io deployment
  topology. Replace example app names and domains with values owned by your
  deployment, and review every secret before use.
- **[../SECURITY.md](../SECURITY.md)** — supported reporting path and the
  security boundaries maintainers should preserve.

## Source-of-truth references

- **[decision-log.md](decision-log.md)** — every architectural and security decision with rationale. If you are wondering *why* something is the way it is, this file is the answer before the code is. **Do not duplicate its contents into other docs — link to specific sections instead.**
- **../.specify/memory/constitution.md** — the non-negotiable principles (trust before transport, payload privacy, user-visible control, etc.). Any new spec must pass the Constitution Check.

## Feature specs

Per-feature specs live under `../specs/<id>-<slug>/`. Each contains `spec.md`, `plan.md`, `tasks.md`, and usually `quickstart.md`. Notable entries:

### Relay / bridge

- `001-remote-mcp-relay` — the full relay architecture (cleartext + encrypted slices).
- `005-mvp-relay-minimal` — the cleartext frame-forwarding slice that proved end-to-end routing.
- `006-cli-stdio-bridge` — wires `korat up` and `korat connect` to real subprocesses and the two-port dev cloud.
- `007-cli-bridge-mode` — long-lived `--bridge` stdio mode for Claude Desktop and other MCP clients.
- `009-nats-relay-backplane` — NATS Core pub/sub relay backplane (replaces in-process fan-out).
- `022-per-connection-relay-routing` — per-connection routing table for multi-silo relay.
- `031-relay-confidentiality` — payload E2E encryption between publisher and agent (`--e2e prefer|require|off`).
- `032-leg3-hardening` — cloud-side hardening for the confidentiality leg (session isolation, metadata clearing).

### Persistence / control plane

- `002-control-plane-persistence` — Postgres + EF Core schema for durable metadata.
- `003-local-dev-access` — the grant-before-use loop on one developer's machine.
- `004-developer-api` — `/api/developer/**` HTTP surface for self-driving the product without a UI. See its `quickstart.md` for the canonical curl walkthrough.
- `010-drop-redis-to-postgres` — replaced Redis with Postgres-backed distributed locks (removed Redis dependency entirely).
- `015-sessions-in-orleans` — session lifecycle management moved into Orleans grains.
- `016-cli-node-service` — always-on CLI node daemon (`korat service install/run`), multi-server routing.

### Inference

- `029-inference-point` — register headless CLI agents and BYOK endpoints as OpenAI-compatible inference points per Space.

### Security / E2E

- `023-agentclient-binding-enforcement` — enforced agent-client→grant binding at the relay layer.
- `024-orphan-server-reaper` — reaps stale server registrations and their relay state.
- `025-session-liveness` — session keepalive and server-side eviction of dead sessions.

### Marketing / growth

- `017-node-kinds-multi-agent` — first-class support for agent-kind nodes (multi-agent topologies).
- `027-account-provider-linking` — connect-additional-provider flow (link GitHub ↔ Google to same account).

## Doc conventions

- One-line "who this is for" at the top of every doc.
- Decision rationale lives in `decision-log.md`, never duplicated elsewhere. Other docs link to specific entries.
- Feature scope lives in `specs/<id>/spec.md`. Other docs link, never re-spec.
- MVP cuts (cleartext frames, single-silo routing, dev-only auth) are called out explicitly wherever they could mislead a reader into assuming production-readiness.
- Private operator inventories, incident details, and generated design handoffs
  do not belong in the public repository.
