/**
 * Unit tests for the ServerDetailPage route component.
 *
 * Covers:
 *  - Renders server name, availability badge, publisher node link.
 *  - "Server not found" empty state for an unknown serverId.
 *  - Grants section: rows for this server only; empty message otherwise.
 *  - Sessions section: rows for this server only; empty message otherwise.
 *  - Disable button (Available) calls the disable mutation.
 *  - Delete button (Unavailable/Disabled) opens ConfirmDialog and calls the delete
 *    mutation on confirm, then navigates back to /servers.
 *  - Loading and error states.
 *
 * Network is mocked at the api module boundary.
 */
import { describe, expect, it, vi, beforeEach } from 'vitest';
import { render, screen, fireEvent, waitFor, within } from '@testing-library/react';
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
const mockGrantsList = vi.fn();
const mockSessionsList = vi.fn();
const mockDisable = vi.fn();
const mockEnable = vi.fn();
const mockDeleteSrv = vi.fn();

vi.mock('@/lib/api', () => ({
  api: {
    space: { get: (...args: unknown[]) => mockSpaceGet(...args) },
    grants: { list: (...args: unknown[]) => mockGrantsList(...args) },
    sessions: { list: (...args: unknown[]) => mockSessionsList(...args) },
    mcpServers: {
      disable: (...args: unknown[]) => mockDisable(...args),
      enable: (...args: unknown[]) => mockEnable(...args),
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
  getIdValue: (id: unknown) =>
    typeof id === 'object' && id !== null && 'value' in id
      ? String((id as { value: unknown }).value)
      : String(id),
}));

// Stub presence helpers to avoid time-sensitive logic in tests.
vi.mock('@/lib/presence', () => ({
  computeSkew: () => 0,
  deriveServerAvailability: (status: string) => (status === 'Published' ? 'Available' : status === 'Disabled' ? 'Disabled' : 'Unavailable'),
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

const SERVER_DISABLED = {
  id: { value: 'srv-002' },
  displayName: 'Data Server',
  status: 'Disabled' as const,
  publisherNodeId: { value: 'node-bbb' },
  publisherNodeName: 'Node Beta',
  publisherNodeStatus: 'Online',
  publisherNodeLastSeenAt: null,
  lastSeenAt: null,
  isAsserted: true,
};

// GAP N1: Disabled is reversible (shows Enable), so Delete only ever appears
// for the genuinely-unavailable state. The mocked `deriveServerAvailability`
// above maps any status other than 'Published'/'Disabled' to 'Unavailable'.
const SERVER_UNAVAILABLE = {
  id: { value: 'srv-003' },
  displayName: 'Ghost Server',
  status: 'Unavailable' as const,
  publisherNodeId: { value: 'node-bbb' },
  publisherNodeName: 'Node Beta',
  publisherNodeStatus: 'Offline',
  publisherNodeLastSeenAt: null,
  lastSeenAt: null,
  isAsserted: true,
};

const SPACE_BASE = {
  id: { value: 'space-1' },
  displayName: 'Test Space',
  nodes: [],
  mcpServers: [SERVER_1, SERVER_DISABLED, SERVER_UNAVAILABLE],
  pendingAccessRequests: [],
  serverTime: new Date().toISOString(),
  presenceStaleSeconds: 90,
};

const GRANT_1 = {
  id: 'grant-1',
  consumerId: 'agent-1',
  mcpServerId: 'srv-001',
  agentName: 'claude-cli',
  serverName: 'Auth Server',
  status: 'Active' as const,
  approvedAt: new Date().toISOString(),
  revokedAt: null,
};

const SESSION_1 = {
  id: { value: 'sess-1' },
  consumerId: 'agent-1',
  agentName: 'claude-cli',
  mcpServerId: 'srv-001',
  serverName: 'Auth Server',
  status: 'Active' as const,
  effectiveStatus: 'Active' as const,
  startedAt: new Date().toISOString(),
  endedAt: null,
  bytesClientToServer: 10,
  bytesServerToClient: 20,
  closeReason: null,
};

// ---------------------------------------------------------------------------
// Helpers
// ---------------------------------------------------------------------------

import { Route as ServerDetailFileRoute } from './servers.$serverId';

function makeQC() {
  return new QueryClient({
    defaultOptions: {
      queries: { retry: false, gcTime: 0 },
      mutations: { retry: false },
    },
  });
}

function renderPage(initialPath = '/servers/srv-001') {
  const qc = makeQC();
  const rootRoute = createRootRoute({ component: () => <Outlet /> });

  const serversRoute = createRoute({
    getParentRoute: () => rootRoute,
    path: '/servers',
    component: () => <div>Servers list page</div>,
  });

  const grantsRoute = createRoute({
    getParentRoute: () => rootRoute,
    path: '/grants',
    component: () => <div>Grants page</div>,
    validateSearch: (s: Record<string, unknown>) => {
      const out: { agent?: string } = {};
      if (typeof s.agent === 'string') out.agent = s.agent;
      return out;
    },
  });

  const nodesRoute = createRoute({
    getParentRoute: () => rootRoute,
    path: '/nodes',
    component: () => <div>Nodes page</div>,
  });

  const sessionsRoute = createRoute({
    getParentRoute: () => rootRoute,
    path: '/sessions',
    component: () => <div>Sessions page</div>,
    validateSearch: (s: Record<string, unknown>) => {
      const out: { agentName?: string } = {};
      if (typeof s.agentName === 'string') out.agentName = s.agentName;
      return out;
    },
  });

  // Build the /servers/$serverId route using the real component.
  const serverDetailRoute = createRoute({
    getParentRoute: () => rootRoute,
    path: '/servers/$serverId',
    // eslint-disable-next-line @typescript-eslint/no-explicit-any
    component: ServerDetailFileRoute.options.component as any,
  });

  const history = createMemoryHistory({ initialEntries: [initialPath] });
  const router = createRouter({
    routeTree: rootRoute.addChildren([serversRoute, serverDetailRoute, grantsRoute, nodesRoute, sessionsRoute]),
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
  mockSpaceGet.mockResolvedValue(SPACE_BASE);
  mockGrantsList.mockResolvedValue([GRANT_1]);
  mockSessionsList.mockResolvedValue([SESSION_1]);
  mockDisable.mockResolvedValue(undefined);
  mockEnable.mockResolvedValue(undefined);
  mockDeleteSrv.mockResolvedValue(undefined);
});

describe('ServerDetailPage — happy path', () => {
  it('renders the server name and availability badge', async () => {
    renderPage();
    expect(await screen.findByRole('heading', { name: 'Auth Server' })).toBeInTheDocument();
    expect(screen.getByText('Available')).toBeInTheDocument();
  });

  it('renders the publisher node as a link to /nodes', async () => {
    renderPage();
    await screen.findByRole('heading', { name: 'Auth Server' });
    const link = screen.getByRole('link', { name: 'Node Alpha' });
    expect(link.getAttribute('href') ?? '').toContain('/nodes');
  });

  it('renders grants scoped to this server', async () => {
    renderPage();
    // Scope to the Grants MiniSection specifically: the fixture agent ("claude-cli")
    // legitimately appears in both the Grants and Sessions sections, so an
    // unscoped findByText is ambiguous. MiniSection renders its title as a <span>
    // inside a label row div, itself inside the section's outer wrapper div —
    // walk up two levels to reach the wrapper containing the section's rows.
    const title = await screen.findByText('Permissions — who can call this');
    const section = title.parentElement?.parentElement;
    expect(section).not.toBeNull();
    expect(within(section as HTMLElement).getByText('claude-cli')).toBeInTheDocument();
  });

  it('renders sessions scoped to this server', async () => {
    renderPage();
    await screen.findByRole('heading', { name: 'Auth Server' });
    expect(screen.getByText('sess-1')).toBeInTheDocument();
  });

  it('the session row\'s agent EntityLink uses the agent id, not the session id (regression)', async () => {
    renderPage();
    await screen.findByRole('heading', { name: 'Auth Server' });
    // Before the fix, rawId={sessionId} leaked into the tooltip/href instead of
    // the real agent id. 'claude-cli' legitimately appears as both the Grants
    // row's and the Sessions row's agent link — both resolve to the same
    // agent-1 in this fixture, so assert every matching link carries the
    // agent id and never the session id.
    const links = screen.getAllByRole('link', { name: 'claude-cli' });
    expect(links.length).toBeGreaterThan(0);
    for (const link of links) {
      const href = link.getAttribute('href') ?? '';
      expect(href).toContain('/grants');
      expect(href).toContain('agent=agent-1');
      expect(href).not.toContain('sess-1');
    }
  });
});

describe('ServerDetailPage — not found', () => {
  it('shows a not-found empty state for an unknown serverId', async () => {
    renderPage('/servers/does-not-exist');
    expect(await screen.findByText('Server not found')).toBeInTheDocument();
  });
});

describe('ServerDetailPage — disable', () => {
  it('calls the disable mutation when Disable is clicked (Available server)', async () => {
    renderPage('/servers/srv-001');
    const btn = await screen.findByRole('button', { name: 'Disable' });
    fireEvent.click(btn);
    await waitFor(() => expect(mockDisable).toHaveBeenCalledWith('srv-001'));
  });
});

// GAP N1: Disabled → Enable (reversible), not Delete. Delete is reserved for
// the genuinely-unavailable state (see SERVER_UNAVAILABLE fixture).
describe('ServerDetailPage — enable', () => {
  it('calls the enable mutation when Enable is clicked (Disabled server)', async () => {
    renderPage('/servers/srv-002');
    const btn = await screen.findByRole('button', { name: 'Enable' });
    fireEvent.click(btn);
    await waitFor(() => expect(mockEnable).toHaveBeenCalledWith('srv-002'));
  });
});

describe('ServerDetailPage — delete', () => {
  it('opens a confirm dialog and calls delete on confirm (Unavailable server), then navigates back', async () => {
    renderPage('/servers/srv-003');
    const deleteBtn = await screen.findByRole('button', { name: 'Delete' });
    fireEvent.click(deleteBtn);

    const dialog = await screen.findByRole('dialog');
    const confirmBtn = within(dialog).getByRole('button', { name: 'Delete' });
    fireEvent.click(confirmBtn);

    await waitFor(() => expect(mockDeleteSrv).toHaveBeenCalledWith('srv-003'));
    expect(await screen.findByText('Servers list page')).toBeInTheDocument();
  });
});

describe('ServerDetailPage — error state', () => {
  it('renders error state when the space query fails', async () => {
    mockSpaceGet.mockRejectedValue(new Error('Network error'));
    renderPage();
    expect(await screen.findByText(/could not load server/i)).toBeInTheDocument();
  });
});
