# Security Policy

## Reporting a vulnerability

Please do not disclose a suspected vulnerability in a public issue, pull
request, discussion, or chat.

Use GitHub's private vulnerability reporting flow from the repository's
**Security** tab when it is available. Include:

- affected component and version or commit;
- reproduction steps or a minimal proof of concept;
- expected impact;
- whether credentials or production data may be involved;
- any suggested mitigation.

If private vulnerability reporting is unavailable, contact the maintainers
through an already established private channel and mention only that you need a
security contact. Do not send exploit details over a public channel.

Before the repository is publicly launched, maintainers must enable private
vulnerability reporting and publish a dedicated security contact. This is
tracked in [OPEN_SOURCE_READINESS.md](OPEN_SOURCE_READINESS.md).

## Scope

Read [docs/security/threat-model.md](docs/security/threat-model.md) first. It
states what Korat protects and which boundaries are deliberately not enforced,
so you can tell a defect from an accepted limitation before writing a report.

Security-sensitive areas include:

- cross-Space isolation and object-level authorization;
- CLI/runtime bearer credentials and node identity binding;
- consumer TOFU binding and the reserved `cagg_` namespace;
- access approval, permission revocation, and session termination;
- relay participant resolution and NATS routing;
- optional E2E downgrade and fail-closed behavior;
- OAuth consent, refresh-token storage, and HTTP MCP outbound requests;
- payload logging, persistence, telemetry, and error reporting;
- developer-only endpoints accidentally enabled in production.

Please avoid testing against production users or infrastructure without written
authorization. Use a local deployment and test accounts wherever possible.

## Supported versions

Until the first public release, only the current `dev` line receives security
fixes. A version support table will be added when stable releases are published.
