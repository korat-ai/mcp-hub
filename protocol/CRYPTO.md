# Korat Node Protocol — Cryptography

Two independent mechanisms, both live as of 031-relay-confidentiality.

## 1. Node authentication (LIVE)

Proves a connecting node belongs to the space without per-node secrets.

```
node_auth_token = base64( HMAC-SHA256( key = utf8(owner_token), msg = utf8(node_id) ) )
```

- Both sides recompute independently from the shared `owner_token`; they match iff both
  know the same owner token. A stranger who does not know `owner_token` cannot forge a
  token for ANY `node_id` in the space.
- Verification is constant-time; empty/null tokens always fail.
- Reference: `Korat.Domain.Auth.NodeAuthTokens` (C#). Conformance vectors:
  `test-vectors/node-auth.json`.
- **Note:** when a node authenticates via `Authorization: Bearer` (the device-code token),
  this HMAC is not required — the cloud skips it once the Bearer resolves. The HMAC path is
  the no-Bearer fallback. Mobile apps use Bearer.

Threat model: defends against internet-stranger node impersonation. A holder of the owner
token can claim any node id in their own space — acceptable under single-owner-per-space.

---

## 2. Relay E2E encryption (031 — LIVE)

> **031-relay-confidentiality supersedes the earlier random-nonce sketch in this section.**
> The `RelayCrypto` class (random nonce, no AAD, symmetric key) was a placeholder; it is
> preserved for legacy compatibility but is NOT used in the E2E path. The normative
> implementation is `E2eHandshake` + `E2eSessionCipher`.

### 2.1 Overview

Per-session ECDH key exchange (P-256 ephemeral) followed by AES-256-GCM payload
encryption. The cloud relays the handshake messages and the encrypted frames but never
sees plaintext content or the derived session key.

Capability negotiation is backward-compatible: if either peer lacks `"e2e-v1"` in its
`NodeHello.capabilities`, the session falls back to the unencrypted path. Fallback is
always surfaced with a loud warning; it is NEVER silent.

### 2.2 Key exchange

```
Agent                           Cloud (relay)                    Publisher
  │                                 │                                │
  │── E2eKeyOffer ─────────────────►│                                │
  │   {session_id, version=1,       │─── E2eKeyOffer ───────────────►│
  │    curve="p256",                │    (stamped with mcp_server_id)│
  │    pub_key=agentSpki,           │                                │
  │    salt=16-byte-random}         │                                │
  │                                 │◄── E2eKeyAnswer ───────────────│
  │◄── E2eKeyAnswer ────────────────│    {pub_key=publisherSpki,     │
  │    {pub_key=publisherSpki,      │     confirm_tag}               │
  │     confirm_tag}                │                                │
  │── E2eKeyConfirm ───────────────►│                                │
  │   {confirm_tag}                 │─── E2eKeyConfirm ─────────────►│
```

If the publisher node does not support `e2e-v1`, the cloud sends `E2eNotSupported` to
the agent instead of forwarding the offer.

### 2.3 Key derivation

```
transcript_hash = SHA256(
    utf8(session_id) || 0x00 ||
    utf8(agent_client_id) || 0x00 ||
    utf8(publisher_node_id) || 0x00 ||
    salt(16) ||
    agent_spki ||
    publisher_spki
)

ikm    = ECDH shared secret (P-256 raw X-coordinate, big-endian)
hkdf   = HKDF-SHA256(ikm, salt=offer.salt, info=utf8("korat-relay-e2e-v1") || transcript_hash)
okm    = 96 bytes: K_payload(32) | K_conf(32) | reserved(32)
```

- `K_payload` — 32-byte AES-256-GCM key for payload encryption.
- `K_conf` — 32-byte key for confirm HMAC tags.
- `reserved` — zeroed immediately; not returned to callers.

Reference: `Korat.Protocol.E2eHandshake` (C#). Conformance: `test-vectors/e2e-handshake.json`.

### 2.4 Confirm tags

```
publisher_confirm = HMAC-SHA256(K_conf, utf8("publisher-confirm") || transcript_hash)
agent_confirm     = HMAC-SHA256(K_conf, utf8("agent-confirm")     || transcript_hash)
```

The confirm tags are sent in `E2eKeyAnswer` (publisher → agent) and `E2eKeyConfirm`
(agent → publisher) respectively. They prove both parties derived the same `K_conf`
from the same ECDH shared secret and transcript.

Verification is constant-time (`CryptographicOperations.FixedTimeEquals`).

### 2.5 Frame encryption (E2eSessionCipher)

```
nonce  = dir(1B) || 0x00 0x00 0x00(3B) || seq(8B big-endian)  → 12 bytes
AAD    = "korat-frame-v1"(14B) || sessionId_utf8 || 0x00 || dir(1B) || seq(8BE) || SHA256(meta_bytes)
wire   = tag(16B) || ciphertext                                 (nonce NOT transmitted)
```

- **dir**: `0x00` = agent→publisher (`client_to_server`); `0x01` = publisher→agent.
- **seq**: per-direction monotonic counter starting at 0; strict enforcement on receive
  (replay/reorder → `CryptographicException`; session terminated).
- **meta_bytes**: serialized `FrameMetadata` proto bytes. Empty → `SHA256(empty)`.
- **tag**: 16-byte GCM auth tag prepended to ciphertext.
- Decryption MUST reject frames with `wire.Length < 16`.
- `K_payload` is zeroed on `E2eSessionCipher.Dispose()`.

Reference: `Korat.Protocol.E2eSessionCipher` (C#).
Conformance vectors: `test-vectors/e2e-cipher.json` (includes tampered-tag, tampered-AAD,
wrong-direction, replayed-seq negative cases).

### 2.6 Cleartext metadata header (FrameMetadata)

Each E2E-encrypted frame carries a **cleartext** `FrameMetadata` proto message
(field 9 of `RelayFrame`) stamped by the sender:

| Field | Type | Description |
|---|---|---|
| `tool_name` | string | MCP tool name from `tools/call`; empty for responses/chunks |
| `kind` | string | `"request"` \| `"response"` \| `"notification"` \| `"chunk"` |
| `category` | string | `"tool_call"` \| `"tool_result"` \| `"lifecycle"` \| `"other"` |
| `payload_bytes` | uint64 | Byte count of the plaintext payload (ct.Length − 16) |

The cloud reads ONLY this header for inspection/policy (see `McpToolCallInspector.ObserveMetadata`).
It is **AAD-bound**: `SHA256(meta_bytes)` is included in the frame's AAD, so a cloud
that alters any metadata field causes AEAD failure at the peer.

Reference: `Korat.Protocol.FrameMetadataFactory` (C#).

### 2.7 Trust model

#### Passive cloud (DB dump / log scrape / full-transcript sniff)

The cloud observes: `session_id`, `agent_client_id`, `publisher_node_id`, `offer.pub_key`,
`answer.pub_key`, `offer.salt`, confirm tags, all frame ciphertexts + metadata headers.

With only public keys and the transcript, a passive observer **cannot** compute the ECDH
shared secret. Therefore it cannot derive `K_payload` or decrypt/forge any frame.
This is the primary threat this design defeats.

#### Active cloud (key-swap MITM) — DOCUMENTED RESIDUAL

An **active** cloud can substitute `pub_key` in both the offer and answer, producing a
forged transcript with consistent confirm tags (because the cloud controls the relay and
can generate a matching handshake on both sides).

**This is NOT claimed to be prevented.** The confirm tags protect against a passive
transcript-replay but do not stop a key-swapping relay.

**Upgrade path (later leg):** the publisher holds a long-term identity keypair; it signs
`answer.pub_key || transcript_hash` with its private key; the agent TOFU-pins the
fingerprint on first connect (or uses an owner-pinned key stored in the console). This
signature rides in an additive `E2eKeyAnswer` field and is ignored by peers that do not
understand it (backward-compatible). Implementation is a separate feature leg.

#### Downgrade attack

A cloud that sends `E2eNotSupported` to an honest agent is **visible**: the agent emits a
loud warning to stderr. Under `--e2e=require` the agent closes the session instead of
continuing in plaintext. Silent downgrade is not possible against a conformant agent.

### 2.8 Backward compatibility

- Old CLIs (no `"e2e-v1"` capability): session proceeds as plaintext (`RelayFrame.enc=0`,
  no `meta` field). The cloud's inspector falls back to legacy payload parsing. Zero
  behavior change for old peers.
- Old cloud (no `E2eKeyOffer` handler): unknown oneof cases are ignored (proto3 default
  switch falls through) → agent times out waiting for answer → plaintext fallback +
  downgrade warning.
- Legacy `RelayFrame` fields 1–7 are byte-identical; `enc` and `meta` are absent when
  their values are zero/null (proto3 default = no bytes on wire).

---

## 3. Removed: legacy random-nonce cipher

An earlier `RelayCrypto` placeholder (AES-256-GCM, random nonce per frame,
wire `nonce(12) || tag(16) || ciphertext`) was never wired into the live
gateway and has been removed along with its `test-vectors/relay-crypto.json`
conformance fixture. §2 is the only relay payload cipher.
