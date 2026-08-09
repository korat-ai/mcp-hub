# Trust and privacy

Last updated: 2026-08-02

Korat handles access to local and cloud MCP capabilities. Trust is therefore a
primary product surface: owners must see what is connected, who can use it, and
how to stop that access.

## Default permission model

Nothing can open a backend MCP session until the owner has approved the exact
consumer/server pair:

```text
Consumer A may use MCP server B inside Space S.
```

Permissions do not cross Space boundaries. Broad implicit rules such as "all
consumers can use all servers" are not part of the default model.

## Owner controls

The console exposes:

- pending access requests;
- approve and deny actions;
- active and revoked permissions;
- immediate permission revocation and active-session termination;
- enable, disable, reconnect, and delete actions for MCP servers;
- effective runtime and server availability;
- active and historical session metadata;
- connected Space-MCP OAuth clients and consent revocation.

## Payload policy

Korat must not intentionally log or persist MCP payload bodies, including:

- tool arguments and results;
- prompt or message text;
- local file contents;
- model output;
- raw MCP request/response bodies.

Allowed operational metadata is explicit and bounded:

- account and Space identifiers;
- runtime, consumer, server, permission, and session identifiers;
- transport and connection state;
- timestamps, byte counts, latency, and error classes;
- safe MCP method/tool metadata where the protocol mode makes it available.

Runtime-to-runtime sessions can negotiate optional per-session E2E encryption.
With `--e2e=require`, negotiation failure closes the session. Plaintext fallback
under `--e2e=prefer`, HTTP MCP proxying, and Space-MCP backend sessions are
cloud-readable at their terminus. The correct promise is **no payload
persistence or logging**, not "the Cloud can never technically see bytes."

## Error reporting and telemetry

Source and self-hosted builds do not send error reports unless the deployer
configures a Sentry-compatible DSN. Backend and browser reporting are separate
operator choices. Browser reporting also honors the browser's Do Not Track
signal and can be disabled with:

```js
localStorage.setItem('korat.telemetry', 'off')
```

The CLI reads `KORAT_SENTRY_DSN` at runtime and can also be published with a DSN
embedded by the official release workflow. Set `KORAT_TELEMETRY=0` to disable
CLI reporting before the SDK is initialized.

Configured reporting is limited to error diagnostics. Performance tracing and
browser replay are disabled, default PII collection is disabled, and
before-send filters redact known token, email, DSN, and workstation-path
patterns. The payload policy above still applies: MCP request and response
bodies must not be captured as telemetry. Telemetry credentials and source-map
upload credentials belong in deployment secrets, not in the repository.

## Identity and isolation

What is enforced:

- Browser owners use a protected session cookie and antiforgery token.
- CLI and runtime processes use revocable bearer credentials.
- Runtime Hello authenticates the connection and resolves its Space
  server-side.
- Normal consumer IDs bind by trust on first use to one runtime.
- Space-MCP consumers use a reserved server-minted namespace and sentinel.
- Foreign-Space entity probes return `NotFound` before entity state is
  disclosed.

What is **not** enforced, stated plainly because the heading invites the
opposite reading:

- **Agents running on the same machine are not isolated from each other.**
  Credentials live in files owned by the OS user, so any process running as
  that user can read them and act as any of that user's agents. Permissions
  are issued per agent and are revocable per agent, which is what makes
  activity readable and revocation surgical — but the separation is a
  bookkeeping boundary, not a security boundary between local processes.
- **An approval names a program on a machine, not its behavior.** Korat
  verifies what a runtime declares it will launch. Whoever can write to that
  machine can change what the launched program actually does.

Neither limitation is specific to Korat, and neither has a solution the
project can adopt today. Both are explained, with the reasoning and the
owner-side mitigations, in [security/threat-model.md](security/threat-model.md).

## Product language

Prefer concrete owner-facing language:

- "Allow this consumer to use Ableton MCP."
- "Revoke permission."
- "Published from Studio Mac."
- "Runtime last seen two minutes ago."
- "Revoking immediately closes active sessions."

Avoid language that suggests devices and consumers are always the same entity,
that access is automatic, or that every transport is end-to-end encrypted.

Technical invariants and code locations are in
[../ARCHITECTURE.md](../ARCHITECTURE.md) and
[dev/architecture.md](dev/architecture.md).
