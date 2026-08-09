# Korat Node Protocol — Wire Spec

Protocol package: `korat.relay.v1`. Transport: gRPC over HTTP/2 + TLS. The single RPC
is `NodeGatewayService.Connect` — a bidirectional stream. See `node-gateway.proto` for
message shapes; this document covers semantics the proto comments cannot express.

## 1. Connection & handshake

1. Node opens the stream and sends `NodeHello` as the first `NodeToGatewayMessage`.
2. Cloud replies with `GatewayHello` (or `AccessDenied`, then closes).
3. `NodeHello.node_kind`: `"publisher"` (hosts MCP servers) or `"agent"` (consumes one).
   Empty ⇒ publisher (back-compat). A phone is always `"publisher"`.

### Authentication — two paths

- **Primary (Bearer):** send `Authorization: Bearer <token>` as call-level gRPC metadata.
  The token comes from the device-code login flow (§5). When the cloud resolves the Bearer
  to a user, `NodeHello.node_auth_token` is ignored. **This is what the CLI and the mobile
  apps use.**
- **Fallback (HMAC):** `node_auth_token = base64(HMAC-SHA256(key=utf8(owner_token),
  msg=utf8(node_id)))`. Used when no Bearer is presented. See `CRYPTO.md`.
- On failure the cloud sends `AccessDenied("Invalid node auth token")` and closes.

## 2. Heartbeat & presence

- After the handshake the node sends `Heartbeat { node_id, sent_at_unix_ms }` periodically;
  the cloud replies `HeartbeatAck`.
- **Presence rule:** a node marked Online whose last heartbeat is older than the stale
  threshold is treated as Offline. The current threshold is **90 seconds**
  (`Korat.Domain.NodePresenceRules.StaleThreshold`). Send heartbeats well inside this
  window — 30–60s is the recommended cadence.
- A published MCP server is "online" to agents iff: it is published AND its owner node is
  connected AND the heartbeat is fresh.

## 3. Publishing MCP servers

- `PublishMcpServer` (single) → `PublishMcpServerAck { mcp_server_id }`. Idempotent by
  `(node_id, display_name)` — re-publishing returns the same id.
- `UnpublishMcpServer` takes one server offline.
- `SyncMcpServers` is the declarative full-set reconcile: send the COMPLETE set of servers
  this node hosts, ONCE per connection, after an authoritative config load. The cloud
  upserts every server in the set and soft-retires any it has for this node that is absent.
  It replies with one `PublishMcpServerAck` per server so the node can rebuild its routing
  map (display_name → mcp_server_id). Idempotent; safe to resend on reconnect.
- A phone publishes exactly one server (the device itself) via `SyncMcpServers`.

## 4. Session lifecycle

1. (Agent side, elsewhere) An agent requests access; the publisher node receives traffic
   only after a grant exists.
2. `RequestSession { request_id, agent_client_id, mcp_server_id }` →
   - `AccessPending { access_request_id }` — waiting for owner approval, or
   - `AccessDenied { reason }`, or
   - `SessionOpened { session_id, home_gateway_id, payload_limits }`.
3. Data flows as `RelayFrame`:
   - `session_id`, `source_node_id`, `target_node_id`, `sequence_number` (monotonic per
     session/direction), `direction`, `ciphertext` (opaque payload), and `mcp_server_id`
     (stamped by the cloud ONLY on frames relayed TO the publisher node, so a multi-server
     node routes to the right local server; empty toward the agent and on node-sent frames).
   - The payload bytes carry MCP JSON-RPC. The cloud never inspects them.
4. `CloseSession { session_id, reason }` from either side ends it.

## 5. Enrollment (device-code login)

Mobile apps obtain their Bearer token exactly like `korat login`:

- `POST /api/auth/cli/device-code` → `{ device_code, user_code, verification_uri,
  verification_uri_complete?, interval, expires_in }`.
- Show `user_code` + `verification_uri` to the user; they approve in a browser at
  `my.korat.dev`.
- Poll `POST /api/auth/cli/token` with `{ device_code }` until it returns the CLI token
  (or `authorization_pending` / `slow_down` / `expired_token`).
- Store the token in platform secure storage; send it as `Authorization: Bearer` on Connect.

The pending login attempt is in-memory cloud-side (not persisted) — if the cloud restarts
mid-login the app retries from `device-code`. Only the issued token is durable.

## 6. Payload limits

`SessionOpened.payload_limits` carries `PayloadLimitPolicy { per_message_limit_bytes,
session_warning_bytes, session_hard_limit_bytes }`. Exceeding a limit yields
`PayloadLimitExceeded { session_id, limit_name, limit_bytes }`. Nodes serving list-shaped
data (notifications, SMS, photos) MUST paginate/truncate to stay within the per-message
limit rather than relying on the cloud to cut them off.

## 7. Version negotiation

- `NodeHello.cli_version` — the client's bare SemVer ("" for legacy). Mobile apps report
  their own app version here.
- `GatewayHello.current_cli_version` / `min_supported_cli_version` — what the cloud
  considers latest / oldest-served. Clients below the minimum should surface an upgrade
  prompt. Same backward-compat-window policy as the CLI (see `docs/RELEASING.md`).
