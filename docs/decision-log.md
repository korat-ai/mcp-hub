# Decision Log

Last updated: 2026-08-09

## 2026-08-09

### License: Apache-2.0

Decision:

Release under the Apache License 2.0. Supersedes the 2026-07-26 "License
Direction" entry, which deferred the choice.

Rationale:

The product is the hosted relay service, not exclusive possession of the
source, so neither a hosted competitor nor a proprietary derivative is worth
constraining against. What is worth having is the patent grant (§3) and the
inbound=outbound contribution term (§5), which makes a CLA unnecessary. GPL-3.0
was requested, applied, and then withdrawn by the owner.

Consequence worth recording: under a copyleft license, `protocol/` would have
needed its own more permissive license, because a third-party client linking
the reference implementation would otherwise inherit copyleft. Apache-2.0
dissolves that — no carve-out is needed and none was made.

Not decided by this: trademark (§6 grants none), and the dependency-compatibility
review, which has not been run.

Process note, recorded because it cost two wrong public commits: the owner
asked "GPL — what is the difference?"; a follow-up question about hosted
competitors was answered, and that answer was mistaken for a choice of license.
Apache-2.0 went out, was corrected to GPL-3.0 on the owner's objection, then
returned to Apache-2.0 by the owner's decision. An answer to a sub-question is
not a decision on the main one — confirm the choice in the owner's own words
before anything irreversible.

### Public repository is `korat-ai/mcp-hub`

Decision:

Publish a clean snapshot to `korat-ai/mcp-hub`. Supersedes the 2026-07-26
"Repository" entry naming `korat-ai/korat-mcp-hub` canonical — that repository
stays private and remains where development happens.

Rationale:

The 1,258-commit history contains internal runbooks and personal paths, so it
could not be published unchanged. Documentation and the console's repository
link now point at the public name; release binaries were never served from
either repository (they come from the public `korat-ai/homebrew-tap`), so the
installer is unaffected.

## 2026-07-26

### License Direction

Decision:

Prepare the repository for an OSI open-source release. Do not label or release
it as open source until the owner selects and installs an OSI-approved license.

Rationale:

Source transparency supports the product's trust model. License choice changes
real legal rights and therefore remains an explicit owner decision rather than
an implementation default.

### Repository

Decision:

Use `korat-ai/korat-mcp-hub` as the canonical repository.

Repository:

https://github.com/korat-ai/korat-mcp-hub

### Default Product Surface

Decision:

The public default is the MCP relay: Overview, MCP servers, Access, Activity,
and Runtimes. Hosted agents, inference, channels, and rooms remain optional
modules behind an explicit build-time feature flag.

Rationale:

The optional code is valuable, but it must not expand the core product model or
make publisher runtimes and MCP consumer identities look like additional
machines.

## 2026-05-25

### Product Direction

Decision:

Korat MCP Hub should be framed as "Tailscale for MCP".

Rationale:

The Tailscale mental model is familiar to technical users: devices join a private network after login, become visible to the account, and can be granted access to one another. Korat should reuse that product mechanic, but at the MCP application layer rather than as a general VPN.

### Product Category

Decision:

Korat is not an AI product.

Rationale:

Korat should not run models, host agents, interpret prompts, or provide AI logic. It should provide identity, connectivity, permissions, and trust for MCP servers and agent clients.

### Core User Story

Decision:

The primary use case is remote access from one computer's agent to another computer's local MCP server.

Example:

- home computer runs an Ableton MCP server;
- user logs the home machine into Korat;
- a second computer logs into the same Korat account;
- an agent on the second computer receives approved access to the Ableton MCP server.

### Trust Position

Decision:

Korat should not log MCP payloads.

Rationale:

Users are granting access to local capabilities. Trust requires that Korat's cloud layer is not a place where tool arguments, tool results, prompt text, or local file contents are retained.

### Future Cloud MCP Endpoint

Decision:

Korat may later expose a cloud-hosted MCP endpoint for a user's Space, but this is not part of the first version.

Rationale:

A single Korat Space MCP endpoint could let an agent client access approved local MCP servers and cloud integrations through one trusted surface. This could move Korat toward a personal MCP hub while reusing the same identity, grant, visibility, and revocation model.

The first version should stay focused on trusted remote access to local MCP servers before adding hosted connectors or an aggregated cloud MCP endpoint.

### Initial Audience

Decision:

Start with developers and power users, not enterprise buyers.

Rationale:

The first users are likely to understand MCP, tolerate CLI-first onboarding, and care deeply about transparent security and control.

## 2026-05-27

### Developer API Surface

Decision:

Introduce a dedicated `/api/developer/**` route group in the Cloud whose sole purpose is to let an implementing AI agent (or a scripted human developer) drive the product end-to-end via HTTP, without a CLI, without a browser, without real MCP server binaries.

Rationale:

The MVP scenario (`docs/mvp-scope.md`) requires an agent on machine A to call an MCP server on machine B through Korat with an approved grant. An AI agent doing this validation has no terminal multiplexer to keep `korat up` running, no Ableton/Blender process to publish, and no browser to click "Allow" on the approve page. A dev-only HTTP surface that exposes state inspection, mock entity creation, and approval shortcuts is the smallest change that unblocks live agent-driven MVP validation. The feature is spec'd in `specs/004-developer-api/spec.md`.

### Developer API Gating

Decision:

`/api/developer/**` is registered **only when `app.Environment.IsDevelopment()`**. No config-flag fallback. No separate Kestrel port.

Rationale:

Operator error (a config flag accidentally enabled in production) is a real risk class. Tying registration directly to the ASP.NET Core environment removes the toggle entirely — in any non-Development build the routes physically do not exist, returning `404 Not Found`. A separate listener port was considered for extra isolation but rejected as docker-compose plumbing overhead for marginal added safety beyond the env-gate.

### OpenAPI as Developer API Contract

Decision:

Use `Microsoft.AspNetCore.OpenApi` (built into .NET 10) to expose `GET /api/developer/openapi.json` as the machine-readable contract for the dev surface. A small hand-written `GET /api/developer` index endpoint complements it for quick human-eye discovery; a contract test asserts the two stay in sync via `EndpointDataSource` reflection.

Rationale:

A hand-rolled index alone drifts the moment a new endpoint is added without an index update. Generated OpenAPI is the industry-standard machine-discoverable contract and costs only a few `.WithSummary()` / `.Produces<T>()` annotations per endpoint. Swagger UI is deliberately deferred — the JSON spec is sufficient for the implementing agent; a UI is one line away if a human ever wants it.

### No Auth on Developer Endpoints

Decision:

`/api/developer/**` requires no authentication. The env-gate plus the default localhost-only Kestrel binding in Development is the entire defensive posture.

Rationale:

The surface does not exist in production (see "Developer API Gating"), and in development the listener is bound to localhost by default. Adding auth on top would slow the agent's self-driving workflow (every curl needs a token round-trip) while adding no real security boundary. This decision is explicitly scoped to dev endpoints — production endpoints continue to require owner auth per the 003 Web fix (W3).

### Hybrid Realism for Mock Entities

Decision:

Developer endpoints that create mock entities (nodes, MCP servers, agent-clients) call the **existing grain methods** used by production code paths (e.g. `INodeGrain.RegisterAsync`, `IMcpServerGrain.PublishAsync`). The single exception is a new `INodeGrain.MarkOnlineForTestingAsync()` method that sets `IsOnline=true` and `LastSeenAt=now` without requiring a gRPC handshake — explicitly named, XmlDoc'd as development-only, and not callable from production code paths.

Rationale:

Bypassing grains by writing directly to repositories would skip validation, audit, and the state machine — exactly the invariants the trust model depends on. Going through grains preserves every invariant for free; the only invariant the dev API legitimately needs to *bypass* is "node becomes online only through a real gRPC heartbeat stream", because a curl-driven agent has no such stream. That single bypass is therefore the single new grain method, named to make its scope unambiguous.

## 2026-05-27 (post-review pass)

### Deferred Post-Review Items (explicit acknowledgement)

After three parallel reviews (architecture / security / frontend), most CRITICAL and cheap HIGH findings were closed inline. The following items are **explicitly deferred** to follow-on sessions and tracked here so they don't get re-discovered:

**Architectural CRITICAL deferred:**
- **AgentClient↔Node binding validation** (`HandleRequestSessionAsync`). The architecture review flagged that a malicious node A can spoof `agentClientId = X` where X is registered on node B. The defensive fix (`repository.GetAgentClientAsync(id)` → verify `agentClient.NodeId == conn.NodeId`) conflicts with the current dev-API design that registers agent-clients against a placeholder `dev-agent-source` node rather than the real agent CLI's node. Closing this requires redesigning the agent-client/source-node relationship — bigger than a single fix. **Mitigation today**: dev-only demo, single-machine, no multi-tenant trust boundary. **Action**: spec'd as next-session priority.

**Architectural HIGH deferred:**
- Multi-silo `ISessionRoutingTable` abstraction (today singleton, in-process).
- `DeliveryFailure` / `CloseSession` proto evolution for hard errors instead of silent timeout.
- G11 Postgres migration generation (`tests/FOLLOWUPS.md` G11 entry).

**Security HIGH deferred (production-only):**
- mTLS or node-token auth on gRPC port 5192.
- Real production owner auth (OAuth/SSO/passkey) replacing dev shared secret.
- TLS on REST + gRPC (single port + HTTPS + HSTS).
- E2E frame encryption (constitution II — already deferred in C-Minimal cut).
- Rate limiting (`AddRateLimiter`).
- Audit log for owner mutations.
- CSRF token for cookie-only mutations (SameSite=Lax is the only defense today).
- Secrets in KeyVault instead of env vars / `config.json`.

**Frontend MEDIUM deferred (polish):**
- Explicit `:focus-visible` styling.
- Error-color + glyph (currently color-only).
- Approve.html primary/secondary button visual hierarchy.
- ISO timestamps via `Intl.DateTimeFormat`.
- `prefers-color-scheme: dark` support.

These deferrals are not regressions — they are scope cuts that were never inside MVP. The MVP-scope review verdict is APPROVED.

### Post-Review Fix Pass

Decision:

After parallel architecture/security/frontend reviews, a single consolidated opus fix-agent landed:

- `SessionRoutingTable.CloseSession()` now wired into `NodeGatewayService.Connect` `finally` block — eviction on stream teardown (CRITICAL memory-leak fix).
- `SecurityHeadersMiddleware` emitting CSP, X-Frame-Options: DENY, X-Content-Type-Options: nosniff, Referrer-Policy: no-referrer.
- DB password stripped from `apps/Korat.Cloud/appsettings.json` (only `appsettings.Development.json` keeps the dev value); non-Development startup will fail-fast if `ConnectionStrings__Korat` env var is unset.
- `LocalIdentityStore.Save` calls `File.SetUnixFileMode(path, UserRead | UserWrite)` on non-Windows.
- Frontend (`approve.html` + `index.html`): `<main id="main">` landmark, `role="alert"` + `aria-live="polite"` on error banners, double-submit disable on approve/deny/revoke, visibility-aware polling (`document.visibilityState === 'hidden'`), `aria-live="polite"` on dynamic table bodies.
- `PostReviewSecurityTests.cs` regression tests for headers + DB-password absence + memory-leak fix.

Rationale:

These were either CRITICAL within the MVP demo scope (memory leak), trivial-cost wins from cheap-fix HIGH/MEDIUM (security headers, file mode), or accessibility-AA blockers (landmark + aria-live). Skipping them would have undermined the "MVP demonstrably works on real CLI binaries" claim — once observed they were cheap enough to land before the session closed.

Tests: 169/169 pass after fix pass.

## 2026-05-27

### C-Minimal MVP Cut: Cleartext Frames for First Demo

Decision:

For sub-feature `005-mvp-relay-minimal`, RelayFrame routing between agent and publisher nodes is implemented as **cleartext byte-forwarding** through an in-process `SessionRoutingTable`. `RelayFrame.ciphertext` is treated as plaintext bytes for now. End-to-end encryption (constitution principle II — "cloud never sees payload") is **deferred** to a follow-on feature.

Additional explicit deferrals in this MVP cut:

- **Constitution II (cloud never sees payload)** — cleartext for MVP; the gateway can see frame bytes today.
- **Constitution IX (payload size limits)** — `PayloadLimitPolicy` is sent on `SessionOpened` but not enforced on inbound frames.
- **Revoke-during-active-session** — if a grant is revoked mid-stream, in-flight frames keep flowing until the streams disconnect. Defer.
- **Cross-silo routing** — the routing table is in-process. A multi-silo deployment cannot route between nodes attached to different silos until a follow-on adds the inter-silo hop.

Rationale:

The structurally-missing piece of MVP was not "encryption" — it was *any* frame routing at all. `NodeGatewayService` returned `SessionOpened` but the Frame handler was a log-and-drop. Until two nodes can actually exchange bytes through Korat, neither the trust model nor the routing architecture has been validated end-to-end. Shipping the cleartext relay first lets us prove the wire-level path, the session-to-node map, the stream-writer thread-safety contract, and the agent↔publisher acknowledgement pattern. Encryption is then a layered concern: the agent and publisher negotiate a key out-of-band and treat `RelayFrame.ciphertext` as actually-ciphertext — no further routing changes needed on the cloud side.

Mitigation:

- The decision is logged here and documented inline in `apps/Korat.Cloud/Gateways/SessionRoutingTable.cs` and the `NodeGatewayService.Connect` Frame handler so future contributors do not mistake the cleartext state for the target architecture.
- `specs/005-mvp-relay-minimal/spec.md` lists this as the only constitution-II compromise on the path to MVP.
- The follow-on encryption feature (tracked as the next slice after D smoke) wraps `ciphertext` with real key material at the node ends without touching the gateway, so the bytes-in / bytes-out behavior demonstrated by `RelayFrameForwardingTests` remains unchanged after the encryption layer lands.

### Stdio-Bridge Pumping Model (006-cli-stdio-bridge)

Decision:

The publisher-side stdio bridge spawns **one subprocess per relay session**, lazily on the
first inbound frame for an unknown `session_id`. Each subprocess gets:

- Stdin written one frame at a time (raw bytes, no MCP framing layer).
- Stdout read by a background pump that wraps each ≤4 KB chunk in an outbound
  `RelayFrame` with monotonically-increasing `sequence_number`, `direction = "server_to_client"`.
- Stderr forwarded to the host's stderr with a `[mcp]` prefix (for operator visibility).

The publisher CLI accepts `korat up --serve <name>` to bind exactly **one MCP server per `up`
invocation**. The launch command is resolved from the local config that `korat mcp add`
populates. The cloud-side `mcp-server` record is consulted only by the agent (for serverId
lookup) — the publisher trusts its own local registration.

Two cloud listening ports are required in dev:

- `5191` HTTP/1.1 — REST + browser UI.
- `5192` HTTP/2 prior-knowledge — gRPC node-gateway (`NodeGatewayService.Connect`).

Kestrel cannot multiplex HTTP/1.1 and HTTP/2 on the same plain TCP endpoint without TLS
(it logs `HTTP/2 is not enabled for ... TLS is not enabled`). Production uses one TLS endpoint
with ALPN negotiation and collapses back to a single port.

Rationale:

- **Lazy subprocess spawn** avoids forking processes for stale or never-used sessions and
  keeps `korat up` cheap when the publisher is idle.
- **One-MCP-per-`up`** sidesteps the lookup question "which MCP server is this session
  for?" without requiring a new gateway message type. A future multi-MCP publisher can
  add a `SessionOpened`-mirror message addressed to the publisher with an `mcp_server_id`
  field. Cost of the cut: a user with two registered MCP servers runs `korat up` twice.
- **4 KB chunk size** matches the typical OS pipe buffer and avoids buffering large
  stdout writes into a single frame. The cloud enforces no per-frame size limit yet
  (constitution IX deferred); chunking keeps individual frames small regardless.
- **Two dev ports** is the minimal Kestrel-compliant way to host gRPC and HTTP/1.1 on
  the same dev cloud without a TLS cert. The CLI's `LocalIdentity.CloudGrpcUrl` defaults
  to `:5192` and falls back to `CloudUrl` when unset (backwards compat).

Mitigation:

- The split-port arrangement is documented in `apps/Korat.Cloud/Program.cs` and in
  `docs/mvp-scope.md` "Running the demo" so a contributor configuring a fresh machine
  knows to set both ports.
- `tests/Korat.EndToEnd.Tests/MvpDemoEndToEndTests.cs` exercises the full path with real
  process boundaries; any regression in subprocess spawn, frame routing, or port
  config will fail this test under `KORAT_E2E_RUN=1`.
- The bridge contract is intentionally byte-pump only (no JSON-RPC framing, no MCP
  initialize handshake). When real MCP servers replace the echo demo, they will negotiate
  on top of the raw stream the same way they do over local stdio — no Korat changes needed.

## 2026-05-27 (later)

### Post-Review Fix Pass

Three reviewers (architecture / security / frontend) audited the MVP cut. This entry
records what landed in the same session and what was deliberately deferred.

#### Landed in this pass

**Architecture (CRITICAL + HIGH cluster):**

- *SessionRoutingTable eviction on teardown* (CRITICAL-1) — `NodeGatewayService.Connect`
  now enumerates `SessionRoutingTable.FindSessionsForNode(conn.NodeId)` in its `finally`
  block and removes routing entries + closes the `SessionGrain`. Closes the
  monotonically-growing-dictionary leak. Regression: `SessionRoutingTable_EvictsOnStreamTeardown`.
- *AgentClientId / NodeId trust gate* (CRITICAL-2) — before serving `RequestSession` the
  gateway resolves the `IAgentClientGrain` for the requested agent-client and rejects
  with `AccessDenied { reason = "agent_client_node_mismatch" }` when the grain's recorded
  `NodeId` differs from the stream's Hello-authenticated `NodeId`. The MVP carries one
  caveat: `AgentClientGrain` is not yet persisted, so a grain that has never seen
  `RegisterAsync` returns default state — that case falls through (treated as unknown
  rather than rejected) to keep tests and dev flows working. Once the AgentClient gains
  persistence, this can be tightened to a hard reject. Regression:
  `RequestSession_SpoofedAgentClient_IsRejected`.
- *Heartbeat ↔ stream binding* (HIGH-1) — `HandleHeartbeatAsync` now uses
  `conn.NodeId.Value` for both the grain lookup and the echoed `HeartbeatAck.NodeId`;
  a mismatched wire field is logged but ignored, not trusted.
- *CloseSession proto handling* (HIGH-2) — `NodeToGatewayMessage.PayloadOneofCase.CloseSession`
  no longer falls through silently: participant check, routing-table eviction, peer
  forward of `GatewayToNodeMessage.CloseSession`, and `ISessionGrain.CloseAsync` are all
  invoked. `SessionRoutingTable.SendToNodeAsync` is the new peer-write helper.
- *Cross-stream failure isolation* (HIGH-3) — `SessionRoutingTable.ForwardFrameAsync`
  wraps the peer-write in a try/catch; on IO failure the peer's stream entry is dropped
  and `false` is returned. The sender's stream stays alive instead of being killed by
  a peer that half-closed.
- *SessionBridge `GetOrAdd` race* (HIGH-4 / CLI) — replaced
  `ConcurrentDictionary<string, SessionContext>` with
  `ConcurrentDictionary<string, Lazy<SessionContext>>` using
  `LazyThreadSafetyMode.ExecutionAndPublication` so the subprocess factory runs at most
  once per `session_id` under concurrent inbound frames.

**Security (HIGH + cheap MEDIUM):**

- *DB password stripped from committed `appsettings.json`* (HIGH-1) — the file now
  carries a connection-string TEMPLATE without `Password=`. `Program.cs` fail-fasts on
  startup when the non-Development / non-Testing environment has no password,
  `Pwd=`, or `Integrated Security=`. Dev keeps the existing `appsettings.Development.json`
  with `Password=korat`. Operators set `ConnectionStrings__Korat` via the standard env
  override mechanism in non-Dev environments. `ProductionAbsenceWebFactory` supplies a
  dummy password via `UseSetting` to keep the production-absence probe bootable.
- *Security headers middleware* (MED-1) — new `Web/SecurityHeadersMiddleware.cs` emits
  `X-Content-Type-Options`, `X-Frame-Options`, `Referrer-Policy`, and a baseline CSP on
  every response. CSP keeps `'unsafe-inline'` for `script-src` / `style-src` because
  approve/index HTML still uses inline `<script>` / `<style>`; the follow-on is to
  extract those to external files and switch to nonce-based CSP.
- *config.json file mode* (MED-2) — `LocalIdentityStore.Save` calls
  `File.SetUnixFileMode(path, UserRead | UserWrite)` on Unix after writing. Best-effort
  (silently tolerates filesystems without chmod, e.g. SMB shares).
- *Dev owner secret production guard* (MED-3) — `SpaceOwnerAuth` constructor throws on
  startup when `IConfiguration["KORAT_DEV_OWNER_SECRET"]` is null AND the host
  environment is `Production`. Dev / Testing retain the `dev-owner-secret` fallback.

**Frontend (HIGH + cheap MEDIUM):**

- *Double-submit disable* (HIGH-1) — `approve.html#decide()` and
  `index.html#decideRequest / revokeGrant / disable-server-click` now disable buttons
  before `await fetch` and re-enable only on failure. Eliminates the
  "double-click → confusing 409 after a 200" race.
- *Landmarks + alerts* (HIGH-2) — both pages wrap content in `<main id="main">`;
  `#auth-error-banner`, `#message`, and dynamic row-error cells carry `role="alert"`
  with `aria-live="polite"`.
- *aria-live polling regions* (HIGH-3) — pending-requests and grants `<tbody>` elements
  declare `aria-live="polite" aria-atomic="false"` so screen-readers announce new rows.
- *Visibility-aware polling with backoff* (MED-1) — `index.html` replaces the naïve
  `setInterval(refreshAll, 5000)` with a `setTimeout`-chained loop that skips while the
  tab is hidden and exponentially backs off on consecutive errors up to ~60s.
- *Stop polling on terminal status* (MED-2) — `approve.html` clears its interval once
  `loadRequest` sees a non-Pending status.
- *Locale timestamp formatting* (LOW) — both pages render ISO timestamps via
  `toLocaleString()`.
- *Primary / secondary button styling on approve.html* (LOW).

#### Post-Review Backlog (deferred to follow-on)

These were flagged by the reviewers but require either a deployment-environment change
or a multi-session design effort and are deliberately not in this fix pass:

1. **mTLS on the gRPC node-gateway** (SEC-CRITICAL-prod) — requires a CA / cert
   issuance story for nodes. Tracked separately.
2. **Real production owner authentication** (SEC-HIGH-prod) — OAuth/SSO/passkey
   replacing the `KORAT_DEV_OWNER_SECRET` shared-secret. Pre-condition: owner-identity
   architecture decision.
3. **TLS on REST + gRPC collapsed onto one port** (SEC-HIGH-prod) — currently dev runs
   two ports (5191 HTTP/1.1 REST, 5192 HTTP/2 prior-knowledge for gRPC) because Kestrel
   can't multiplex on plain TCP. Production TLS endpoint uses ALPN to collapse to one.
4. **End-to-end frame encryption** (already deferred under "C-Minimal MVP Cut").
5. **Multi-silo SessionRoutingTable abstraction** — current routing is in-process.
   Cross-silo routing needs an Orleans-grain or NATS-backed peer-discovery hop.
6. **G11 — Postgres migration generation** — `tests/FOLLOWUPS.md` describes the EF
   migration required to alter Status columns to `varchar` and add the filtered-unique
   index. Must be generated before any non-InMemory deployment.
7. **Audit-log entity** — durable history of approve / deny / revoke / disable actions
   for the Space owner. Required for compliance posture but not in MVP scope.
8. **CSRF token for cookie-only mutations** — current owner-cookie flow is same-origin,
   but a stricter posture issues a per-session CSRF token consumed via a custom header.
9. **Per-route rate limiting** — currently no rate limit on `/api/access-requests/**`
   or the gRPC stream open path; a defensive limiter belongs in front of those.
10. **`app.UseExceptionHandler` with ProblemDetails** — the current pipeline catches
    `KoratDomainException` inline and writes the message string directly. A
    consolidated exception handler returning RFC 7807 ProblemDetails for every error
    class is cleaner.
11. **AgentClientGrain persistence** — see CRITICAL-2 caveat above. Tightening the
    `agent_client_node_mismatch` reject to also cover unknown agent-clients requires
    the grain state to survive deactivation, which in turn requires either
    `IPersistentState` or a repository-backed `Hydrate` pattern.
12. **Externalize inline JS / CSS + nonce-based CSP** — see SEC-MED-1 above.

---

## gRPC Node Auth (HMAC-derived per-node token) — W8

**Date:** 2026-05-27
**Status:** ACCEPTED — implemented.
**Threat:** before this change the `Hello` handshake on the gRPC port (`:5192`) was
unauthenticated. The only mitigation was binding the listener to `IPAddress.Loopback`
in dev. Once Fly.io exposes the gRPC port publicly, anyone who can guess a `NodeId`
could open a stream and impersonate that node — sending Heartbeat / PublishMcpServer /
RequestSession on its behalf.

**Decision:** require a per-node auth token on every `Hello`:

```
NodeAuthToken = base64( HMAC-SHA256(OwnerToken, NodeId) )
```

- Cloud verifies in `NodeGatewayService.HandleHelloAsync` using `SpaceOwnerAuth.OwnerSecret`
  (the same `KORAT_DEV_OWNER_SECRET` already used for REST owner auth). Mismatch →
  `AccessDenied("Invalid node auth token")` and the stream terminates before any grain
  is touched.
- CLI computes in `NodeGatewayConnection.ConnectAsync` and `McpAddCommand` using
  `LocalIdentityStore`'s `OwnerToken` (cached locally).
- Single source of truth: `Korat.Domain.Auth.NodeAuthTokens.{Compute, Verify}`.
- `NodeAuthTokens.Verify` uses `CryptographicOperations.FixedTimeEquals` for constant-time
  comparison.

**Why HMAC and not per-node token storage:**

- **No new persistence.** Both sides already share `OwnerToken`; recomputing the token
  from `(OwnerToken, NodeId)` removes the need for a new table, registration RPC, and
  rotation flow.
- **Closes the actual gap.** A stranger from the internet who doesn't know `OwnerToken`
  cannot forge any `NodeAuthToken` for any `NodeId`.
- **Matches single-owner-per-Space semantics.** Anyone who holds `OwnerToken` can already
  mutate `/api/space` arbitrarily; letting them also claim any `NodeId` in their own
  Space is no incremental compromise.

**Out of scope (carried forward as backlog items):**

- **Multi-owner per Space.** Would require per-node tokens with separate storage so one
  owner cannot impersonate another owner's nodes inside the same Space.
- **Token rotation.** With HMAC derivation, rotating `OwnerToken` invalidates every
  per-node token simultaneously — fine for MVP, painful for production multi-tenant.
- **mTLS.** Still the long-term answer; the HMAC token is the MVP bridge.
