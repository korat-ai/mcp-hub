/**
 * Unit tests for the ConnectedAppsPage route component (Space-MCP inc-2a, Task 8):
 *  - lists consents (client name, space, created date)
 *  - revoke flow: row button → ConfirmDialog → api call
 *  - empty state / error state
 *
 * Network is mocked at the api module boundary (mirrors grants.test.tsx's harness).
 */
import { describe, expect, it, vi, beforeEach } from 'vitest';
import { render, screen, fireEvent, waitFor } from '@testing-library/react';
import {
  createMemoryHistory, createRouter, createRootRoute, createRoute, RouterProvider, Outlet,
} from '@tanstack/react-router';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';

const mockConsentsList = vi.fn();
const mockConsentsRevoke = vi.fn();

vi.mock('@/lib/api', () => ({
  api: {
    oauthConsents: {
      list: (...args: unknown[]) => mockConsentsList(...args),
      revoke: (...args: unknown[]) => mockConsentsRevoke(...args),
    },
    auth: { me: vi.fn() },
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

vi.mock('@/lib/toast', () => ({ toastReceipt: vi.fn() }));

const CONSENT_1 = {
  id: 'authz-001',
  clientId: 'korat-mcp',
  clientDisplayName: 'Korat MCP client',
  spaceId: 'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa',
  spaceName: 'My Space',
  createdAt: '2026-07-11T00:00:00Z',
};

function makeQC() {
  return new QueryClient({
    defaultOptions: {
      queries: { retry: false, gcTime: 0 },
      mutations: { retry: false },
    },
  });
}

async function renderPage() {
  const { ConnectedAppsPage } = await import('./connected-apps');
  const rootRoute = createRootRoute({ component: () => <Outlet /> });
  const route = createRoute({ getParentRoute: () => rootRoute, path: '/connected-apps', component: ConnectedAppsPage });
  const router = createRouter({
    routeTree: rootRoute.addChildren([route]),
    history: createMemoryHistory({ initialEntries: ['/connected-apps'] }),
  });
  const qc = makeQC();
  render(
    <QueryClientProvider client={qc}>
      <RouterProvider router={router} />
    </QueryClientProvider>,
  );
}

describe('ConnectedAppsPage', () => {
  beforeEach(() => {
    vi.clearAllMocks();
  });

  it('lists consents', async () => {
    mockConsentsList.mockResolvedValue([CONSENT_1]);
    await renderPage();
    expect(await screen.findByText('Korat MCP client')).toBeInTheDocument();
  });

  it('revokes via confirm dialog', async () => {
    mockConsentsList.mockResolvedValue([CONSENT_1]);
    mockConsentsRevoke.mockResolvedValue(undefined);
    await renderPage();
    fireEvent.click(await screen.findByRole('button', { name: /revoke/i }));
    fireEvent.click(await screen.findByRole('button', { name: /confirm|revoke access/i }));
    await waitFor(() => expect(mockConsentsRevoke).toHaveBeenCalledWith('authz-001'));
  });

  it('shows empty state', async () => {
    mockConsentsList.mockResolvedValue([]);
    await renderPage();
    expect(await screen.findByText(/no connected apps/i)).toBeInTheDocument();
  });

  it('shows error state', async () => {
    mockConsentsList.mockRejectedValue(new Error('boom'));
    await renderPage();
    expect(await screen.findByText(/could not load connected apps/i)).toBeInTheDocument();
  });
});
