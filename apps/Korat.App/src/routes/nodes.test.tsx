/**
 * Unit tests for the NodesPage route component.
 *
 * Covers:
 *  - Unfiltered: all nodes rendered.
 *  - Row click navigates to /nodes/$name (the node's real id) — NodesScreen
 *    parity ("row → detail"). NOTE: this replaces the previous contract where
 *    the node name cell linked straight to /servers?node=<id>; that
 *    capability now lives inside the node detail page's "MCP servers
 *    published" section (with its own link back out to the filtered list).
 *  - `?name=` filter chip (id-based; see nodes.tsx validateSearch comment).
 *  - Empty state when space has no nodes.
 *  - Error state when space query fails.
 *
 * Network is mocked at the api module boundary.
 */
import { describe, expect, it, vi, beforeEach } from 'vitest';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import {
  createMemoryHistory,
  createRouter,
  createRootRoute,
  createRoute,
  RouterProvider,
  Outlet,
} from '@tanstack/react-router';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';

// ---------------------------------------------------------------------------
// Mock api module — must be declared before importing the route
// ---------------------------------------------------------------------------

const mockSpaceGet = vi.fn();

vi.mock('@/lib/api', () => ({
  api: {
    space: { get: (...args: unknown[]) => mockSpaceGet(...args) },
    auth: { me: vi.fn() },
    cli: { tokens: { list: vi.fn() } },
  },
  ApiError: class ApiError extends Error {
    constructor(public status: number, public body: string) {
      super(`HTTP ${status}`);
    }
  },
  getIdValue: (id: unknown) =>
    typeof id === 'object' && id !== null && 'value' in id
      ? String((id as { value: unknown }).value)
      : String(id),
}));

// Stub presence helpers to avoid time-sensitive logic in tests.
vi.mock('@/lib/presence', () => ({
  computeSkew: () => 0,
  isNodeOnline: () => true,
}));

// Stub useNow to a fixed value.
vi.mock('@/hooks/useNow', () => ({
  useNow: () => Date.now(),
}));

// ---------------------------------------------------------------------------
// Fixtures
// ---------------------------------------------------------------------------

const NODE_A = {
  id: { value: 'node-aaa' },
  displayName: 'Alpha Node',
  status: 'Online' as const,
  kind: 'publisher' as const,
  lastSeenAt: '2026-06-17T10:00:00Z',
  createdAt: '2026-06-01T10:00:00Z',
  hostname: 'alpha-mbp.local',
  os: 'macos',
  arch: 'arm64',
  cliVersion: '0.4.1',
  note: 'primary laptop',
};

const NODE_B = {
  id: { value: 'node-bbb' },
  displayName: 'Beta Node',
  status: 'Offline' as const,
  kind: 'publisher' as const,
  lastSeenAt: null,
  createdAt: '2026-06-01T10:00:00Z',
  hostname: null,
  os: null,
  arch: null,
  cliVersion: null,
  note: null,
};

const CONSUMER_IDENTITY = {
  id: { value: 'node-consumer' },
  displayName: 'Internal Consumer Identity',
  status: 'Offline' as const,
  kind: 'agent' as const,
  lastSeenAt: null,
  createdAt: '2026-06-01T10:00:00Z',
  hostname: null,
  os: null,
  arch: null,
  cliVersion: null,
  note: null,
};

const SPACE_BASE = {
  id: { value: 'space-1' },
  displayName: 'Test Space',
  mcpServers: [],
  pendingAccessRequests: [],
  serverTime: new Date().toISOString(),
  presenceStaleSeconds: 90,
};

// ---------------------------------------------------------------------------
// Helpers
// ---------------------------------------------------------------------------

import { NodesPage } from './nodes';

function makeQC() {
  return new QueryClient({
    defaultOptions: {
      queries: { retry: false, gcTime: 0 },
      mutations: { retry: false },
    },
  });
}

function renderPage(initialPath = '/nodes') {
  const qc = makeQC();
  const rootRoute = createRootRoute({ component: () => <Outlet /> });

  const nodesRoute = createRoute({
    getParentRoute: () => rootRoute,
    path: '/nodes',
    component: NodesPage,
    validateSearch: (s: Record<string, unknown>) => {
      const out: { name?: string } = {};
      if (typeof s.name === 'string') out.name = s.name;
      return out;
    },
  });

  // A node-detail stub, nested under `/nodes` (matches production dot-file
  // nesting: nodes.$name.tsx is a CHILD of nodes.tsx) so row clicks (→
  // /nodes/$name) resolve AND the atDetail double-render guard in NodesPage
  // can be exercised — a sibling route would never trigger that guard.
  const nodeDetailRoute = createRoute({
    getParentRoute: () => nodesRoute,
    path: '$name',
    component: () => <div data-testid="node-detail-stub">Node detail page</div>,
  });

  const history = createMemoryHistory({ initialEntries: [initialPath] });
  const router = createRouter({
    routeTree: rootRoute.addChildren([nodesRoute.addChildren([nodeDetailRoute])]),
    history,
  });

  render(
    <QueryClientProvider client={qc}>
      <RouterProvider router={router} />
    </QueryClientProvider>,
  );
  return { qc, router };
}

// ---------------------------------------------------------------------------
// Tests
// ---------------------------------------------------------------------------

beforeEach(() => {
  vi.clearAllMocks();
  mockSpaceGet.mockResolvedValue({ ...SPACE_BASE, nodes: [NODE_A, NODE_B, CONSUMER_IDENTITY] });
});

// ---------------------------------------------------------------------------
// Unfiltered list
// ---------------------------------------------------------------------------

describe('NodesPage — unfiltered list', () => {
  it('renders publisher runtimes and hides synthetic consumer identities', async () => {
    renderPage();
    expect(await screen.findByText('Alpha Node')).toBeInTheDocument();
    expect(screen.getByText('Beta Node')).toBeInTheDocument();
    expect(screen.queryByText('Internal Consumer Identity')).toBeNull();
  });

  it('renders the hostname when the node has reported one', async () => {
    renderPage();
    await screen.findByText('Alpha Node');
    expect(screen.getByText('alpha-mbp.local')).toBeInTheDocument();
    expect(screen.getByText('macos')).toBeInTheDocument();
  });

  it('falls back to the host short-id when no hostname was reported', async () => {
    renderPage();
    await screen.findByText('Beta Node');
    // shortId truncates to 12 chars; 'node-bbb' is ≤12 chars so it shows as-is
    expect(screen.getByText('node-bbb')).toBeInTheDocument();
  });

  it('shows the node note as a subtitle when set', async () => {
    renderPage();
    await screen.findByText('Alpha Node');
    expect(screen.getByText('primary laptop')).toBeInTheDocument();
  });

  it('renders no note subtitle when the node has none', async () => {
    renderPage();
    const betaRow = (await screen.findByText('Beta Node')).closest('tr');
    expect(betaRow).not.toBeNull();
    // Beta Node has no note — its own row must not contain Alpha's note text
    // (which legitimately renders elsewhere on the page, in Alpha's own row).
    expect(betaRow!.textContent).not.toContain('primary laptop');
  });
});

// ---------------------------------------------------------------------------
// Row → detail
// ---------------------------------------------------------------------------

describe('NodesPage — row click navigates to detail', () => {
  it('clicking a row navigates to /nodes/<its real id>', async () => {
    const { router } = renderPage();
    const row = (await screen.findByText('Alpha Node')).closest('tr');
    expect(row).not.toBeNull();
    await userEvent.click(row!);
    await waitFor(() => expect(router.state.location.pathname).toBe('/nodes/node-aaa'));
  });

  it('a different row navigates to its own id', async () => {
    const { router } = renderPage();
    const row = (await screen.findByText('Beta Node')).closest('tr');
    expect(row).not.toBeNull();
    await userEvent.click(row!);
    await waitFor(() => expect(router.state.location.pathname).toBe('/nodes/node-bbb'));
  });

  it('replaces the master list with the detail Outlet instead of double-rendering', async () => {
    renderPage();
    const row = (await screen.findByText('Alpha Node')).closest('tr');
    await userEvent.click(row!);
    await screen.findByTestId('node-detail-stub');
    // The master list's own chrome (Add Node button + other rows) must not
    // still be present underneath the detail Outlet.
    expect(screen.queryByRole('link', { name: /add node/i })).toBeNull();
    expect(screen.queryByText('Beta Node')).toBeNull();
  });

  // Fable review (#186 MEDIUM-1): role="button" on a <tr> is an ARIA violation — it strips the
  // row of its native `row` semantics (cells lose column-header association) and turns any
  // nested <Link> into an invalid nested-interactive structure that screen readers may stop
  // exposing. Restored real row semantics: the <tr> is mouse-only now, keyboard/screen-reader
  // access instead comes from a genuine <Link> in the row's primary (Name) cell.
  it('does not expose role="button" or tabIndex on the row', async () => {
    renderPage();
    const row = (await screen.findByText('Alpha Node')).closest('tr') as HTMLElement;
    expect(row).not.toHaveAttribute('role', 'button');
    expect(row).not.toHaveAttribute('tabindex');
  });

  it('exposes the node name as a real focusable link to its detail route', async () => {
    const { router } = renderPage();
    const link = await screen.findByRole('link', { name: 'Alpha Node' });
    expect(link).toHaveAttribute('href', '/nodes/node-aaa');
    await userEvent.click(link);
    await waitFor(() => expect(router.state.location.pathname).toBe('/nodes/node-aaa'));
  });
});

// ---------------------------------------------------------------------------
// `?name=` filter chip
// ---------------------------------------------------------------------------

describe('NodesPage — `?name=` filter chip', () => {
  it('filters the table down to the matching node id', async () => {
    renderPage('/nodes?name=node-aaa');
    // "Alpha Node" legitimately appears twice while filtered — once in the
    // ActiveFilterChip label, once in the table row.
    expect((await screen.findAllByText('Alpha Node')).length).toBe(2);
    expect(screen.queryByText('Beta Node')).toBeNull();
  });

  it('shows an active filter chip labelled with the node display name', async () => {
    renderPage('/nodes?name=node-aaa');
    await screen.findAllByText('Alpha Node');
    expect(screen.getByTestId('active-filter-chip')).toHaveTextContent('Alpha Node');
  });

  it('clearing the chip navigates back to the unfiltered path', async () => {
    const { router } = renderPage('/nodes?name=node-aaa');
    await screen.findAllByText('Alpha Node');
    await userEvent.click(screen.getByRole('button', { name: /clear/i }));
    await waitFor(() => expect(router.state.location.pathname).toBe('/nodes'));
    expect(await screen.findByText('Beta Node')).toBeInTheDocument();
  });
});

// ---------------------------------------------------------------------------
// Empty state
// ---------------------------------------------------------------------------

describe('NodesPage — empty state', () => {
  it('renders empty state when space has no nodes', async () => {
    mockSpaceGet.mockResolvedValue({ ...SPACE_BASE, nodes: [] });
    renderPage();
    expect(await screen.findByText('No publisher runtimes')).toBeInTheDocument();
  });
});

// ---------------------------------------------------------------------------
// Error state
// ---------------------------------------------------------------------------

describe('NodesPage — error state', () => {
  it('renders error state when space query fails', async () => {
    mockSpaceGet.mockRejectedValue(new Error('Network error'));
    renderPage();
    expect(await screen.findByText(/could not load runtimes/i)).toBeInTheDocument();
  });
});
