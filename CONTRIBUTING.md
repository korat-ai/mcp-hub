# Contributing to Korat

Thank you for helping improve Korat MCP Hub.

## Before contributing

This project is licensed under Apache-2.0 ([`LICENSE`](LICENSE)). By opening a
pull request you agree that your contribution is licensed the same way —
Apache-2.0 §5 says exactly that, so there is no CLA on top of it.

For substantial work, open an issue or discussion first so maintainers can
confirm scope and avoid duplicated effort.

## Development setup

Install the SDK pinned by `global.json`, Node.js 20 or newer, npm, Docker, and
PostgreSQL 16. Then:

```bash
docker compose up -d
dotnet restore Korat.slnx
dotnet build Korat.slnx

cd apps/Korat.App
npm ci
npm run build
```

The full walkthrough is in [docs/dev/README.md](docs/dev/README.md).

## Product vocabulary

Use the owner-facing terms **runtime**, **consumer**, **permission**, and
**activity** in new UI and CLI copy. Existing domain and wire names (`Node`,
`AgentClient`, `Grant`, `Session`) remain stable compatibility contracts.

The default product is the MCP trust and relay layer. Inference, hosted agents,
channels, rooms, and AG-UI are an optional module and should not leak into the
default relay navigation or onboarding.

## Compatibility and security

- Keep REST, protobuf, and released CLI JSON changes additive where possible.
- Preserve `node`/`nodes` CLI aliases.
- Resolve the caller's Space before disclosing entity state.
- Never log or persist MCP payload bodies.
- Treat heartbeat-derived presence as user-visible truth.
- Model HTTP and Space-MCP sessions explicitly; do not invent fake runtime
  presence for cloud-terminated participants.
- Do not commit `.env`, `.mcp.json`, credentials, tokens, private keys, or local
  absolute paths.

Read [ARCHITECTURE.md](ARCHITECTURE.md) before changing admission, routing,
presence, or identity behavior.

## Tests

Run checks proportional to the change:

```bash
dotnet test tests/Korat.Domain.Tests
dotnet test tests/Korat.Cli.Tests
dotnet test tests/Korat.Cloud.ContractTests
dotnet test tests/Korat.Cloud.IntegrationTests

cd apps/Korat.App
npm run build
npm test
npm run lint
```

Persistence and auth suites use Testcontainers and require Docker. The pull
request workflow also builds the production Docker image and trimmed CLI.

## Branches and commits

- Target `dev`, the integration branch.
- `master` is the release branch.
- Keep commits focused and use an imperative subject.
- Do not add generated frontend output under
  `apps/Korat.Cloud/wwwroot/app/`.
- Do not add `Co-authored-by` or other co-authorship trailers.

## Reporting vulnerabilities

Do not report a vulnerability in a public issue. Follow
[SECURITY.md](SECURITY.md).
