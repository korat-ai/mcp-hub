/**
 * Unit tests for the SessionsPage route component.
 *
 * Covers:
 *  - Renders all sessions unfiltered.
 *  - Filters by agentName search param.
 *  - Filters by serverName search param.
 *  - Filters by node search param (id-based, -3 parity).
 *  - ActiveFilterChip shows verbatim name and fires clear.
 *  - Stacked "Session" cell: id over an agent · server · node breadcrumb, each an
 *    EntityLink cross-navigating to /grants?agent= (agent, no detail entity),
 *    /servers/$serverId (server's own detail), /nodes/$name (node's own detail).
 *  - Filtered-to-empty shows scoped empty message + chip.
 *  - Empty state when no sessions at all.
 *  - Error state.
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
// Mock api module
// ---------------------------------------------------------------------------

const { mockSessionsList } = vi.hoisted(() => ({
  mockSessionsList: vi.fn(),
}));

vi.mock('@/lib/api', () => ({
  api: {
    sessions: {
      list: (...args: unknown[]) => mockSessionsList(...args),
    },
    auth: { me: vi.fn() },
    space: { get: vi.fn() },
    cli: { tokens: { list: vi.fn() } },
  },
  ApiError: class ApiError extends Error {
    constructor(public status: number, public body: string) {
      super(`HTTP ${status}`);
    }
  },
  getIdValue: (id: unknown) =>
    id !== null && typeof id === 'object' && 'value' in id
      ? String((id as { value: unknown }).value)
      : String(id),
}));

// ---------------------------------------------------------------------------
// Fixtures
// ---------------------------------------------------------------------------

const SESSION_ALPHA = {
  id: { value: 'session-aaa-111' },
  consumerId: 'agent-aaa',
  agentName: 'claude-flagship',
  mcpServerId: 'srv-111',
  serverName: 'My MCP Server',
  publisherNodeId: 'node-aaa',
  publisherNodeName: 'work-laptop',
  status: 'Active' as const,
  effectiveStatus: 'Active' as const,
  startedAt: '2026-06-15T10:00:00Z',
  endedAt: null,
  bytesClientToServer: 1024,
  bytesServerToClient: 2048,
  closeReason: null,
};

const SESSION_BETA = {
  id: { value: 'session-bbb-222' },
  consumerId: 'agent-bbb',
  agentName: 'gpt-bridge',
  mcpServerId: 'srv-222',
  serverName: 'Other Server',
  publisherNodeId: 'node-bbb',
  publisherNodeName: 'studio-runtime',
  status: 'Closed' as const,
  effectiveStatus: 'Closed' as const,
  startedAt: '2026-06-15T09:00:00Z',
  endedAt: '2026-06-15T09:30:00Z',
  bytesClientToServer: 512,
  bytesServerToClient: 256,
  closeReason: 'Completed' as const,
};

// ---------------------------------------------------------------------------
// Helpers
// ---------------------------------------------------------------------------

import { SessionsPage } from './sessions';

function makeQC() {
  return new QueryClient({
    defaultOptions: {
      queries: { retry: false, gcTime: 0 },
      mutations: { retry: false },
    },
  });
}

function renderPage(initialEntry = '/sessions') {
  const qc = makeQC();
  const rootRoute = createRootRoute({ component: () => <Outlet /> });
  const sessionsRoute = createRoute({
    getParentRoute: () => rootRoute,
    path: '/sessions',
    component: SessionsPage,
    validateSearch: (
      s: Record<string, unknown>,
    ): { agentName?: string; serverName?: string; node?: string } => {
      const out: { agentName?: string; serverName?: string; node?: string } = {};
      if (typeof s.agentName === 'string') out.agentName = s.agentName;
      if (typeof s.serverName === 'string') out.serverName = s.serverName;
      if (typeof s.node === 'string') out.node = s.node;
      return out;
    },
  });
  // Sibling routes so the breadcrumb cross-nav links (Agent → /grants,
  // Server → /servers/$serverId, Node → /nodes/$name) resolve within the test
  // router (mirrors grants.test.tsx / nodes.$name.test.tsx).
  const grantsRoute = createRoute({
    getParentRoute: () => rootRoute,
    path: '/grants',
    component: () => <div>Grants page</div>,
    validateSearch: (s: Record<string, unknown>): { agent?: string } => {
      const out: { agent?: string } = {};
      if (typeof s.agent === 'string') out.agent = s.agent;
      return out;
    },
  });
  const serverDetailRoute = createRoute({
    getParentRoute: () => rootRoute,
    path: '/servers/$serverId',
    component: () => <div>Server detail page</div>,
  });
  const nodeDetailRoute = createRoute({
    getParentRoute: () => rootRoute,
    path: '/nodes/$name',
    component: () => <div>Node detail page</div>,
  });
  const history = createMemoryHistory({ initialEntries: [initialEntry] });
  const router = createRouter({
    routeTree: rootRoute.addChildren([sessionsRoute, grantsRoute, serverDetailRoute, nodeDetailRoute]),
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
  mockSessionsList.mockResolvedValue([]);
});

describe('SessionsPage — empty state', () => {
  it('renders empty state when no sessions', async () => {
    renderPage();
    expect(await screen.findByText('No sessions yet')).toBeInTheDocument();
  });
});

describe('SessionsPage — unfiltered list', () => {
  beforeEach(() => {
    mockSessionsList.mockResolvedValue([SESSION_ALPHA, SESSION_BETA]);
  });

  it('renders all sessions when no filter param', async () => {
    renderPage('/sessions');
    expect(await screen.findByText('claude-flagship')).toBeInTheDocument();
    expect(screen.getByText('gpt-bridge')).toBeInTheDocument();
  });

  it('does not show filter chip when no filter is active', async () => {
    renderPage('/sessions');
    await screen.findByText('claude-flagship');
    expect(screen.queryByTestId('active-filter-chip')).toBeNull();
  });
});

describe('SessionsPage — agentName filter', () => {
  beforeEach(() => {
    mockSessionsList.mockResolvedValue([SESSION_ALPHA, SESSION_BETA]);
  });

  it('filters sessions by agentName search param', async () => {
    renderPage('/sessions?agentName=claude-flagship');
    // 'claude-flagship' appears in both the chip and the table cell — use findAllByText
    expect(await screen.findAllByText('claude-flagship')).not.toHaveLength(0);
    expect(screen.queryByText('gpt-bridge')).toBeNull();
  });

  it('shows active-filter chip with verbatim agentName label', async () => {
    renderPage('/sessions?agentName=claude-flagship');
    expect(await screen.findByTestId('active-filter-chip')).toBeInTheDocument();
    // chip label appears inside the chip element
    const chip = screen.getByTestId('active-filter-chip');
    expect(chip).toHaveTextContent('claude-flagship');
  });

  it('chip has a clear button', async () => {
    renderPage('/sessions?agentName=claude-flagship');
    await screen.findByTestId('active-filter-chip');
    expect(screen.getByRole('button', { name: 'Clear filter' })).toBeInTheDocument();
  });
});

describe('SessionsPage — serverName filter', () => {
  beforeEach(() => {
    mockSessionsList.mockResolvedValue([SESSION_ALPHA, SESSION_BETA]);
  });

  it('filters sessions by serverName search param', async () => {
    renderPage('/sessions?serverName=My%20MCP%20Server');
    // 'My MCP Server' appears in both the chip and the table cell — use findAllByText
    expect(await screen.findAllByText('My MCP Server')).not.toHaveLength(0);
    expect(screen.queryByText('Other Server')).toBeNull();
  });

  it('shows active-filter chip with verbatim serverName label', async () => {
    renderPage('/sessions?serverName=My%20MCP%20Server');
    expect(await screen.findByTestId('active-filter-chip')).toBeInTheDocument();
    const chip = screen.getByTestId('active-filter-chip');
    expect(chip).toHaveTextContent('My MCP Server');
  });
});

describe('SessionsPage — node filter (id-based, -3 parity)', () => {
  beforeEach(() => {
    mockSessionsList.mockResolvedValue([SESSION_ALPHA, SESSION_BETA]);
  });

  it('filters sessions by the publisherNodeId search param', async () => {
    renderPage('/sessions?node=node-aaa');
    expect(await screen.findByText('claude-flagship')).toBeInTheDocument();
    expect(screen.queryByText('gpt-bridge')).toBeNull();
  });

  it('resolves the chip label from the matching session\'s publisherNodeName', async () => {
    renderPage('/sessions?node=node-aaa');
    const chip = await screen.findByTestId('active-filter-chip');
    expect(chip).toHaveTextContent('work-laptop');
  });
});

describe('SessionsPage — stacked Session cell breadcrumb cross-nav', () => {
  beforeEach(() => {
    mockSessionsList.mockResolvedValue([SESSION_ALPHA]);
  });

  it('agent breadcrumb links to /grants with the consumerId param', async () => {
    renderPage('/sessions');
    await screen.findByText('claude-flagship');
    const link = screen.getByRole('link', { name: 'claude-flagship' });
    const href = link.getAttribute('href') ?? '';
    expect(href).toContain('/grants');
    expect(href).toContain('agent=agent-aaa');
  });

  it('server breadcrumb links to the server\'s own detail page (/servers/$serverId)', async () => {
    renderPage('/sessions');
    await screen.findByText('My MCP Server');
    const link = screen.getByRole('link', { name: 'My MCP Server' });
    const href = link.getAttribute('href') ?? '';
    expect(href).toBe('/servers/srv-111');
  });

  it('node breadcrumb links to the node\'s own detail page (/nodes/$name)', async () => {
    renderPage('/sessions');
    await screen.findByText('work-laptop');
    const link = screen.getByRole('link', { name: 'work-laptop' });
    const href = link.getAttribute('href') ?? '';
    expect(href).toBe('/nodes/node-aaa');
  });

  it('renders the short session id above the breadcrumb, in the same cell', async () => {
    renderPage('/sessions');
    const agentLink = await screen.findByText('claude-flagship');
    // shortId(session-aaa-111) truncates to the first 12 chars — assert it's stacked in
    // the same "Session" cell as the breadcrumb (-3 parity: one column, not three).
    const cell = agentLink.closest('td');
    expect(cell?.textContent).toContain('session-aaa-');
  });
});

describe('SessionsPage — filtered-to-empty', () => {
  beforeEach(() => {
    mockSessionsList.mockResolvedValue([SESSION_ALPHA]);
  });

  it('shows scoped empty message when filter matches nothing', async () => {
    renderPage('/sessions?agentName=nonexistent-agent');
    expect(await screen.findByText('No sessions match this filter')).toBeInTheDocument();
  });

  it('chip is still visible when filtered to empty', async () => {
    renderPage('/sessions?agentName=nonexistent-agent');
    await screen.findByText('No sessions match this filter');
    expect(screen.getByTestId('active-filter-chip')).toBeInTheDocument();
  });
});

describe('SessionsPage — error state', () => {
  it('renders error state when sessions query fails', async () => {
    mockSessionsList.mockRejectedValue(new Error('Network error'));
    renderPage();
    expect(await screen.findByText('Could not load sessions')).toBeInTheDocument();
  });

  it('shows retry button in error state', async () => {
    mockSessionsList.mockRejectedValue(new Error('Network error'));
    renderPage();
    await screen.findByText('Could not load sessions');
    expect(screen.getByRole('button', { name: /retry/i })).toBeInTheDocument();
  });
});

describe('SessionsPage — chip clear navigation', () => {
  beforeEach(() => {
    mockSessionsList.mockResolvedValue([SESSION_ALPHA, SESSION_BETA]);
  });

  it('clicking clear removes the filter and shows all sessions', async () => {
    renderPage('/sessions?agentName=claude-flagship');
    await screen.findByTestId('active-filter-chip');
    const clearBtn = screen.getByRole('button', { name: 'Clear filter' });
    fireEvent.click(clearBtn);
    // After clearing, both sessions should be visible
    await waitFor(() => {
      expect(screen.queryByTestId('active-filter-chip')).toBeNull();
    });
    expect(await screen.findByText('claude-flagship')).toBeInTheDocument();
    expect(screen.getByText('gpt-bridge')).toBeInTheDocument();
  });
});
