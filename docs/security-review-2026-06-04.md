# Security Review — 2026-06-04 (`dev` branch)

**Reviewer:** automated `/security-review` (identify → false-positive filter → ≥8/10 confidence bar).
**Branch:** `dev` (then ~231 commits ahead of `master`), HEAD `ba1a414` (feature 025).
**Build label:** `8bfb08e` (per console footer at review time).

## Scope
Security-focused pass over the branch, concentrating on security-critical surfaces (not docs/design/tests):

- **Relay / gateway trust boundary** (node↔cloud): `apps/Korat.Cloud/Gateways/**`
  (`NodeGatewayService`, `SessionRoutingTable`, `GrpcAuthHelper`, `NatsRelayBackplane`,
  `NatsSubjects`, `ISessionRouteResolver`), `src/Korat.Protocol/RelayCrypto.cs`, `node-gateway.proto`.
- **Auth & session**: `apps/Korat.Cloud/Web/Auth/**` (CanonicalSigninHandler, MagicLinkService,
  PendingLinkService, InviteService, EmailChangeService, CliTokenService, SessionService,
  OAuthStateProtector, IsSafeReturnUrl, SecFetchSiteValidator, RequireAntiforgeryExtensions,
  PolymorphicAuthResolver, GitHubOAuthExtensions, UserProvisioningService), `RequireAuthExtensions`,
  `SpaceResolver`.
- **Web endpoints / authz (cross-tenant / IDOR)**: `Endpoints.cs`, `Web/Developer/**` (prod-gating),
  `MetaEndpoints.cs`, `SecurityHeadersMiddleware.cs`.
- **Crypto / tokens**: `src/Korat.Domain/Auth/**`, `OpenIddictSigningKey.cs`, `NodeAuthTokens` (HMAC).
- **CLI process spawning** (RCE / path-traversal): `apps/Korat.Cli/Mcp/**`, `Service/**`
  (launchd/systemd unit generation, ConfigWatcher), `Util/{ShellHelper,BrowserLauncher}.cs`,
  `Commands/UpgradeCommand.cs`.
- **Persistence**: `EfMetadataRepository.cs` (raw SQL), `OrleansAdoNetSchema.cs`.

## Result: no HIGH or MEDIUM vulnerabilities

No finding met the reporting bar (concrete, >80%-exploitable, introduced on this branch). Every
candidate examined was already correctly defended.

### Verified clean

| Surface | Verification |
|---|---|
| Relay trust boundary | Node identity bound to the Hello-authenticated `conn.NodeId`; never re-derived from wire fields (heartbeat / frame-direction / `McpServerId` stamp all use stream identity). Frame peer resolved from the Orleans control plane, **not** `target_node_id`; sender verified as a session participant. Bearer auth fails closed on revoked tokens. Node re-homing across spaces blocked (SEC-CRITICAL-1); agent-clients pinned to a node TOFU (023). NATS subjects base64url-encode ids → no subject/wildcard injection. |
| Cross-tenant / IDOR (`Endpoints.cs`) | Every owner endpoint derives `SpaceId` from the authenticated `UserId`; mutating endpoints re-verify the resource belongs to the caller's space (cloaked-404). Revoke-by-id scopes ownership in the SQL `WHERE`. |
| Tokens / crypto | Secrets use CSPRNG (`RandomNumberGenerator` / `Guid.NewGuid()`); CLI + email-change tokens SHA-256-hashed at rest and matched on the hash; device `user_code` single-use with TTL. No raw/interpolated SQL injection (parameterized). |
| OAuth / CSRF | DataProtection-signed OAuth state, 10-min TTL; `returnUrl` allow-listed + re-validated after percent-decode; GitHub PKCE + verified-primary-email only; cookie-auth JSON POSTs carry `.RequireAntiforgeryValidation()` + SecFetch checks. |
| CLI process spawning | All `UseShellExecute=false` with separated command/args (no shell interpolation); command/args from the node's own trusted config; launchd plist values XML-escaped. |
| Developer API | Double-gated: refuses to register in Production unless `KORAT_ENABLE_DEVELOPER_API=1` is explicitly set, plus a re-check in the route group. |
| Attack-surface | Legacy single-secret `SpaceOwnerAuth` / `DevOwnerAuthExtensions` deleted in the cutover — net reduction. |

### Non-findings (accepted design — not vulnerabilities)
- Session and magic-link tokens are 122-bit CSPRNG ids stored by-id rather than as a separately
  hashed secret — accepted bearer-capability posture, consistent with the 023 AgentClientId TOFU
  model. Optional future hardening: hash at rest like CLI tokens.
- `/api/version` exposes Fly machine/region/image to **authenticated** users only — intentional.

## Notes for future reviewers
The branch carries annotated fixes throughout its sensitive paths (`sec M1`, `ARCH-CRITICAL-1/2`,
`SEC-CRITICAL-1`, IDOR notes, TOFU reasoning) from prior security passes. The
detailed internal review artifact was intentionally omitted from the public
release tree. The relay invariants touched
by features 022 (per-connection routing) and 023 (agent-client binding) were re-confirmed as
defenses, not regressions: relay identity is the Hello-authenticated `conn.NodeId`, and the
session peer is resolved from the control plane rather than the wire.
