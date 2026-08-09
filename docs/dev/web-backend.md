# Korat Cloud backend

This guide is the contributor map for `apps/Korat.Cloud`. Start with
[architecture.md](architecture.md) for the cross-component model and use the
code as the source of truth for endpoint and option details.

## Responsibilities

One ASP.NET Core process hosts:

- the owner console and REST control plane;
- the gRPC runtime gateway;
- the Orleans silo and grains;
- PostgreSQL-backed metadata;
- optional NATS cross-instance relay forwarding;
- browser, CLI device-flow, and Space-MCP OAuth surfaces;
- optional Sentry-compatible error reporting.

Korat Cloud controls identity, permission, lifecycle, and routing metadata. It
must not intentionally log or persist MCP payload bodies.

## Process and ports

Development defaults:

| Listener | Default | Protocol | Purpose |
|---|---:|---|---|
| REST/UI | `localhost:5191` | HTTP/1.1 | SPA, REST, browser auth, Space-MCP OAuth |
| runtime gateway | `localhost:5192` | h2c gRPC | publisher and consumer streams |

`ASPNETCORE_URLS` changes the REST listener. `KORAT_GRPC_PORT` changes the gRPC
port. `KORAT_BIND_ALL_INTERFACES=1` permits non-loopback gRPC binding.

The reference Fly profile terminates REST TLS at Fly and passes raw TCP `:8443`
to Caddy for gRPC TLS and h2c proxying. See
../deployment-fly.md.

## Composition root

`apps/Korat.Cloud/Program.cs` is the composition root. It configures:

- Kestrel listeners and forwarded headers;
- authentication, authorization, antiforgery, and rate limiting;
- EF Core and OpenIddict stores;
- Orleans clustering, grain storage adapters, and filters;
- relay routing/backplane services;
- HTTP MCP proxying and OAuth;
- hosted cleanup and audit services;
- endpoint groups and the SPA fallback.

Keep feature behavior out of `Program.cs` where a focused service or domain rule
can own it.

## Public model and compatibility model

Owner-facing language maps onto stable domain and wire names:

| Public term | Domain/API term |
|---|---|
| Runtime | `Node` |
| Consumer | `AgentClient` |
| Permission | `Grant` |
| Activity | `Session` |

Do not rename wire fields or persisted entities merely to update UI copy.
`kind=agent` Node rows are synthetic consumer identities; normal owner runtime
views hide them.

## Core request paths

### Publisher runtime

1. A CLI bearer authenticates the gRPC stream.
2. `NodeHello` binds the runtime to the caller's Space and records display and
   host metadata.
3. Heartbeats maintain effective presence.
4. MCP declarations are reconciled into the Space catalog.
5. Frames are accepted only for an admitted, routed session.

### Consumer session

1. A consumer stream authenticates and sends its stable `--agent` identity.
2. `SessionAdmission` loads the target server.
3. Foreign-Space targets return `NotFound` before server state is disclosed.
4. The consumer identity is checked or trust-on-first-use bound.
5. An active permission is required; otherwise an idempotent access request is
   created.
6. A successful admission records the authoritative participants and opens the
   relay route.

The caller-provided frame destination is never trusted as the routing
authority.

### Space-MCP

OpenIddict serves the active authorization-code and refresh-token flow for
Space-MCP clients. The aggregator uses a server-minted `cagg_` consumer identity
and the `cagg-sentinel` Node ID. The sentinel is a routing marker, not a runtime
row.

Revoking a connected application's consent invalidates its reference tokens and
closes affected backend sessions.

### HTTP MCP

An `http_cloud` server is terminated by Cloud and therefore has no publisher
runtime. URL validation, redirect validation, secret storage, and OAuth refresh
remain server-side concerns. A missing publisher Node must not make this session
shape stale.

## Presence and availability

`NodePresenceRules` is the canonical backend rule:

- stored `Offline` is offline;
- stored `Online` without `LastSeenAt` fails closed;
- a heartbeat at or beyond the stale threshold is offline.

`SessionPresenceRules` distinguishes the three session shapes:

- runtime-to-runtime requires both real participants online;
- HTTP MCP does not require a publisher runtime;
- Space-MCP does not require a consumer sentinel row.

The REST projection may expose an `effectiveStatus` without rewriting the stored
session lifecycle state.

## REST surfaces

The main owner surfaces are grouped under:

- `/api/space` — overview, runtimes, servers, pending access;
- `/api/access-requests` — detail, approve, deny;
- `/api/grants` — permissions and revocation;
- `/api/sessions` — activity metadata;
- `/api/oauth/consents` — connected Space-MCP applications;
- `/api/auth/*` and `/api/cli/*` — browser and CLI identity;
- `/api/mcp-servers/*` — local and HTTP MCP management.

Inference, hosted agents, channels, rooms, and AG-UI are an optional product
module. Their backend implementation remains present but is not part of the
default relay console.

## Storage and clustering

`src/Korat.Persistence` owns EF mappings and migrations. Grains in
`src/Korat.Grains` are the control-plane state owners and persist through
`IMetadataRepository`.

Off Fly, Orleans uses localhost clustering. On Fly, `FLY_PRIVATE_IP` enables
PostgreSQL ADO.NET clustering and shared Data Protection keys. Multi-instance
relay forwarding uses NATS when `NATS_URL` is configured.

Migration execution is explicit on Fly:

```bash
KORAT_RUN_MIGRATIONS=1
```

Only a serialized release step or designated instance should apply schema
changes.

## Production startup gates

Non-Development startup fails closed without:

- a password-bearing PostgreSQL connection;
- GitHub and Google OAuth credentials;
- a Resend API key and sender address;
- a persistent OpenIddict PKCS#12 signing/encryption certificate;
- `Korat:Cli:PublicOrigin`.

Optional push and error-reporting integrations degrade to no-op when their
configuration is absent.

## Testing

Fast domain rules:

```bash
dotnet test tests/Korat.Domain.Tests
```

Backend behavior:

```bash
dotnet test tests/Korat.Auth.Tests
dotnet test tests/Korat.Cloud.ContractTests
dotnet test tests/Korat.Cloud.IntegrationTests
```

The latter suites use Testcontainers or the full in-process Cloud host and may
require Docker plus restored NuGet packages.

Before changing admission, identity, presence, or routing, read
[../../ARCHITECTURE.md](../../ARCHITECTURE.md) and
[../trust-and-privacy.md](../trust-and-privacy.md).
