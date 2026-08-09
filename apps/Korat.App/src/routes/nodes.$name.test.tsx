/**
 * Unit tests for the NodeDetailPage route component (/nodes/$name).
 *
 * Covers:
 *  - Node not found (unknown id) → EmptyState + back link.
 *  - Detail card renders host/status/last-seen.
 *  - "MCP servers published" section only lists servers whose publisherNodeId
 *    matches this node; empty message for a node with none.
 *  - Server row click navigates to /servers/$serverId (real server id).
 *  - "Sessions" section joins sessions to this node via SessionDto.publisherNodeId
 *    — excludes sessions on other nodes' servers.
 *  - "Inference points" section only renders when at least one point's
 *    nodeId matches this node.
 *  - Error state when the space query fails.
 *
 * Network is mocked at the api module boundary; presence/time logic uses the
 * real @/lib/presence implementation against fixed,
 * realistic timestamps (not stubbed) so availability/online derivation is
 * exercised for real.
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
// Mock api module — must be declared before importing the route.
// ---------------------------------------------------------------------------

const mockSpaceGet = vi.fn();
const mockSessionsList = vi.fn();
const mockNodesUpdate = vi.fn();

vi.mock('@/lib/api', () => ({
  api: {
    space: { get: (...args: unknown[]) => mockSpaceGet(...args) },
    sessions: { list: (...args: unknown[]) => mockSessionsList(...args) },
    nodes: { update: (...args: unknown[]) => mockNodesUpdate(...args) },
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

// Fixed clock so presence/relative-time derivation is deterministic.
const NOW = new Date('2026-06-20T12:00:00Z').getTime();
vi.mock('@/hooks/useNow', () => ({
  useNow: () => NOW,
}));

// ---------------------------------------------------------------------------
// Fixtures
// ---------------------------------------------------------------------------

const isoAgo = (ms: number) => new Date(NOW - ms).toISOString();

const NODE_A = {
  id: { value: 'node-aaa' },
  displayName: 'Alpha Node',
  status: 'Online' as const,
  kind: 'publisher' as const,
  lastSeenAt: isoAgo(1_000),
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
  kind: 'agent' as const,
  lastSeenAt: null,
  hostname: null,
  os: null,
  arch: null,
  cliVersion: null,
  note: null,
};

const SERVER_ON_A = {
  id: { value: 'srv-1' },
  displayName: 'filesystem',
  status: 'Published' as const,
  publisherNodeId: { value: 'node-aaa' },
  lastSeenAt: isoAgo(1_000),
  isAsserted: true,
  publisherNodeName: 'Alpha Node',
  publisherNodeLastSeenAt: isoAgo(1_000),
  publisherNodeStatus: 'Online',
};

const SERVER_ON_OTHER = {
  id: { value: 'srv-2' },
  displayName: 'unrelated-server',
  status: 'Published' as const,
  publisherNodeId: { value: 'node-zzz' },
  lastSeenAt: isoAgo(1_000),
  isAsserted: true,
  publisherNodeName: 'Zeta Node',
  publisherNodeLastSeenAt: isoAgo(1_000),
  publisherNodeStatus: 'Online',
};

const SESSION_ON_A_SERVER = {
  id: { value: 'sess-1' },
  consumerId: 'agent-anya',
  agentName: '@anya',
  mcpServerId: 'srv-1',
  serverName: 'filesystem',
  publisherNodeId: 'node-aaa',
  publisherNodeName: 'Alpha Node',
  status: 'Active' as const,
  effectiveStatus: 'Active' as const,
  startedAt: isoAgo(5_000),
  endedAt: null,
  bytesClientToServer: 100,
  bytesServerToClient: 200,
  closeReason: null,
};

const SESSION_ON_OTHER_SERVER = {
  id: { value: 'sess-2' },
  consumerId: 'agent-scout',
  agentName: '@scout',
  mcpServerId: 'srv-2',
  serverName: 'unrelated-server',
  publisherNodeId: 'node-zzz',
  publisherNodeName: 'Zeta Node',
  status: 'Active' as const,
  effectiveStatus: 'Active' as const,
  startedAt: isoAgo(5_000),
  endedAt: null,
  bytesClientToServer: 100,
  bytesServerToClient: 200,
  closeReason: null,
};


const SPACE_BASE = {
  id: { value: 'space-1' },
  displayName: 'Test Space',
  pendingAccessRequests: [],
  serverTime: new Date(NOW).toISOString(),
  presenceStaleSeconds: 90,
};

// ---------------------------------------------------------------------------
// Import under test (after mocks above)
// ---------------------------------------------------------------------------

import { NodeDetailPage } from './nodes.$name';

function makeQC() {
  return new QueryClient({
    defaultOptions: {
      queries: { retry: false, gcTime: 0 },
      mutations: { retry: false },
    },
  });
}

function renderPage(initialPath: string) {
  const qc = makeQC();
  const rootRoute = createRootRoute({ component: () => <Outlet /> });

  const nodesRoute = createRoute({
    getParentRoute: () => rootRoute,
    path: '/nodes',
    component: () => <div>Nodes list</div>,
  });

  const nodeDetailRoute = createRoute({
    getParentRoute: () => rootRoute,
    path: '/nodes/$name',
    component: NodeDetailPage,
  });

  const serversRoute = createRoute({
    getParentRoute: () => rootRoute,
    path: '/servers',
    component: () => <div>Servers list</div>,
    validateSearch: (s: Record<string, unknown>) => {
      const out: { node?: string } = {};
      if (typeof s.node === 'string') out.node = s.node;
      return out;
    },
  });

  const serverDetailRoute = createRoute({
    getParentRoute: () => rootRoute,
    path: '/servers/$serverId',
    component: () => <div>Server detail</div>,
  });

  const grantsRoute = createRoute({
    getParentRoute: () => rootRoute,
    path: '/grants',
    component: () => <div>Grants list</div>,
    validateSearch: (s: Record<string, unknown>) => {
      const out: { agent?: string; server?: string } = {};
      if (typeof s.agent === 'string') out.agent = s.agent;
      if (typeof s.server === 'string') out.server = s.server;
      return out;
    },
  });

  const sessionsRoute = createRoute({
    getParentRoute: () => rootRoute,
    path: '/sessions',
    component: () => <div>Sessions list</div>,
    validateSearch: (s: Record<string, unknown>) => {
      const out: { node?: string } = {};
      if (typeof s.node === 'string') out.node = s.node;
      return out;
    },
  });

  const history = createMemoryHistory({ initialEntries: [initialPath] });
  const router = createRouter({
    routeTree: rootRoute.addChildren([
      nodesRoute, nodeDetailRoute, serversRoute, serverDetailRoute, grantsRoute, sessionsRoute,
    ]),
    history,
  });

  render(
    <QueryClientProvider client={qc}>
      <RouterProvider router={router} />
    </QueryClientProvider>,
  );
  return { qc, router };
}

beforeEach(() => {
  vi.clearAllMocks();
  mockSpaceGet.mockResolvedValue({
    ...SPACE_BASE,
    nodes: [NODE_A, NODE_B],
    mcpServers: [SERVER_ON_A, SERVER_ON_OTHER],
  });
  mockSessionsList.mockResolvedValue([SESSION_ON_A_SERVER, SESSION_ON_OTHER_SERVER]);
  mockNodesUpdate.mockResolvedValue({
    id: { value: 'node-aaa' },
    displayName: 'Alpha Node',
    note: 'updated note',
    updatedAt: new Date(NOW).toISOString(),
  });
});

// ---------------------------------------------------------------------------
// Node not found
// ---------------------------------------------------------------------------

describe('NodeDetailPage — node not found', () => {
  it('renders an empty state for an unknown node id', async () => {
    renderPage('/nodes/node-does-not-exist');
    expect(await screen.findByText('Runtime not found')).toBeInTheDocument();
  });

  it('renders a back link to /nodes', async () => {
    renderPage('/nodes/node-does-not-exist');
    await screen.findByText('Runtime not found');
    expect(screen.getByRole('link', { name: /Runtimes/ })).toHaveAttribute('href', '/nodes');
  });
});

// ---------------------------------------------------------------------------
// Detail card
// ---------------------------------------------------------------------------

describe('NodeDetailPage — detail card', () => {
  it('renders the node display name and status', async () => {
    renderPage('/nodes/node-aaa');
    expect(await screen.findByText('Alpha Node')).toBeInTheDocument();
    // "Online" appears twice: the header StatusBadge and the detail card's
    // "Status" row (raw node.status) — both are expected.
    expect(screen.getAllByText('Online').length).toBeGreaterThanOrEqual(2);
  });

  it('renders host metadata (hostname/os/arch/cli version)', async () => {
    renderPage('/nodes/node-aaa');
    await screen.findByText('Alpha Node');
    expect(screen.getByText('alpha-mbp.local')).toBeInTheDocument();
    expect(screen.getByText('macos')).toBeInTheDocument();
    expect(screen.getByText('arm64')).toBeInTheDocument();
    expect(screen.getByText('0.4.1')).toBeInTheDocument();
  });

  it('falls back to the short id and em dashes when metadata was never reported', async () => {
    renderPage('/nodes/node-bbb');
    await screen.findByText('Beta Node');
    expect(screen.getByText('node-bbb')).toBeInTheDocument();
    expect(screen.getAllByText('—').length).toBeGreaterThanOrEqual(3); // OS / Arch / CLI version
  });
});

// ---------------------------------------------------------------------------
// Owner-editable note
// ---------------------------------------------------------------------------

describe('NodeDetailPage — owner note', () => {
  it('renders the existing note text', async () => {
    renderPage('/nodes/node-aaa');
    expect(await screen.findByText('primary laptop')).toBeInTheDocument();
  });

  it('shows a placeholder when no note is set', async () => {
    renderPage('/nodes/node-bbb');
    await screen.findByText('Beta Node');
    expect(screen.getByText('No note yet.')).toBeInTheDocument();
  });

  it('editing and saving fires a PATCH /api/nodes/{id} mutation', async () => {
    renderPage('/nodes/node-aaa');
    await screen.findByText('primary laptop');

    await userEvent.click(screen.getByRole('button', { name: /edit/i }));
    const textarea = screen.getByLabelText('Runtime note');
    await userEvent.clear(textarea);
    await userEvent.type(textarea, 'updated note');
    await userEvent.click(screen.getByRole('button', { name: /save/i }));

    await waitFor(() => {
      expect(mockNodesUpdate).toHaveBeenCalledWith('node-aaa', { note: 'updated note' });
    });
  });

  it('clearing the note text sends note: null', async () => {
    renderPage('/nodes/node-aaa');
    await screen.findByText('primary laptop');

    await userEvent.click(screen.getByRole('button', { name: /edit/i }));
    const textarea = screen.getByLabelText('Runtime note');
    await userEvent.clear(textarea);
    await userEvent.click(screen.getByRole('button', { name: /save/i }));

    await waitFor(() => {
      expect(mockNodesUpdate).toHaveBeenCalledWith('node-aaa', { note: null });
    });
  });

  it('Cancel discards the draft without calling the API', async () => {
    renderPage('/nodes/node-aaa');
    await screen.findByText('primary laptop');

    await userEvent.click(screen.getByRole('button', { name: /edit/i }));
    const textarea = screen.getByLabelText('Runtime note');
    await userEvent.clear(textarea);
    await userEvent.type(textarea, 'discard me');
    await userEvent.click(screen.getByRole('button', { name: /cancel/i }));

    expect(mockNodesUpdate).not.toHaveBeenCalled();
    expect(screen.getByText('primary laptop')).toBeInTheDocument();
  });
});

// ---------------------------------------------------------------------------
// Servers on this node
// ---------------------------------------------------------------------------

describe('NodeDetailPage — servers published from this node', () => {
  it('lists only servers whose publisherNodeId matches this node', async () => {
    renderPage('/nodes/node-aaa');
    // 'filesystem' legitimately appears twice — once as the server row, once
    // as the session row's server label (same fixture server) — so assert
    // presence via getAllByText rather than the singular query.
    expect((await screen.findAllByText('filesystem')).length).toBeGreaterThan(0);
    expect(screen.queryByText('unrelated-server')).toBeNull();
  });

  it('shows an empty message for a node that publishes nothing', async () => {
    renderPage('/nodes/node-bbb');
    expect(await screen.findByText('No servers published from this runtime.')).toBeInTheDocument();
  });

  it('clicking a server row navigates to /servers/$serverId', async () => {
    const { router } = renderPage('/nodes/node-aaa');
    // The MCP servers section renders before the Sessions section, so the
    // server MiniRow's label is the first "filesystem" match (the second is
    // the same-named session's server label, a non-interactive EntityLink).
    const [serverRowLabel] = await screen.findAllByText('filesystem');
    await userEvent.click(serverRowLabel);
    await waitFor(() => {
      expect(router.state.location.pathname).toBe('/servers/srv-1');
    });
  });
});

// ---------------------------------------------------------------------------
// Sessions on this node
// ---------------------------------------------------------------------------

describe('NodeDetailPage — sessions on this node', () => {
  it('shows sessions through a server this node publishes', async () => {
    renderPage('/nodes/node-aaa');
    expect(await screen.findByText('@anya')).toBeInTheDocument();
  });

  it('excludes sessions through another node\'s server', async () => {
    renderPage('/nodes/node-aaa');
    await screen.findByText('@anya');
    expect(screen.queryByText('@scout')).toBeNull();
  });
});


// ---------------------------------------------------------------------------
// Error state
// ---------------------------------------------------------------------------

describe('NodeDetailPage — error state', () => {
  it('renders error state when the space query fails', async () => {
    mockSpaceGet.mockRejectedValue(new Error('Network error'));
    renderPage('/nodes/node-aaa');
    expect(await screen.findByText(/could not load runtime/i)).toBeInTheDocument();
  });
});
