/**
 * RootLayout sign-in route tests.
 *
 * Verifies that auth-only chrome (Account link, Sign out button, AuthBanner,
 * SyncIndicator) is hidden on the /signin route whether or not the user is
 * authenticated, and that it IS visible on authenticated routes.
 *
 * Approach: build a minimal in-memory router that mounts the real RootLayout
 * as the root component. Mock the api module and spaceQueryOptions so
 * /api/space never fires a real fetch.
 */
import { describe, expect, it, vi, beforeEach } from 'vitest';
import { render, screen } from '@testing-library/react';
import {
  createMemoryHistory,
  createRouter,
  createRootRoute,
  createRoute,
  RouterProvider,
} from '@tanstack/react-router';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';

// ---------------------------------------------------------------------------
// Mock api module — tests never hit the network
// ---------------------------------------------------------------------------
vi.mock('@/lib/api', () => ({
  api: {
    auth: {
      me: vi.fn(),
      updateMe: vi.fn(),
      sessions: { list: vi.fn(), revoke: vi.fn() },
      email: { change: vi.fn(), confirm: vi.fn() },
      signout: vi.fn(),
    },
    cli: { tokens: { list: vi.fn(), revoke: vi.fn() } },
    space: { get: vi.fn() },
  },
  ApiError: class ApiError extends Error {
    constructor(
      public status: number,
      public body: string,
    ) {
      super(`HTTP ${status}${body ? `: ${body}` : ''}`);
      this.name = 'ApiError';
    }
  },
}));

// Mock spaceQueryOptions so AuthGate and AuthBanner never fire a real query
vi.mock('@/lib/queries/space', () => ({
  spaceQueryOptions: () => ({
    queryKey: ['space'],
    queryFn: () => new Promise(() => undefined), // stays pending — no 401 injected
    staleTime: Infinity,
    retry: false,
  }),
}));

// Mock TanStack Router useNavigate so mutation hooks don't throw outside router
const mockNavigate = vi.fn();
vi.mock('@tanstack/react-router', async (importOriginal) => {
  const original = await importOriginal<typeof import('@tanstack/react-router')>();
  return { ...original, useNavigate: () => mockNavigate };
});

import { ThemeProvider } from '@/components/layout/ThemeProvider';
import { RootLayout } from '@/routes/__root';

// ---------------------------------------------------------------------------
// Test harness
// ---------------------------------------------------------------------------

function makeQC() {
  return new QueryClient({
    defaultOptions: {
      queries: { retry: false, gcTime: 0 },
      mutations: { retry: false },
    },
  });
}

/**
 * Build a minimal router that uses RootLayout as root and navigates to
 * `initialPath`. The router always has stub routes for / and /signin so
 * Links don't warn about missing routes.
 */
function buildRouter(initialPath: string) {
  const rootRoute = createRootRoute({ component: RootLayout });
  const homeRoute = createRoute({
    getParentRoute: () => rootRoute,
    path: '/',
    component: () => <div data-testid="home-page">Home</div>,
  });
  const signinRoute = createRoute({
    getParentRoute: () => rootRoute,
    path: '/signin',
    component: () => <div data-testid="signin-page">Sign in</div>,
  });
  const accountRoute = createRoute({
    getParentRoute: () => rootRoute,
    path: '/account/profile',
    component: () => <div data-testid="account-page">Account</div>,
  });

  const history = createMemoryHistory({ initialEntries: [initialPath] });
  const router = createRouter({
    routeTree: rootRoute.addChildren([homeRoute, signinRoute, accountRoute]),
    history,
  });
  return router;
}

function renderAt(path: string) {
  const qc = makeQC();
  const router = buildRouter(path);
  render(
    <ThemeProvider>
      <QueryClientProvider client={qc}>
        <RouterProvider router={router} />
      </QueryClientProvider>
    </ThemeProvider>,
  );
  return { qc, router };
}

beforeEach(() => {
  vi.clearAllMocks();
  mockNavigate.mockReset();
});

// ---------------------------------------------------------------------------
// Tests
// ---------------------------------------------------------------------------

describe('RootLayout on /signin', () => {
  it('does NOT render the Account link', async () => {
    renderAt('/signin');
    // Wait for the page sentinel to be present (router settled)
    expect(await screen.findByTestId('signin-page')).toBeInTheDocument();
    expect(screen.queryByRole('link', { name: /account/i })).toBeNull();
  });

  it('does NOT render the Sign out button', async () => {
    renderAt('/signin');
    expect(await screen.findByTestId('signin-page')).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: /sign out/i })).toBeNull();
  });

  it('does NOT render the AuthBanner', async () => {
    renderAt('/signin');
    expect(await screen.findByTestId('signin-page')).toBeInTheDocument();
    // AuthBanner renders role="alert" with "Not authenticated." text
    expect(screen.queryByRole('alert')).toBeNull();
  });

  it('does NOT render the SyncIndicator', async () => {
    renderAt('/signin');
    expect(await screen.findByTestId('signin-page')).toBeInTheDocument();
    // SyncIndicator renders "syncing…" or "synced …" text
    expect(screen.queryByText(/syncing|synced/i)).toBeNull();
  });
});

describe('RootLayout on / (authenticated route)', () => {
  it('renders the Account link', async () => {
    renderAt('/');
    expect(await screen.findByTestId('home-page')).toBeInTheDocument();
    expect(screen.getByRole('link', { name: /account/i })).toBeInTheDocument();
  });

  it('renders the Sign out button', async () => {
    renderAt('/');
    expect(await screen.findByTestId('home-page')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: /sign out/i })).toBeInTheDocument();
  });

  it('renders the SyncIndicator', async () => {
    renderAt('/');
    expect(await screen.findByTestId('home-page')).toBeInTheDocument();
    // SyncIndicator always renders some sync text
    expect(screen.getByText(/syncing|synced/i)).toBeInTheDocument();
  });
});
