/**
 * Unit tests for the GrantsPage route component.
 *
 * Covers:
 *  - Unfiltered: all grants rendered.
 *  - ?server=<id> filters to matching grants only.
 *  - ?agent=<id> filters to matching grants only.
 *  - ActiveFilterChip appears when filter active; chip label resolves name.
 *  - Chip clear navigates to /grants without the filter param.
 *  - Agent cell self-filters /grants?agent=<consumerId> (when agentName present) —
 *    there is no dedicated agent detail page, so this remains a filtered-view link.
 *  - Server cell links to the server's own detail page /servers/$serverId (when
 *    serverName present) — parity: an entity name opens its own detail page.
 *  - Agent/Server cells are tooltip-only when name is absent.
 *  - Filtered-to-empty shows scoped empty message with chip still visible.
 *  - Filtered-to-empty hint copy is scoped per active filter (-3 parity).
 *  - Revoke flow: inline row button → ConfirmDialog → mutation.isPending busy state.
 *  - Empty state when no grants at all.
 *  - Error state when grants query fails.
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

const mockGrantsList = vi.fn();
const mockGrantsRevoke = vi.fn();

vi.mock('@/lib/api', () => ({
  api: {
    grants: {
      list: (...args: unknown[]) => mockGrantsList(...args),
      revoke: (...args: unknown[]) => mockGrantsRevoke(...args),
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

// Stub toast so it doesn't blow up in tests.
vi.mock('@/lib/toast', () => ({
  toastReceipt: vi.fn(),
}));

// ---------------------------------------------------------------------------
// Fixtures
// ---------------------------------------------------------------------------

const GRANT_1 = {
  id: 'grant-001',
  consumerId: 'agent-aaa',
  mcpServerId: 'srv-111',
  agentName: 'Claude Agent',
  serverName: 'Auth Server',
  status: 'Active' as const,
  approvedAt: '2026-06-01T00:00:00Z',
  revokedAt: null,
};

const GRANT_2 = {
  id: 'grant-002',
  consumerId: 'agent-bbb',
  mcpServerId: 'srv-222',
  agentName: 'GPT Bridge',
  serverName: 'Data Server',
  status: 'Active' as const,
  approvedAt: '2026-06-02T00:00:00Z',
  revokedAt: null,
};

const GRANT_NO_NAMES = {
  id: 'grant-003',
  consumerId: 'agent-ccc',
  mcpServerId: 'srv-333',
  agentName: undefined,
  serverName: undefined,
  status: 'Revoked' as const,
  approvedAt: '2026-06-03T00:00:00Z',
  revokedAt: '2026-06-04T00:00:00Z',
};

// ---------------------------------------------------------------------------
// Helpers
// ---------------------------------------------------------------------------

import { Route as GrantsFileRoute } from './grants';

function makeQC() {
  return new QueryClient({
    defaultOptions: {
      queries: { retry: false, gcTime: 0 },
      mutations: { retry: false },
    },
  });
}

function renderPage(initialPath = '/grants') {
  const qc = makeQC();
  const rootRoute = createRootRoute({ component: () => <Outlet /> });

  // Build /grants route using the real component and validateSearch. The Agent cell
  // still self-filters this same route (-3 parity, no dedicated agent detail page);
  // the Server cell now links out to /servers/$serverId, so register that as a
  // sibling route for the link to resolve (mirrors sessions.test.tsx).
  const grantsRoute = createRoute({
    getParentRoute: () => rootRoute,
    path: '/grants',
    // eslint-disable-next-line @typescript-eslint/no-explicit-any
    component: GrantsFileRoute.options.component as any,
    validateSearch: GrantsFileRoute.options.validateSearch,
  });

  const serverDetailRoute = createRoute({
    getParentRoute: () => rootRoute,
    path: '/servers/$serverId',
    component: () => <div>Server detail page</div>,
  });

  const history = createMemoryHistory({ initialEntries: [initialPath] });
  const router = createRouter({
    routeTree: rootRoute.addChildren([grantsRoute, serverDetailRoute]),
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
  mockGrantsList.mockResolvedValue([GRANT_1, GRANT_2]);
  mockGrantsRevoke.mockResolvedValue(undefined);
});

// ---------------------------------------------------------------------------
// Unfiltered list
// ---------------------------------------------------------------------------

describe('GrantsPage — unfiltered list', () => {
  it('renders all grants when no filter param', async () => {
    renderPage();
    expect(await screen.findByText('Claude Agent')).toBeInTheDocument();
    expect(screen.getByText('GPT Bridge')).toBeInTheDocument();
  });

  it('does not show the filter chip when no filter active', async () => {
    renderPage();
    await screen.findByText('Claude Agent');
    expect(screen.queryByTestId('active-filter-chip')).toBeNull();
  });

  it('renders server names for all grants', async () => {
    renderPage();
    await screen.findByText('Auth Server');
    expect(screen.getByText('Data Server')).toBeInTheDocument();
  });
});

// ---------------------------------------------------------------------------
// Server filter (?server=)
// ---------------------------------------------------------------------------

describe('GrantsPage — ?server= filter', () => {
  it('shows only grants whose mcpServerId matches the filter', async () => {
    renderPage('/grants?server=srv-111');
    expect(await screen.findByText('Claude Agent')).toBeInTheDocument();
    expect(screen.queryByText('GPT Bridge')).toBeNull();
  });

  it('renders ActiveFilterChip when server param is active', async () => {
    renderPage('/grants?server=srv-111');
    await screen.findByText('Claude Agent');
    expect(screen.getByTestId('active-filter-chip')).toBeInTheDocument();
  });

  it('chip label resolves serverName from first matching grant', async () => {
    renderPage('/grants?server=srv-111');
    await screen.findByText('Claude Agent');
    const chip = screen.getByTestId('active-filter-chip');
    expect(chip).toHaveTextContent('Server');
    expect(chip).toHaveTextContent('Auth Server');
  });

  it('clears filter when chip clear button is clicked', async () => {
    renderPage('/grants?server=srv-111');
    await screen.findByText('Claude Agent');
    const clearBtn = screen.getByRole('button', { name: /clear filter/i });
    fireEvent.click(clearBtn);
    // After clearing, both grants should be visible
    await waitFor(() => {
      expect(screen.getByText('GPT Bridge')).toBeInTheDocument();
    });
    expect(screen.queryByTestId('active-filter-chip')).toBeNull();
  });
});

// ---------------------------------------------------------------------------
// Agent filter (?agent=)
// ---------------------------------------------------------------------------

describe('GrantsPage — ?agent= filter', () => {
  it('shows only grants whose consumerId matches the filter', async () => {
    renderPage('/grants?agent=agent-aaa');
    // Wait for chip to appear (means data has loaded and filter is active)
    await waitFor(() => {
      expect(screen.getByTestId('active-filter-chip')).toBeInTheDocument();
    });
    // GPT Bridge should not be visible in the table
    expect(screen.queryByRole('link', { name: 'GPT Bridge' })).toBeNull();
  });

  it('renders ActiveFilterChip when agent param is active', async () => {
    renderPage('/grants?agent=agent-aaa');
    await waitFor(() => {
      expect(screen.getByTestId('active-filter-chip')).toBeInTheDocument();
    });
  });

  it('chip label resolves agentName from first matching grant', async () => {
    renderPage('/grants?agent=agent-aaa');
    const chip = await screen.findByTestId('active-filter-chip');
    expect(chip).toHaveTextContent('Claude Agent');
  });
});

// ---------------------------------------------------------------------------
// Outgoing links: agent cell → self-filters /grants?agent=<consumerId>
// ---------------------------------------------------------------------------

describe('GrantsPage — agent cell self-filters /grants?agent=', () => {
  it('agent name cell is a link when agentName is present', async () => {
    renderPage();
    await screen.findByText('Claude Agent');
    const link = screen.getByRole('link', { name: 'Claude Agent' });
    expect(link).toBeInTheDocument();
  });

  it('agent cell link points to /grants with the consumerId param', async () => {
    renderPage();
    await screen.findByText('Claude Agent');
    const link = screen.getByRole('link', { name: 'Claude Agent' });
    const href = link.getAttribute('href') ?? '';
    expect(href).toContain('/grants');
    expect(href).toContain('agent=agent-aaa');
  });
});

// ---------------------------------------------------------------------------
// Outgoing links: server cell → the server's own detail page /servers/$serverId
// ---------------------------------------------------------------------------

describe('GrantsPage — server cell links to /servers/$serverId', () => {
  it('server name cell is a link when serverName is present', async () => {
    renderPage();
    await screen.findByText('Auth Server');
    const link = screen.getByRole('link', { name: 'Auth Server' });
    expect(link).toBeInTheDocument();
  });

  it('server cell link points to the server\'s own detail page (not a /grants filter)', async () => {
    renderPage();
    await screen.findByText('Auth Server');
    const link = screen.getByRole('link', { name: 'Auth Server' });
    const href = link.getAttribute('href') ?? '';
    expect(href).toBe('/servers/srv-111');
  });
});

// ---------------------------------------------------------------------------
// Tooltip-only when name is absent
// ---------------------------------------------------------------------------

describe('GrantsPage — tooltip-only cells when name absent', () => {
  beforeEach(() => {
    mockGrantsList.mockResolvedValue([GRANT_NO_NAMES]);
  });

  it('agent cell renders no link when agentName is undefined', async () => {
    renderPage();
    // Wait for data to load (revoked badge appears)
    await screen.findByText('Revoked');
    // Access-section tabs are links; the data row itself must keep unnamed entities non-linking.
    const row = screen.getByText('Revoked').closest('tr');
    expect(row).not.toBeNull();
    expect(within(row!).queryByRole('link')).toBeNull();
  });
});

// ---------------------------------------------------------------------------
// Filtered-to-empty
// ---------------------------------------------------------------------------

describe('GrantsPage — filtered empty state', () => {
  it('shows scoped empty message when filter matches no grants', async () => {
    renderPage('/grants?server=srv-zzz');
    await waitFor(() => {
      expect(screen.getByTestId('active-filter-chip')).toBeInTheDocument();
    });
    expect(screen.getByText(/no permissions match this filter/i)).toBeInTheDocument();
  });

  it('scopes the hint copy to the server filter (-3 parity)', async () => {
    renderPage('/grants?server=srv-zzz');
    await waitFor(() => {
      expect(screen.getByTestId('active-filter-chip')).toBeInTheDocument();
    });
    expect(screen.getByText(/no consumer has access to .* yet\./i)).toBeInTheDocument();
  });

  it('scopes the hint copy to the agent filter (-3 parity)', async () => {
    renderPage('/grants?agent=agent-zzz');
    await waitFor(() => {
      expect(screen.getByTestId('active-filter-chip')).toBeInTheDocument();
    });
    expect(screen.getByText(/no permissions held by .* yet\./i)).toBeInTheDocument();
  });

  it('chip is still visible when filtered to empty', async () => {
    renderPage('/grants?agent=agent-zzz');
    await waitFor(() => {
      expect(screen.getByTestId('active-filter-chip')).toBeInTheDocument();
    });
  });
});

// ---------------------------------------------------------------------------
// Revoke flow: inline button → ConfirmDialog → mutation.isPending busy state
// ---------------------------------------------------------------------------

describe('GrantsPage — revoke flow', () => {
  it('opens a confirm dialog naming the agent and server', async () => {
    renderPage();
    await screen.findByText('Claude Agent');
    const revokeButtons = screen.getAllByRole('button', { name: /revoke/i });
    fireEvent.click(revokeButtons[0]);
    expect(
      await screen.findByText("Revoke Claude Agent's access to Auth Server?"),
    ).toBeInTheDocument();
  });

  it('surfaces the inline busy state on the row once the dialog is dismissed mid-flight', async () => {
    // ConfirmDialog documents Cancel as an abort *signal*, not real cancellation — the
    // mutation keeps running in the background. This proves the row's busy state is wired
    // to mutation.isPending independently of the dialog's own (masked-while-open) pending UI.
    let resolveRevoke: () => void = () => {};
    mockGrantsRevoke.mockImplementation(
      () => new Promise<void>((resolve) => { resolveRevoke = resolve; }),
    );
    renderPage();
    await screen.findByText('Claude Agent');
    fireEvent.click(screen.getAllByRole('button', { name: /revoke/i })[0]);
    const dialog = await screen.findByRole('dialog');
    fireEvent.click(within(dialog).getByRole('button', { name: 'Revoke' }));

    // Cancel closes the dialog while the mutation is still in flight.
    fireEvent.click(within(dialog).getByRole('button', { name: 'Cancel' }));

    // The row's inline Revoke button (now unmasked) goes busy ('…') and disables.
    await waitFor(() => {
      const rowButton = screen.getByRole('button', { name: '…' });
      expect(rowButton).toBeDisabled();
    });

    resolveRevoke();
    await waitFor(() => {
      expect(screen.queryByRole('button', { name: '…' })).toBeNull();
    });
  });
});

// ---------------------------------------------------------------------------
// Global empty state
// ---------------------------------------------------------------------------

describe('GrantsPage — global empty state', () => {
  it('renders empty state when no grants at all', async () => {
    mockGrantsList.mockResolvedValue([]);
    renderPage();
    expect(await screen.findByText('No permissions yet')).toBeInTheDocument();
  });
});

// ---------------------------------------------------------------------------
// Error state
// ---------------------------------------------------------------------------

describe('GrantsPage — error state', () => {
  it('renders error state when grants query fails', async () => {
    mockGrantsList.mockRejectedValue(new Error('Network error'));
    renderPage();
    expect(await screen.findByText(/could not load permissions/i)).toBeInTheDocument();
  });
});
