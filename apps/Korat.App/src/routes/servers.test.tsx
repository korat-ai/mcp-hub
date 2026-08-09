/**
 * Unit tests for the ServersPage route component.
 *
 * Covers:
 *  - Unfiltered: all servers rendered.
 *  - ?node=<publisherNodeId> filters to matching servers only.
 *  - ActiveFilterChip appears when filter active; chip label resolves publisherNodeName.
 *  - Clear filter via chip navigates to /servers without the node param.
 *  - Server name cell links to /grants?server=<serverId>.
 *  - Publisher node cell links to /nodes.
 *  - Row click (outside interactive cells) navigates to /servers/$serverId.
 *  - Filtered-to-empty shows scoped empty message.
 *  - Error state when space query fails.
 *
 * Network is mocked at the api module boundary.
 */
import { describe, expect, it, vi, beforeEach } from 'vitest';
import { render, screen, fireEvent, waitFor } from '@testing-library/react';
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
const mockDisable = vi.fn();
const mockDeleteSrv = vi.fn();

vi.mock('@/lib/api', () => ({
  api: {
    space: { get: (...args: unknown[]) => mockSpaceGet(...args) },
    mcpServers: {
      disable: (...args: unknown[]) => mockDisable(...args),
      delete: (...args: unknown[]) => mockDeleteSrv(...args),
    },
    auth: { me: vi.fn() },
    cli: { tokens: { list: vi.fn() } },
  },
  ApiError: class ApiError extends Error {
    constructor(public status: number, public body: string) {
      super(`HTTP ${status}`);
    }
  },
  // Mirrors the REAL getIdValue (apps/Korat.App/src/lib/api.ts) — including its crash on
  // null/undefined — so a test exercising an http_cloud row (null publisherNodeId) actually
  // proves the route guards it, rather than a lenient mock papering over the real bug.
  getIdValue: (id: unknown) =>
    typeof id === 'string' ? id : (id as { value: string }).value,
}));

// Stub presence helpers to avoid time-sensitive logic in tests.
vi.mock('@/lib/presence', () => ({
  computeSkew: () => 0,
  deriveServerAvailability: (
    status: string,
    _isAsserted: boolean,
    _publisherNodeStatus: unknown,
    _publisherNodeLastSeenAt: unknown,
    _presenceStaleSeconds: unknown,
    _skewMs: unknown,
    _nowMs: unknown,
    transport?: string | null,
  ) => {
    if (status === 'Disabled') return 'Disabled' as const;
    if (transport === 'http_cloud') return status === 'Published' ? 'Available' as const : 'Unavailable' as const;
    return 'Available' as const;
  },
}));

// Stub useNow to a fixed value.
vi.mock('@/hooks/useNow', () => ({
  useNow: () => Date.now(),
}));

// Stub toast so it doesn't blow up in tests.
vi.mock('@/lib/toast', () => ({
  toastReceipt: vi.fn(),
}));

// ---------------------------------------------------------------------------
// Fixtures
// ---------------------------------------------------------------------------

const NODE_A = { id: { value: 'node-aaa' }, displayName: 'Node Alpha', status: 'Online', lastSeenAt: null };
const NODE_B = { id: { value: 'node-bbb' }, displayName: 'Node Beta', status: 'Online', lastSeenAt: null };

const SERVER_1 = {
  id: { value: 'srv-001' },
  displayName: 'Auth Server',
  status: 'Published' as const,
  publisherNodeId: { value: 'node-aaa' },
  publisherNodeName: 'Node Alpha',
  publisherNodeStatus: 'Online',
  publisherNodeLastSeenAt: null,
  lastSeenAt: null,
  isAsserted: true,
};

const SERVER_2 = {
  id: { value: 'srv-002' },
  displayName: 'Data Server',
  status: 'Published' as const,
  publisherNodeId: { value: 'node-bbb' },
  publisherNodeName: 'Node Beta',
  publisherNodeStatus: 'Online',
  publisherNodeLastSeenAt: null,
  lastSeenAt: null,
  isAsserted: true,
};

// Increment 1 (HTTP MCP direct-to-Space, Finding 16 M5 / Task-6-gate HIGH): an http_cloud row —
// no publisher node exists at all, so the projection nulls every publisher* field.
const SERVER_3_HTTP_CLOUD = {
  id: { value: 'srv-003' },
  displayName: 'Cloud API Server',
  status: 'Published' as const,
  transport: 'http_cloud',
  publisherNodeId: null,
  publisherNodeName: null,
  publisherNodeStatus: null,
  publisherNodeLastSeenAt: null,
  lastSeenAt: null,
  isAsserted: true,
};

const SPACE_BASE = {
  id: { value: 'space-1' },
  displayName: 'Test Space',
  nodes: [NODE_A, NODE_B],
  pendingAccessRequests: [],
  serverTime: new Date().toISOString(),
  presenceStaleSeconds: 90,
};

// ---------------------------------------------------------------------------
// Helpers
// ---------------------------------------------------------------------------

import { Route as ServersFileRoute } from './servers';

function makeQC() {
  return new QueryClient({
    defaultOptions: {
      queries: { retry: false, gcTime: 0 },
      mutations: { retry: false },
    },
  });
}

function renderPage(initialPath = '/servers') {
  const qc = makeQC();
  const rootRoute = createRootRoute({ component: () => <Outlet /> });

  // A grants route so that links to /grants?server= resolve.
  const grantsRoute = createRoute({
    getParentRoute: () => rootRoute,
    path: '/grants',
    component: () => <div>Grants page</div>,
    validateSearch: (s: Record<string, unknown>) => {
      const out: { server?: string; agent?: string } = {};
      if (typeof s.server === 'string') out.server = s.server;
      if (typeof s.agent === 'string') out.agent = s.agent;
      return out;
    },
  });

  // A nodes route so links to /nodes resolve.
  const nodesRoute = createRoute({
    getParentRoute: () => rootRoute,
    path: '/nodes',
    component: () => <div>Nodes page</div>,
  });

  // Build the /servers route using the real component and validateSearch.
  const serversRoute = createRoute({
    getParentRoute: () => rootRoute,
    path: '/servers',
    // eslint-disable-next-line @typescript-eslint/no-explicit-any
    component: ServersFileRoute.options.component as any,
    validateSearch: ServersFileRoute.options.validateSearch,
  });

  // A server-detail stub, nested under `/servers` (matches production
  // dot-file nesting: servers.$serverId.tsx is a CHILD of servers.tsx) so
  // row clicks (row → detail) resolve AND the atDetail double-render guard
  // in ServersPage can be exercised — a sibling route would never trigger it.
  const serverDetailRoute = createRoute({
    getParentRoute: () => serversRoute,
    path: '$serverId',
    component: () => <div data-testid="server-detail-stub">Server detail page</div>,
  });

  const history = createMemoryHistory({ initialEntries: [initialPath] });
  const router = createRouter({
    routeTree: rootRoute.addChildren([serversRoute.addChildren([serverDetailRoute]), grantsRoute, nodesRoute]),
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
  mockSpaceGet.mockResolvedValue({ ...SPACE_BASE, mcpServers: [SERVER_1, SERVER_2] });
  mockDisable.mockResolvedValue(undefined);
  mockDeleteSrv.mockResolvedValue(undefined);
});

// ---------------------------------------------------------------------------
// Unfiltered list
// ---------------------------------------------------------------------------

describe('ServersPage — unfiltered list', () => {
  it('renders all servers when no filter param', async () => {
    renderPage();
    expect(await screen.findByText('Auth Server')).toBeInTheDocument();
    expect(screen.getByText('Data Server')).toBeInTheDocument();
  });

  it('does not show the filter chip when no filter active', async () => {
    renderPage();
    await screen.findByText('Auth Server');
    expect(screen.queryByTestId('active-filter-chip')).toBeNull();
  });

  it('renders publisher node names for all servers', async () => {
    renderPage();
    await screen.findByText('Auth Server');
    expect(screen.getByText('Node Alpha')).toBeInTheDocument();
    expect(screen.getByText('Node Beta')).toBeInTheDocument();
  });
});

// ---------------------------------------------------------------------------
// Node filter
// ---------------------------------------------------------------------------

describe('ServersPage — ?node= filter', () => {
  it('shows only servers whose publisherNodeId matches the filter', async () => {
    renderPage('/servers?node=node-aaa');
    expect(await screen.findByText('Auth Server')).toBeInTheDocument();
    expect(screen.queryByText('Data Server')).toBeNull();
  });

  it('renders ActiveFilterChip when node param is active', async () => {
    renderPage('/servers?node=node-aaa');
    await screen.findByText('Auth Server');
    expect(screen.getByTestId('active-filter-chip')).toBeInTheDocument();
  });

  it('chip label resolves publisherNodeName from first matching server', async () => {
    renderPage('/servers?node=node-aaa');
    await screen.findByText('Auth Server');
    const chip = screen.getByTestId('active-filter-chip');
    // The node filter chip now renders the "Node" dimension eyebrow instead of
    // the generic "Filtered by" prefix (prototype FilterChip parity).
    expect(chip).not.toHaveTextContent('Filtered by');
    expect(chip).toHaveTextContent('Node Alpha');
  });

  it('clears filter when chip clear button is clicked', async () => {
    renderPage('/servers?node=node-aaa');
    await screen.findByText('Auth Server');
    const clearBtn = screen.getByRole('button', { name: /clear filter/i });
    fireEvent.click(clearBtn);
    // After clearing, both servers should be visible
    await waitFor(() => {
      expect(screen.getByText('Data Server')).toBeInTheDocument();
    });
    expect(screen.queryByTestId('active-filter-chip')).toBeNull();
  });
});

// ---------------------------------------------------------------------------
// Links
// ---------------------------------------------------------------------------

describe('ServersPage — links', () => {
  it('server name cell is a link pointing to /grants with server param', async () => {
    renderPage();
    await screen.findByText('Auth Server');
    const link = screen.getByRole('link', { name: 'Auth Server' });
    expect(link).toBeInTheDocument();
    const href = link.getAttribute('href') ?? '';
    expect(href).toContain('/grants');
    expect(href).toContain('server=srv-001');
  });

  it('publisher node cell is a link pointing to /nodes', async () => {
    renderPage();
    await screen.findByText('Node Alpha');
    // EntityLink renders a Link with the node name; find all links with that text
    const nodeLinks = screen.getAllByRole('link', { name: 'Node Alpha' });
    expect(nodeLinks.length).toBeGreaterThanOrEqual(1);
    const href = nodeLinks[0].getAttribute('href') ?? '';
    expect(href).toContain('/nodes');
  });
});

// ---------------------------------------------------------------------------
// Row → detail
// ---------------------------------------------------------------------------

describe('ServersPage — row → detail', () => {
  it('clicking the row (outside the name/node links and action cell) opens the server detail route', async () => {
    renderPage();
    await screen.findByText('Auth Server');
    const row = screen.getByText('Auth Server').closest('tr');
    expect(row).not.toBeNull();
    // Click the row itself (not its child link) — this is what a click on the
    // Status cell or empty row space would dispatch.
    fireEvent.click(row as HTMLElement);
    expect(await screen.findByText('Server detail page')).toBeInTheDocument();
  });

  it('clicking the name link still goes to /grants, not the detail route', async () => {
    renderPage();
    const link = await screen.findByRole('link', { name: 'Auth Server' });
    fireEvent.click(link);
    expect(await screen.findByText('Grants page')).toBeInTheDocument();
  });

  it('replaces the master list with the detail Outlet instead of double-rendering', async () => {
    renderPage();
    await screen.findByText('Auth Server');
    const row = screen.getByText('Auth Server').closest('tr');
    fireEvent.click(row as HTMLElement);
    await screen.findByTestId('server-detail-stub');
    // The master list's own chrome (Add MCP server button + other rows) must
    // not still be present underneath the detail Outlet.
    expect(screen.queryByRole('link', { name: /add mcp server/i })).toBeNull();
    expect(screen.queryByText('Data Server')).toBeNull();
  });

  // Fable review (#186 MEDIUM-1): role="button" on a <tr> is an ARIA violation — it strips the
  // row of its native `row` semantics (cells lose column-header association) and turns any
  // nested <Link> into an invalid nested-interactive structure that screen readers may stop
  // exposing. Restored real row semantics: the <tr> is mouse-only now; the Name cell already
  // contains a genuine <Link> (EntityLink → /grants, asserted above), which is what gives
  // keyboard/screen-reader users real access into this row.
  it('does not expose role="button" or tabIndex on the row', async () => {
    renderPage();
    await screen.findByText('Auth Server');
    const row = screen.getByText('Auth Server').closest('tr') as HTMLElement;
    expect(row).not.toHaveAttribute('role', 'button');
    expect(row).not.toHaveAttribute('tabindex');
  });
});

// ---------------------------------------------------------------------------
// Filtered empty state
// ---------------------------------------------------------------------------

describe('ServersPage — filtered empty state', () => {
  it('shows scoped empty message when filter matches no servers', async () => {
    renderPage('/servers?node=node-zzz');
    // Chip is still visible
    await waitFor(() => {
      expect(screen.getByTestId('active-filter-chip')).toBeInTheDocument();
    });
    expect(screen.getByText(/no servers match this filter/i)).toBeInTheDocument();
  });
});

// ---------------------------------------------------------------------------
// Global empty state
// ---------------------------------------------------------------------------

describe('ServersPage — global empty state', () => {
  it('renders empty state when space has no servers at all', async () => {
    mockSpaceGet.mockResolvedValue({ ...SPACE_BASE, mcpServers: [] });
    renderPage();
    expect(await screen.findByText('No MCP servers')).toBeInTheDocument();
  });
});

// ---------------------------------------------------------------------------
// Error state
// ---------------------------------------------------------------------------

describe('ServersPage — error state', () => {
  it('renders error state when space query fails', async () => {
    mockSpaceGet.mockRejectedValue(new Error('Network error'));
    renderPage();
    expect(await screen.findByText(/could not load servers/i)).toBeInTheDocument();
  });
});

// ---------------------------------------------------------------------------
// http_cloud servers (Increment 1, Finding 16 M5 / Task-6-gate HIGH regression)
// ---------------------------------------------------------------------------

describe('ServersPage — http_cloud server (Finding 16, M5)', () => {
  it('renders an http_cloud row without crashing on its null publisherNodeId', async () => {
    mockSpaceGet.mockResolvedValue({ ...SPACE_BASE, mcpServers: [SERVER_1, SERVER_3_HTTP_CLOUD] });
    renderPage();
    expect(await screen.findByText('Auth Server')).toBeInTheDocument();
    expect(screen.getByText('Cloud API Server')).toBeInTheDocument();
  });

  it('shows a disclosed "Cloud-terminated" badge instead of a node link', async () => {
    mockSpaceGet.mockResolvedValue({ ...SPACE_BASE, mcpServers: [SERVER_3_HTTP_CLOUD] });
    renderPage();
    expect(await screen.findByText('Cloud-terminated')).toBeInTheDocument();
  });

  it('does not crash when the ?node= filter is active alongside an http_cloud row', async () => {
    mockSpaceGet.mockResolvedValue({ ...SPACE_BASE, mcpServers: [SERVER_1, SERVER_3_HTTP_CLOUD] });
    renderPage('/servers?node=node-aaa');
    expect(await screen.findByText('Auth Server')).toBeInTheDocument();
    // http_cloud row can never match a node filter (it has no publisher node).
    expect(screen.queryByText('Cloud API Server')).toBeNull();
  });
});
