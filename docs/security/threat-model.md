# Threat model

Last updated: 2026-08-02

This document states what Korat protects, what it deliberately does not
protect, and why. It is the reference for judging whether a report is a bug or
an accepted boundary.

Companion documents: [SECURITY.md](../../SECURITY.md) for reporting,
[trust-and-privacy.md](../trust-and-privacy.md) for the owner-facing promise.

## What Korat is

Korat relays MCP traffic from an MCP client to an MCP server that lives in
another runtime, gated by an explicit owner approval:

```text
MCP client → consumer identity → owner-approved permission → relay → MCP server
```

The security goal is **owner control over access**: nothing reaches a backend
MCP server until the owner approves that specific consumer/server pair, and
revoking the permission stops traffic immediately.

## Trust boundaries

| Boundary | Crossed by | Enforced by |
|---|---|---|
| Space ↔ Space | any request naming a foreign entity | server-side Space resolution; foreign entities answer `NotFound` before state is disclosed |
| Consumer ↔ MCP server | opening a relay session | permission (grant) lookup at admission |
| Cloud ↔ publisher runtime | gRPC node stream | bearer credential at stream level; server-resolved Space |
| Browser owner ↔ API | console actions | session cookie plus antiforgery token |
| **OS user ↔ their own processes** | reading files in the home directory | **nothing — see "Not protected" below** |

The last row is the important one. It is a real boundary in the diagram and
Korat does not enforce it.

## In scope

Korat treats the following as defects and fixes them:

- Any path that opens a session without an active permission for that exact
  consumer/server pair.
- Any path that reads, writes, or reveals another Space's entities, including
  by distinguishing "exists but denied" from "does not exist".
- Any path that keeps traffic flowing after a permission is revoked.
- Persistence or logging of MCP payload bodies (see the payload policy in
  [trust-and-privacy.md](../trust-and-privacy.md)).
- Privilege escalation beyond what a presented credential is meant to buy —
  in particular, obtaining the effect of an owner approval without one.
- Downgrade of a session that was requested with `--e2e=require`.

## Not protected — accepted boundaries

These are decisions, not gaps. Each is stated with the reason it is not
practical to close.

### 1. Processes running as the same OS user are not isolated from each other

Credentials live in files under the user's home directory with owner-only
permissions. Any process running as that user can read them, and therefore can
act as any of that user's agents.

Consequently: **Korat issues permissions to an agent, but does not guarantee
isolation between agents on the same machine. Anyone who can read this user's
processes and storage can act as any of their agents.**

Encrypting those files does not change this. The decryption key must be
reachable by the legitimate process, so it is reachable by any process of the
same user. Projects that have addressed this in public say so directly — GNOME
Keyring states as a design goal that it will not create "the illusion that
somehow one application running in a security context can keep information from
another application running in the same security context", and Chrome's
documentation of DPAPI notes it "does not protect against malicious
applications able to execute code as the logged in user".

Approaches that do move this boundary all require something Korat does not
have: a per-application OS identity (Android's per-app UID), a code-signing
identity the OS can attest, or a hardware-backed key. Even caller attestation
does not separate agents that are subprocesses of the same terminal — there is
no impersonation to detect, because each caller genuinely is what it claims.

The MCP specification reaches the same place: it excludes stdio transports from
its authorization scheme, and answers local-server compromise with sandboxing
rather than identity.

**Owner mitigation:** treat the machine as the security boundary. Run agents
you do not trust equally as separate OS users or in separate containers.

### 2. An approved MCP server is only as trustworthy as the machine it runs on

An approval authorizes a named MCP server published from a specific runtime.
Korat verifies what the runtime declares it will launch; it cannot verify what
that program does once launched. An attacker who can write to that machine can
replace the program itself — the package, the binary on `PATH`, the working
directory — and the declared definition still matches.

Defending the publisher machine is the operator's responsibility.

### 3. A stolen CLI credential carries the full authority of its owner

The CLI bearer credential authenticates the **user**, not an individual
runtime. Within the user's own Space it can register runtimes, publish MCP
servers, and change the definition of servers it published. It cannot reach
another Space.

Rotating the credential (`korat login` again, or revoking the token in the
console) invalidates the stolen copy.

### 4. Approving a phone approves everything currently enabled on it

The Korat mobile apps join a Space as a runtime and publish themselves as a
**single** MCP server named after the device. A permission is therefore granted
for the whole phone, not for individual capabilities on it.

The per-capability control exists, but it lives on the device: each tool is
gated by both an OS permission and an in-app toggle, and every capability
classed as a sensitive read or as destructive is **off by default** until the
device owner enables it. What the console cannot show is which toggles are
currently on. An owner approving "Pixel 9" in the browser is approving whatever
that device is configured to expose — today and after any later change to those
toggles.

The exposed surface is wide by design: it includes messaging, call control,
contacts, calendar, photos, health data, location, camera, microphone, and — on
Android — screen reading and input injection through the accessibility service,
which reaches applications that expose no MCP interface of their own.

**Owner mitigation:** treat a phone permission as device-wide. Review the
in-app capability toggles on the device itself, and grant phone access only to
agents trusted at that level.

### 5. Not every transport is end-to-end encrypted

Runtime-to-runtime sessions can negotiate per-session E2E encryption. HTTP MCP
proxying and Space-MCP backend sessions terminate in the Cloud and are
cloud-readable there. The promise is **no payload persistence or logging**, not
that the Cloud can never technically observe bytes. See
[trust-and-privacy.md](../trust-and-privacy.md).

## Known gaps being closed

Distinct from the boundaries above: these are defects with an agreed fix, not
accepted behavior.

- **A permission is bound to a server's identity, not to its definition.**
  Re-publishing an existing server under the same name keeps its stable
  identifier, so an already-granted permission continues to apply to a changed
  launch definition. The fix binds each permission to a digest of the server
  definition and makes the permission inactive until the owner re-approves the
  change, with the old and new definition shown side by side.
- **Runtime identity is asserted, not proven.** Within a Space, a runtime
  identifier is a claimed string rather than a key. The cross-Space check is
  enforced; the within-Space one is not.
- **Space-MCP accepts two credential types.** The CLI-token path derives one
  consumer identity per machine rather than per agent, which collapses
  per-agent permissions to machine-wide ones. Only the OAuth path is retained;
  removal of the CLI path is sequenced so that the OAuth path works first.

## Reporting

See [SECURITY.md](../../SECURITY.md). Reports that describe one of the accepted
boundaries above are still welcome — a concrete attack that makes one of them
materially worse than stated is a defect in this document, which is itself
worth fixing.
