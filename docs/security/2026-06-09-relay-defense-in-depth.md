# Relay Defense-in-Depth — Roadmap Spec — 2026-06-09

**Scope:** two defense-in-depth items on the cloud relay data plane (labelled **N-1**) that are too large and too prod-sensitive for a blind patch. This doc states the problem, current state (with `file:line`), threat model, proposed design, phasing, rollout risk, and a recommendation for each — and ends with a recommended order plus what needs owner sign-off.

**Context:** the relay is a Core-NATS backplane (`009-nats-relay-backplane`). Orleans is control-plane only; the data plane (frames) flows through NATS subjects. These two items are *backstops* — today's isolation holds, but it rests on a single mechanism each (subject scoping for tenancy; nothing for on-wire confidentiality).

Cross-reference: the device-side counterpart to the authz story is tracked in the mobile-team work order, GitHub issue **#52** (Item 1). Together they form "authorize agent→capability at the gateway **and** on the device."

---

## N-1a — The NATS broker enforces zero authorization

### Problem statement
The NATS broker that carries every relay frame runs with **no accounts, no users, and no per-subject permissions**. Tenant isolation currently depends entirely on the *cloud silos* publishing/subscribing only their own fully-qualified subjects — the broker itself would happily accept a `SUB korat.relay.>` from any principal that can reach it.

### Current state (file:line)
- `deploy/korat-nats/Dockerfile` — `FROM nats:2.10`, started with `CMD ["--addr", "::", "--port", "4222", "--http_port", "8222"]`. **No `--config` / `nats.conf`**, so no accounts/users/permissions. The directory contains only `Dockerfile` and `fly.toml` — there is no `nats.conf` in the repo.
- `deploy/korat-nats/fly.toml` — `app = "korat-nats"`, no `[http_service]` / `[[services]]` → **no public ports**; reachable only on Fly's private 6PN (`nats://korat-nats.internal:4222`). This is the only thing standing between the open broker and the world.
- `apps/Korat.Cloud/Gateways/NatsSubjects.cs` — subjects are fully-qualified and id-encoded: `korat.relay.frame.<encode(nodeId)>` (`FramePrefix`), `korat.relay.conn.<encode(connectionId)>` (`ConnPrefix`), `korat.relay.tap.<encode(sessionId)>` (`TapPrefix`). Ids are base64url-encoded so a node-supplied id cannot inject `.`/`*`/`>` into a subject.
- `apps/Korat.Cloud/Gateways/NatsRelayBackplane.cs` — the only subscriber today (`NatsRelayBackplane`); it subscribes to specific `korat.relay.frame.<node>` / `korat.relay.conn.<conn>` subjects, **never a wildcard**.
- `apps/Korat.Cloud/NatsUrl.cs` — maps `NATS_URL` into `NatsOpts`. Today it sets only `Url`/`Name`/`TlsOpts`/retry; **no credentials** are attached.

### Threat model
- **Today:** the only subscriber is `NatsRelayBackplane` using fully-qualified subjects; there are **zero wildcard subscriptions**, so cross-tenant frame interception does not happen in the running system. Isolation holds *operationally*.
- **The gap:** the broker *enforces* nothing. Any process that lands on the 6PN — a future co-located app, a misconfigured sidecar, or a **single compromised silo** — could `SUB korat.relay.>` and observe (or `PUB` into) every tenant's frames. The 6PN is the only boundary, and it is a network boundary, not an authorization one. There is no cryptographic or principal-level backstop (see N-1b for the crypto angle).
- **Imminent trigger:** the deferred `korat.relay.tap.<SessionId>` observer feature (subject already reserved as `TapPrefix` in `NatsSubjects.cs`) is *exactly* the shape of a second, sanctioned subscriber. Shipping a tap consumer before the broker can scope it would institutionalize a broad subscriber with no per-session authorization.

### Proposed design
Introduce **NATS accounts/users with per-subject publish/subscribe permission allowlists**, scoping the cloud silos as the only principals:
- A `nats.conf` defining an account (or a small set) for the cloud silos, with explicit `permissions { publish { allow: [...] }, subscribe { allow: [...] } }` allowlists keyed on the `korat.relay.>` subject-prefix family.
- The silo principal is permitted to publish/subscribe `korat.relay.frame.*`, `korat.relay.conn.*` (and, when the tap ships, `korat.relay.tap.*`) — and **nothing else**. No principal gets `>` at the root.
- When the **tap/observer** feature ships, give the observer its **own** user, scoped to **only** `korat.relay.tap.*` (subscribe-only) and **per-SessionId** (the observer must authenticate and only receive the sessions it is authorized to tap — the tap is a sanctioned second subscriber and must not become a `korat.relay.>` firehose).
- Wire it up:
  - `deploy/korat-nats/Dockerfile` mounts/loads the `nats.conf` (`--config`).
  - A **Fly secret** carries the NATS credentials (creds file or user/pass), set on `korat-nats` and on the cloud apps.
  - `apps/Korat.Cloud/NatsUrl.cs` extended to attach `AuthOpts` / a creds file to `NatsOpts` (today it only sets URL/TLS/retry) — and the connection string / secret plumbed through `Program.cs`.

### Phasing
1. **Author `nats.conf`** with accounts + per-subject allowlists; review subject inventory against `NatsSubjects.cs` so the allowlist exactly matches the prefixes in use (`frame`, `conn`, and reserved `tap`).
2. **Provision creds** as Fly secrets (broker side + each cloud app); extend `NatsUrl.cs`/`Program.cs` to consume them.
3. **Deploy to dev**, verify the relay still connects and frames flow end-to-end (this is the risky moment — a creds/permission mismatch breaks the relay silently or loudly).
4. **Deploy to prod** behind the standard prod gate.
5. **Then** build the tap observer with its own per-SessionId-scoped, subscribe-only user.

### Rollout risk
- **Prod-connectivity risk is real:** a creds or permission-allowlist misconfiguration breaks the relay — either the silo can't connect at all, or it connects but is denied on the subjects it needs, so frames silently fail to route. This is the live data plane for every active session.
- Requires a **new Fly secret** (NATS creds) and a **connection-string/`NatsOpts` change** in `NatsUrl.cs` — both must land atomically with the broker config or the relay drops.
- Recommend a dev soak with a forced reconnect (rolling restart) to confirm `RetryOnInitialConnect` + creds path behave under the in-flight-frame loss already documented for deploys.

### Recommendation
**Do this BEFORE the deferred `korat.relay.tap.<SessionId>` observer ships.** The tap is the first sanctioned second subscriber and is the exact shape (a broad-ish relay consumer) that the broker currently cannot constrain; landing subject-scoped accounts first means the tap can be authored with a correctly narrow, per-SessionId, observer-authenticated grant from day one instead of retrofitting authz onto a live firehose.

---

## N-1b — Relay frames are plaintext on the wire (subject scoping is the only separation)

### Problem statement
Relay frames are carried **in cleartext**. The protobuf field is named `Ciphertext`, but the cloud reads it as plaintext bytes. So NATS subject scoping (N-1a) is doing **100% of the on-wire tenant separation**, with no cryptographic backstop: anyone who defeats subject scoping (or who is the broker / a 6PN peer) sees frame contents in the clear.

### Current state (file:line)
- `apps/Korat.Cloud/Gateways/NodeGatewayService.cs:191-193` — comment on the frame-forward path: *"Cleartext frames only — ciphertext is interpreted as plaintext bytes for MVP (constitution II amendment documented in docs/decision-log.md)."*
- `apps/Korat.Cloud/Gateways/SessionRoutingTable.cs:414` — `_inspector.Observe(sessionId, r.McpServerId, r.SpaceId, frame.Direction, frame.Ciphertext.Span)` — the routing table passes `frame.Ciphertext.Span` straight into the inspector **as plaintext**.
- `apps/Korat.Cloud/Observability/McpToolCallInspector.cs:27` — `Observe(... ReadOnlySpan<byte> payload)` reassembles the bytes as newline-delimited JSON-RPC and parses `tools/call` lines. Its own doc comment (`McpToolCallInspector.cs:15-18`) states the **"Plaintext caveat"**: this works *only* while frames are plaintext; under future E2E the payload is opaque and "the key-holder decrypt path must run first — out of scope for 009."
- The `Ciphertext` field name across the proto/`RelayFrame` is the deliberate **forward-design seam**: the wire field is already named for the eventual encrypted payload; today it just carries plaintext.

### Threat model
- With N-1a unfixed, a 6PN peer / compromised silo can both *route to* and *read* every frame — and the contents are plaintext, so interception is full disclosure (MCP tool calls, arguments, and results, which for a phone node include the personal data from `mobile/` Item 1).
- Even *with* N-1a fixed, the broker operator and the cloud silos themselves still see plaintext. There is no end-to-end confidentiality between the two session endpoints (agent client ↔ node); the cloud is fully in the trust path for frame contents.
- Subject scoping is a **single** line of defense for confidentiality. N-1b adds a cryptographic second line that does not depend on the broker or the 6PN.

### Proposed design
Introduce **real end-to-end frame encryption**, using the existing `Ciphertext` field as the seam:
- **Key establishment options (to be chosen):**
  - **Per-session symmetric key established at session open** — the two session endpoints derive/exchange a symmetric key when the session is created (e.g. ECDH between agent-client and node identities, or a key sealed to each endpoint at session-grant time), and all frames in that session are AEAD-encrypted under it.
  - Decide **who holds keys:** the two endpoints only (true E2E, cloud blind), vs. an escrow/cloud-assisted model (cloud can decrypt). The design strongly prefers endpoints-only.
- **Cloud inspector / tap interaction:** if the cloud cannot decrypt (the preferred E2E posture), then `McpToolCallInspector.Observe` (and the `korat.relay.tap.*` observer) **cannot read frame contents** — they see only ciphertext, lengths, direction, and session/server ids. This is the central trade-off: the inspector's `tools/call` extraction (`McpToolCallInspector.cs`) becomes impossible without a key-holder decrypt path, which by definition reintroduces the cloud into the trust path. Options:
  - Drop content inspection under E2E (keep only metadata-level observability: size, direction, timing).
  - Move inspection **to an endpoint** (the node or client emits a signed, redacted audit event), not the cloud.
  - A cloud-assisted decrypt path for inspection only (weakens E2E; document explicitly if chosen).

### Phasing
1. **Decide the trust model** (endpoints-only vs. cloud-assisted) — this gates everything else and needs owner sign-off (see below). It directly determines whether `McpToolCallInspector` survives.
2. **Key-establishment design** at session open (which identities, which exchange, rotation, replay protection).
3. **Implement AEAD** on the `Ciphertext` field at both endpoints (node SDKs in `mobile/` + CLI node + agent client); cloud forwards opaque bytes (it already treats the field as opaque bytes — the routing/forward path in `NodeGatewayService` does not need the plaintext).
4. **Resolve observability:** replace or relocate `McpToolCallInspector` per the chosen trust model; downgrade to metadata-only if endpoints-only.
5. **Staged rollout** with a negotiated capability flag so old (plaintext) and new (encrypted) nodes interoperate during the window.

### Rollout risk
- This touches **every** node implementation (mobile SDKs, CLI node, agent client) and the cloud observability stack — it is a protocol-level change, not a localized patch.
- **Big trade-off to flag explicitly:** E2E frame crypto **vs.** cloud-side tool inspection / observability. The current `McpToolCallInspector` `tools/call` analytics and any future `korat.relay.tap.*` observer depend on plaintext. True E2E blinds them. The org must decide whether per-session tool-call observability is worth keeping the cloud in the trust path.
- Key management (storage, rotation, loss/recovery, per-session derivation) is the hard part and the main source of subtle vulnerabilities.

### Recommendation
**Sequence N-1b after N-1a.** N-1a (subject-scoped accounts) is a contained, deployable hardening that immediately reduces the broker/6PN exposure; N-1b is a protocol-level program gated on a trust-model decision and a rewrite of how the cloud observes traffic. Lead with the cheap, high-leverage broker fix; treat E2E frame crypto as a deliberate, signed-off roadmap item — its primary cost is **losing cloud-side tool inspection**, which is a product/trust decision, not just an engineering one.

---

## Recommended order + what needs owner sign-off

**Order:**
1. **N-1a — NATS subject-scoped accounts/users.** Do this first and **before** the `korat.relay.tap.<SessionId>` observer ships. Contained, deployable, high leverage.
2. **N-1a follow-on — the tap observer** (when built) must use its own per-SessionId-scoped, subscribe-only, authenticated user.
3. **N-1b — E2E frame encryption.** Protocol-level program; sequence after N-1a; gated on the trust-model decision.

**Needs owner sign-off:**
- **Prod relay connectivity (N-1a).** Adding NATS creds + per-subject permission allowlists can break the live relay if mis-scoped. Sign-off required on: the Fly-secret rollout plan, the dev soak / forced-reconnect verification, and the prod deploy gate. A `NatsUrl.cs` / connection-string change ships atomically with the broker `nats.conf`.
- **Key-management & trust model (N-1b).** Owner must decide **endpoints-only E2E (cloud blind)** vs. **cloud-assisted decrypt**, because that decision determines whether `McpToolCallInspector` and the `korat.relay.tap.*` observer keep working. This is the explicit **E2E-crypto vs. cloud-side observability** trade-off — a product/trust call, not purely engineering. Key storage, rotation, and recovery design also need sign-off before implementation.
