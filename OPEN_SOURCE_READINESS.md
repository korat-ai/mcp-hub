# Open-source readiness

Korat is open source under Apache-2.0 (see [`LICENSE`](LICENSE)) and published
at [korat-ai/mcp-hub](https://github.com/korat-ai/mcp-hub). What follows is what
the release cleanup already covered and what still needs an owner decision.

## Completed in the first cleanup pass

- The default console is focused on the MCP relay: Overview, MCP servers,
  Access, Activity, and Runtimes.
- Synthetic consumer identities are hidden from normal runtime views but remain
  available to diagnostics.
- CLI and console availability use heartbeat-derived presence.
- HTTP MCP and Space-MCP sessions no longer appear stale because an intentionally
  absent runtime row is missing.
- Owner-facing access, permission, revocation, and consumer terminology matches
  backend behavior.
- Public TypeScript projections match the REST contracts.
- Machine-specific `.mcp.json` and local review scratch are ignored.
- Founder-only notes, machine inventories, internal operations/runbooks, exact
  environment records, release-manager automation, and generated design
  handoffs were removed from the public release tree.
- Personal workstation paths and fixture names were replaced with neutral
  examples.
- Fly self-hosting guidance now requires explicit app/host targets, documents
  every production startup gate, and no longer embeds volatile prices or a
  personal ACME contact.
- Browser telemetry and source-map upload are opt-in and do not default to a
  private host, organization, or project.
- A heuristic current-tree credential scan found only explicit test fixtures
  and placeholders; relative Markdown links resolve after the cleanup.
- Top-level architecture, contribution, and security guidance is present.

## Blocking decisions

1. **Publish a private security contact.** Enable GitHub private vulnerability
   reporting and add a monitored address or documented equivalent.
2. **Name a maintainer/reviewer policy.** Contribution licensing is settled —
   Apache-2.0 §5 makes contributions inbound=outbound, so no CLA or DCO is
   needed. Who reviews and merges is still unwritten.
3. **Confirm the official CLI telemetry posture.** Source builds are silent
   without a DSN, but the release workflow can embed one and offers
   `KORAT_TELEMETRY=0` as an opt-out. Before publishing binaries, decide whether
   official releases remain opt-out or move to explicit opt-in, and make the
   chosen behavior prominent in release and privacy documentation.

## Required release verification

- Run a dedicated secret scanner (for example gitleaks) on the exact public
  snapshot and, if history is retained, on every reachable historical blob.
  The local heuristic scan is useful evidence but not a substitute.
- Review the official maintainer deployment profiles separately from the
  generic self-hosting guide before deciding whether they belong in the public
  repository.
- Run all CI jobs with network access and Docker:

  ```bash
  dotnet build Korat.slnx -c Release
  dotnet test Korat.slnx -c Release

  cd apps/Korat.App
  npm ci
  npm run build
  npm test
  npm run lint

  docker build .
  ```

- Exercise the local getting-started flow from a clean machine or container.
- Verify a previous supported CLI still works against the new Cloud contracts.
- Verify the default build does not expose the optional agent-platform
  navigation and an opt-in build does.
- Inspect release artifacts and logs to confirm that no telemetry DSN or
  source-map credential is present unless the chosen release policy requires
  it.
- Review generated API/docs output for internal-only endpoints and stale product
  claims.

## Deliberately retained

- Domain/API type names such as `Node`, `AgentClient`, and `Grant` remain for
  compatibility. Public copy maps them to runtime, consumer, and permission.
- Public feature specifications remain as historical implementation context;
  stale links and deployment-specific claims should not be treated as current
  operating instructions.
- The generic Korat usage helper remains because it describes the public CLI,
  not a private release or infrastructure workflow.
- The optional agent-platform implementation remains in the repository behind
  an explicit frontend feature flag; it is not part of the default MCP Hub
  product surface.
