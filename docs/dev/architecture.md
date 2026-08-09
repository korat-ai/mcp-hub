# Korat developer architecture

**Audience:** contributors changing the Cloud, CLI, console, persistence, or
relay protocol.

Read [../../ARCHITECTURE.md](../../ARCHITECTURE.md) first. It is the
source-of-truth product and system overview; this document maps that model to
code.

## Repository map

```text
apps/
  Korat.Cloud/              ASP.NET host, auth, API, Orleans silo, gRPC relay
  Korat.App/                React browser console
  Korat.Cli/                operator CLI, publisher runtime, consumer bridge
  Korat.Demo.EchoMcp/       deterministic MCP subprocess used by tests

src/
  Korat.Domain/             entities, IDs, state and presence rules
  Korat.GrainInterfaces/    Orleans contracts
  Korat.Grains/             distributed control-plane behavior
  Korat.Persistence/        EF Core/PostgreSQL metadata
  Korat.Protocol/           protobuf, frame limits, optional E2E
  Korat.Mcp/                shared MCP plumbing
  Korat.Marketing.*/        marketing-site/domain support compiled into Cloud

tests/
  Korat.Domain.Tests/
  Korat.Protocol.Tests/
  Korat.Cli.Tests/
  Korat.Persistence.Tests/
  Korat.Auth.Tests/
  Korat.Marketing.Tests/
  Korat.Cloud.ContractTests/
  Korat.Cloud.IntegrationTests/
  Korat.EndToEnd.Tests/     gated end-to-end process test
```

`Korat.slnx` contains the current build and unit/integration projects. The SPA
is an npm project; a Cloud release build invokes its Vite build and copies the
output under `apps/Korat.Cloud/wwwroot/app`.

## Entry points

- `apps/Korat.Cloud/Program.cs` configures Kestrel, auth, persistence, Orleans,
  gRPC, REST endpoints, observability, and SPA hosting.
- `apps/Korat.Cloud/Gateways/NodeGatewayService.cs` owns the bidirectional node
  stream and frame/control-message adapters.
- `apps/Korat.Cloud/Gateways/Admission/SessionAdmission.cs` is the shared
  admission policy for gRPC consumers and Space-MCP consumers.
- `apps/Korat.Cloud/Web/Endpoints.cs` contains the core owner-facing Space,
  server, access-request, permission, and activity projections.
- `apps/Korat.Cloud/Mcp/Space/` contains the aggregated Space-MCP endpoint and
  backend-session adapter.
- `apps/Korat.Cli/Program.cs` registers the CLI command tree.
- `apps/Korat.Cli/Service/NodeServiceHost.cs` runs the publisher runtime.
- `apps/Korat.Cli/Commands/ConnectCommand.cs` and bridge helpers implement MCP
  client consumption.
- `apps/Korat.App/src/routes/` contains file-based console routes.

## Domain and persistence

The relay model lives in `src/Korat.Domain/Entities/Entities.cs`:

```text
Space
  ├─ Node                 public term: runtime
  ├─ McpServer
  ├─ AgentClient          public term: consumer
  ├─ AccessRequest
  ├─ Grant                public term: permission
  └─ Session              public console section: activity
```

Strongly typed IDs prevent accidental cross-entity substitution in .NET.
PostgreSQL records use flattened IDs; REST projections may expose either an
`{ value }` ID struct or a plain string. Frontend DTOs must match the actual
projection rather than assuming one universal shape.

`IMetadataRepository` is the persistence abstraction. Orleans grains are the
normal read/write boundary for control-plane behavior:

- `SpaceGrain` scopes collection operations and approvals.
- `NodeGrain` owns connection presence and capabilities.
- `McpServerGrain` owns publication, disable/enable, assertion, HTTP config, and
  OAuth-recovery state.
- `AgentClientGrain` owns the durable consumer-to-runtime binding.
- `SessionGrain` owns lifecycle, route participants, and byte counters.

Do not add a direct repository read in a web/gateway path when the relevant
grain contract already provides the scoped operation.

## Identity and isolation

A browser owner authenticates with a Korat session cookie. CLI/runtime
processes authenticate with a revocable bearer credential and prove their node
identity during Hello.

Space isolation is enforced at every entry point:

- Owner REST endpoints resolve the authenticated user's default Space.
- Space grain keys scope collection reads and writes.
- Session admission loads a target server, then checks the consumer Space
  before disclosing `Disabled` or `NeedsReauth`.
- Cross-Space lookups use `NotFound` to avoid an existence/state oracle.

Consumer identities are bound trust-on-first-use:

- A normal gRPC consumer binds to its requesting runtime.
- A Space-MCP consumer is server-minted in the `cagg_` namespace and binds to
  `cagg-sentinel`.
- Normal consumers cannot present a `cagg_` identity, and server-minted
  consumers cannot escape that namespace.

Display names are informational. They never authorize a request.

## Presence and projections

`NodePresenceRules` is the .NET source of truth for effective runtime presence.
`apps/Korat.App/src/lib/presence.ts` and
`NodesCommand.DeriveEffectiveStatus` mirror it with server-clock correction.

An `Online` row with no heartbeat timestamp is offline. A timestamp at or past
the stale threshold is offline.

`SessionPresenceRules` handles special session participants:

- runtime-to-runtime sessions require both real runtime rows online;
- `http_cloud` sessions do not require a publisher runtime;
- Space-MCP sessions do not require a sentinel runtime row.

The `/api/sessions` endpoint projects `effectiveStatus` without mutating stored
session lifecycle state.

## Server transports

### Local stdio

The publisher runtime sends its configured MCP server set to the Cloud. A row is
available only when it is `Published`, still asserted by the latest sync, and
its publisher runtime has fresh presence.

The runtime launches the server subprocess and pumps newline-delimited MCP
JSON-RPC between stdio and relay frames.

### HTTP cloud

An `http_cloud` server has no publisher `Node`. The Cloud validates outbound URL
and auth configuration, stores secrets/tokens through the envelope-crypto path,
and proxies MCP traffic.

OAuth failures can move the server to `NeedsReauth`, which is distinct from a
generic unavailable state and blocks new sessions until the owner reconnects.

### Space-MCP

The Space-MCP endpoint presents multiple approved backend servers behind one
MCP connection. Its consumer is a durable, server-minted identity. Backend
sessions are cloud-terminated and cannot use runtime-to-runtime E2E.

## Relay routing and confidentiality

`SessionRoutingTable` owns writers for streams connected to one Cloud instance.
The session control plane supplies the authoritative peer; a sender-provided
target is never trusted.

- Local peer: direct writer fast path.
- Remote peer: Core NATS backplane.
- No NATS: local-development fallback, requiring both runtime peers on one
  Cloud instance.

Runtime-to-runtime sessions can negotiate ECDH-derived per-session E2E. The CLI
supports preference and requirement policies. HTTP and Space-MCP legs terminate
at the Cloud and are intentionally not described as end-to-end encrypted.

No relay payload is persisted. Telemetry must use explicit safe metadata and
byte counts. Be careful when changing plaintext inspection: optional E2E frames
offer only their permitted cleartext metadata to the Cloud.

## Browser console

The default console has five core sections:

```text
Overview · MCP servers · Access · Activity · Runtimes
```

`Access` groups permission grants and connected Space-MCP OAuth clients.
Synthetic consumer nodes are excluded from runtime counts and onboarding.
Server cards/counts use derived availability, not stored `Published` alone.

The optional agent-platform navigation is controlled at build time by
`VITE_ENABLE_AGENT_PLATFORM=true`. Direct routes and backend contracts remain
available for compatible deployments.

Relevant frontend files:

- `src/types/api.ts` — REST contracts.
- `src/lib/presence.ts` — presence and availability.
- `src/lib/features.ts` — optional module flags.
- `src/components/layout/AppShell.tsx` and `MobileTabBar.tsx` — navigation.
- `src/routes/index.tsx` — overview counts and onboarding.
- `src/routes/grants.tsx` / `connected-apps.tsx` — Access area.
- `src/routes/sessions.tsx` — activity.
- `src/routes/nodes*.tsx` — owner-facing runtime views.

## Local development topology

```text
localhost:5191  REST + auth + SPA
localhost:5192  gRPC runtime gateway (h2c)
localhost:5432  PostgreSQL from docker compose
```

```bash
docker compose up -d
dotnet run --project apps/Korat.Cloud

cd apps/Korat.App
npm ci
npm run dev
```

The Cloud's two local ports are deliberate: plaintext Kestrel cannot multiplex
the REST HTTP/1.1 and prior-knowledge h2c workloads on one listener in this
configuration.

## Verification

Use the narrowest test first, then the solution/CI gate:

```bash
dotnet test tests/Korat.Domain.Tests
dotnet test tests/Korat.Cli.Tests
dotnet test tests/Korat.Cloud.ContractTests
dotnet test tests/Korat.Cloud.IntegrationTests
dotnet build Korat.slnx -c Release

cd apps/Korat.App
npm run build
npm test
npm run lint
```

Persistence/auth tests require Docker. A production-shaped change should also
build the Dockerfile and consider trimmed CLI warnings.
