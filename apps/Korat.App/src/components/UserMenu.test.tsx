/**
 * UserMenu tests (Task 9).
 *
 * Covers:
 *  - "Account" link renders and points to /account/profile.
 *  - "Sign out" action triggers useSignOut → queryClient.clear() + navigate /signin.
 *  - Sign out button is disabled while mutation is pending.
 *
 * Network is mocked at the api module boundary — tests run entirely in-process.
 * TanStack Router navigate is mocked so mutation hooks can call it; Link still
 * requires a RouterProvider (minimal in-memory router is provided per-test).
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
import type { ReactNode } from 'react';

// ---------------------------------------------------------------------------
// Mock api module so tests never hit the network
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
  },
}));

// Mock TanStack Router useNavigate so useSignOut can call it; Link and other
// router primitives come from the real module (bound to the in-memory router).
const mockNavigate = vi.fn();
vi.mock('@tanstack/react-router', async (importOriginal) => {
  const original = await importOriginal<typeof import('@tanstack/react-router')>();
  return { ...original, useNavigate: () => mockNavigate };
});

import { api } from '@/lib/api';
import { UserMenu } from '@/components/UserMenu';

// ---------------------------------------------------------------------------
// Test harness
// ---------------------------------------------------------------------------

function buildRouter() {
  const rootRoute = createRootRoute({
    component: () => (
      <div>
        <Outlet />
      </div>
    ),
  });
  // Mount UserMenu inside a route so Link has router context
  const homeRoute = createRoute({
    getParentRoute: () => rootRoute,
    path: '/',
    component: UserMenu,
  });
  // Stub /account/profile route so Link can resolve without warnings
  const accountProfileRoute = createRoute({
    getParentRoute: () => rootRoute,
    path: '/account/profile',
    component: () => <div>account</div>,
  });
  // Stub /signin route for navigation assertions
  const signinRoute = createRoute({
    getParentRoute: () => rootRoute,
    path: '/signin',
    component: () => <div>sign in</div>,
  });

  const history = createMemoryHistory({ initialEntries: ['/'] });
  const router = createRouter({
    routeTree: rootRoute.addChildren([homeRoute, accountProfileRoute, signinRoute]),
    history,
  });
  return router;
}

function makeWrapper() {
  const qc = new QueryClient({
    defaultOptions: {
      queries: { retry: false, gcTime: 0 },
      mutations: { retry: false },
    },
  });
  const wrapper = ({ children }: { children: ReactNode }) => (
    <QueryClientProvider client={qc}>{children}</QueryClientProvider>
  );
  return { qc, wrapper };
}

/** Render UserMenu inside a minimal RouterProvider + QueryClientProvider. */
function renderUserMenu(qc: QueryClient) {
  const router = buildRouter();
  return render(
    <QueryClientProvider client={qc}>
      <RouterProvider router={router} />
    </QueryClientProvider>,
  );
}

beforeEach(() => {
  vi.clearAllMocks();
  mockNavigate.mockReset();
});

// ---------------------------------------------------------------------------
// UserMenu
// ---------------------------------------------------------------------------

describe('UserMenu', () => {
  it('renders an Account link that points to /account/profile', async () => {
    const { qc } = makeWrapper();
    renderUserMenu(qc);

    const link = await screen.findByRole('link', { name: /account/i });
    expect(link).toBeInTheDocument();
    expect(link).toHaveAttribute('href', expect.stringContaining('account/profile'));
  });

  it('renders a sign-out button', async () => {
    const { qc } = makeWrapper();
    renderUserMenu(qc);

    expect(await screen.findByRole('button', { name: /sign out/i })).toBeInTheDocument();
  });

  it('clicking sign out calls api.auth.signout, clears cache, and navigates to /signin', async () => {
    (api.auth.signout as ReturnType<typeof vi.fn>).mockResolvedValue(undefined);

    const user = userEvent.setup();
    const { qc } = makeWrapper();
    const clearSpy = vi.spyOn(qc, 'clear');

    renderUserMenu(qc);

    const btn = await screen.findByRole('button', { name: /sign out/i });
    await user.click(btn);

    await waitFor(() => {
      expect(api.auth.signout).toHaveBeenCalledOnce();
      expect(clearSpy).toHaveBeenCalled();
      expect(mockNavigate).toHaveBeenCalledWith({ to: '/signin' });
    });
  });

  it('disables sign out button while mutation is pending', async () => {
    let release: () => void = () => undefined;
    const inflight = new Promise<void>((resolve) => {
      release = resolve;
    });
    (api.auth.signout as ReturnType<typeof vi.fn>).mockReturnValue(inflight);

    const user = userEvent.setup();
    const { qc } = makeWrapper();
    renderUserMenu(qc);

    const btn = await screen.findByRole('button', { name: /sign out/i });
    await user.click(btn);

    await waitFor(() => expect(btn).toBeDisabled());
    release();
  });
});
