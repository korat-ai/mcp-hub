# Korat owner console

This guide covers the React application in `apps/Korat.App`. The console is an
owner control surface for the MCP relay; its default information architecture
must match [architecture.md](architecture.md) and
[../trust-and-privacy.md](../trust-and-privacy.md).

## Stack and build

- React 19
- TypeScript
- Vite
- TanStack Router and Query
- Tailwind CSS
- shadcn/Radix primitives
- Vitest, React Testing Library, and MSW

The production build writes static files into
`apps/Korat.Cloud/wwwroot/app/`; generated output is not committed.

```bash
cd apps/Korat.App
npm ci
npm run build
npm test
npm run lint
```

The Vite development server runs on `:5173` and proxies `/api` to the local
Cloud REST listener.

## Default information architecture

The default sidebar and mobile navigation contain:

1. Overview
2. MCP servers
3. Access
4. Activity
5. Runtimes

Access has two subviews:

- Permissions (`/grants`)
- Connected apps (`/connected-apps`)

The route paths retain old domain terms for compatibility. User-visible labels
use runtime, consumer, permission, and activity.

Inference, Agents, Channels, Rooms, and AG-UI are hidden by default. A deployment
may opt into that separate module at build time:

```bash
VITE_ENABLE_AGENT_PLATFORM=true npm run build
```

Feature gating lives in `src/lib/features.ts`. Do not let optional-module
queries, navigation, onboarding, or statistics leak into the default relay
experience.

## Route and component map

| Surface | Main code |
|---|---|
| shell and desktop navigation | `src/components/layout/AppShell.tsx` |
| mobile navigation | `src/components/layout/MobileTabBar.tsx` |
| overview | `src/routes/index.tsx` |
| MCP server list/detail | `src/routes/servers*.tsx` |
| permissions | `src/routes/grants.tsx` |
| connected OAuth apps | `src/routes/connected-apps.tsx` |
| shared Access tabs | `src/components/domain/AccessNav.tsx` |
| activity | `src/routes/sessions.tsx` |
| runtime list/detail | `src/routes/nodes*.tsx` |
| request approval | `src/routes/approve.$requestId.tsx` |
| status semantics | `src/components/domain/StatusBadges.tsx` |

File routes are generated into `src/routeTree.gen.ts`. Treat that file as
generated output.

## Data contracts

`src/types/api.ts` mirrors the REST projections in
`apps/Korat.Cloud/Web/Endpoints.cs`, not the persistence entities.

Important shapes:

- `/api/space` still serializes strongly typed IDs such as Node and server IDs
  as `{ value }`;
- `/api/grants` explicitly projects flat string IDs;
- `/api/sessions` keeps the session ID as `{ value }` but projects consumer,
  server, and publisher IDs as strings;
- HTTP MCP publisher IDs are `null`;
- older Cloud versions may omit additive fields such as Node `kind` and
  `createdAt`.

Use `getIdValue()` at compatibility boundaries where either a string or
`{ value }` may occur. Do not guess DTO shapes from domain classes.

## Presence and availability

Stored enum values are not sufficient for owner-visible presence.

- Runtime status uses `isNodeOnline()` with Cloud `serverTime`,
  `presenceStaleSeconds`, and the last heartbeat.
- Server availability uses `deriveServerAvailability()` and includes stored
  state, assertion state, transport, and publisher presence.
- Session rows render backend `effectiveStatus`; the UI does not invent fake
  runtimes for HTTP or Space-MCP participants.

Synthetic `kind=agent` rows are transport identities, not publisher machines.
They are excluded from Overview counts, onboarding checks, and the normal
Runtimes table.

## Query and mutation behavior

TanStack Query options live under `src/lib/queries/` and hooks under
`src/hooks/`. Mutations must invalidate every owner surface affected by the
change. In particular:

- approve/deny refreshes pending access and permissions;
- permission revocation refreshes permissions, activity, and overview state;
- server enable/disable/delete refreshes the Space catalog;
- consent revocation refreshes connected apps and affected activity.

Revocation copy must describe actual behavior: an active permission revocation
blocks new sessions and immediately terminates affected live sessions.

## Error and telemetry policy

Browser error reporting is disabled when `VITE_SENTRY_DSN` is empty. The
backend CSP permits only an explicitly configured HTTPS telemetry origin (or
the origin derived from backend `SENTRY_DSN`).

Source-map upload requires all of:

- `SENTRY_AUTH_TOKEN`
- `SENTRY_ORG`
- `SENTRY_PROJECT`
- `SENTRY_URL`

A token by itself must never select an implicit service or project.

The frontend scrubber removes credentials, sensitive query parameters, email
addresses, and local home paths before an event is sent. It does not attach
default PII or enable tracing/replay.

## Test expectations

Prefer behavior assertions over implementation snapshots. Cover:

- default navigation with the optional module disabled;
- opt-in navigation separately;
- loading, error, empty, and data states;
- heartbeat boundary cases;
- HTTP MCP null publisher handling;
- flat versus wrapped ID contracts;
- permission revocation confirmation;
- stable MCP-client setup commands including `--agent`.

When frontend dependencies cannot be restored, syntax-only checks are not a
substitute for `npm run build`, tests, and lint. Record the missing verification
as a release gate.
