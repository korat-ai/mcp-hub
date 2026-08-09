/**
 * Account route tests (Task 8).
 *
 * Covers:
 *  - AccountLayout: section nav renders Profile/Security/CLI Tokens links; header
 *    shows user display name from useMe once data resolves.
 *  - AccountProfileRoute: renders ProfileSection (display name input).
 *  - AccountSecurityRoute: renders SecuritySection (email change form).
 *  - AccountCliRoute: renders CliTokensSection (heading).
 *  - VerifyEmailRoute (success): invalidates auth.me, shows confirmation, navigates
 *    away with replace: true.
 *  - VerifyEmailRoute (expired/invalid): shows recoverable error, never invalidates
 *    auth.me.
 *
 * AccountLayout uses TanStack Router hooks (useRouterState, Link) and therefore
 * requires a RouterProvider. The section-route components and VerifyEmailRoute
 * are rendered directly with a QueryClientProvider (no router needed).
 *
 * Network is mocked at the api module boundary (vi.mock) — no MSW/fetch calls.
 */
import { describe, expect, it, vi, beforeEach } from 'vitest';
import { render, screen, waitFor } from '@testing-library/react';
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
import type { MeDto } from '@/types/api';

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
    cli: {
      tokens: { list: vi.fn(), revoke: vi.fn() },
    },
    space: { get: vi.fn() },
  },
  ApiError: class ApiError extends Error {
    constructor(public status: number, public body: string) {
      super(`HTTP ${status}${body ? `: ${body}` : ''}`);
      this.name = 'ApiError';
    }
  },
}));

// Mock TanStack Router useNavigate — hooks.ts imports it at module level.
// useRouterState and Link still come from the real router bound in tests that
// use RouterProvider; useNavigate is replaced so mutation hooks can call it.
const mockNavigate = vi.fn();
vi.mock('@tanstack/react-router', async (importOriginal) => {
  const original = await importOriginal<typeof import('@tanstack/react-router')>();
  return { ...original, useNavigate: () => mockNavigate };
});

import { api, ApiError } from '@/lib/api';
import {
  AccountLayout,
  AccountProfileRoute,
  AccountSecurityRoute,
  AccountCliRoute,
} from '@/routes/account';
import { VerifyEmailRoute } from '@/routes/account.verify-email';

// ---------------------------------------------------------------------------
// Fixtures
// ---------------------------------------------------------------------------

const meData: MeDto = {
  userId: 'u1',
  displayName: 'Ada Lovelace',
  primaryEmail: 'ada@example.com',
  providers: [
    { provider: 'github', externalId: 'gh-123', linkedAt: '2024-01-01T00:00:00Z' },
  ],
  pendingEmailChange: null,
};

function makeQC() {
  return new QueryClient({
    defaultOptions: {
      queries: { retry: false, gcTime: 0 },
      mutations: { retry: false },
    },
  });
}

/** Minimal QueryClientProvider wrapper for section-route / VerifyEmailRoute tests. */
function QCWrapper({ qc, children }: { qc: QueryClient; children: ReactNode }) {
  return <QueryClientProvider client={qc}>{children}</QueryClientProvider>;
}

/**
 * Build a minimal in-memory router that mounts AccountLayout at /account.
 * Needed for tests that exercise Link and useRouterState inside AccountLayout.
 */
function buildAccountRouter(_qc: QueryClient, initialPath = '/account') {
  const rootRoute = createRootRoute({ component: () => <Outlet /> });
  const accountRoute = createRoute({
    getParentRoute: () => rootRoute,
    path: '/account',
    component: AccountLayout,
  });
  // Add a stub /signin route so Link can resolve it without 404 warnings
  const signinRoute = createRoute({
    getParentRoute: () => rootRoute,
    path: '/signin',
    component: () => <div>sign in</div>,
  });

  const history = createMemoryHistory({ initialEntries: [initialPath] });
  const router = createRouter({
    routeTree: rootRoute.addChildren([accountRoute, signinRoute]),
    history,
  });

  return { router };
}

beforeEach(() => {
  vi.clearAllMocks();
  mockNavigate.mockReset();
});

// ---------------------------------------------------------------------------
// AccountLayout — needs RouterProvider (uses useRouterState + Link)
// ---------------------------------------------------------------------------

describe('AccountLayout', () => {
  it('renders the section navigation tabs', async () => {
    (api.auth.me as ReturnType<typeof vi.fn>).mockResolvedValue(meData);

    const qc = makeQC();
    const { router } = buildAccountRouter(qc);
    render(
      <QueryClientProvider client={qc}>
        <RouterProvider router={router} />
      </QueryClientProvider>,
    );

    // Router renders asynchronously — use findBy so we wait for the initial navigation
    expect(await screen.findByRole('link', { name: /profile/i })).toBeInTheDocument();
    expect(screen.getByRole('link', { name: /security/i })).toBeInTheDocument();
    expect(screen.getByRole('link', { name: /cli tokens/i })).toBeInTheDocument();
  });

  it('shows the user display name once useMe resolves', async () => {
    (api.auth.me as ReturnType<typeof vi.fn>).mockResolvedValue(meData);

    const qc = makeQC();
    const { router } = buildAccountRouter(qc);
    render(
      <QueryClientProvider client={qc}>
        <RouterProvider router={router} />
      </QueryClientProvider>,
    );

    expect(await screen.findByText(/ada lovelace/i)).toBeInTheDocument();
  });

  it('renders the account sections nav landmark', async () => {
    (api.auth.me as ReturnType<typeof vi.fn>).mockResolvedValue(meData);

    const qc = makeQC();
    const { router } = buildAccountRouter(qc);
    render(
      <QueryClientProvider client={qc}>
        <RouterProvider router={router} />
      </QueryClientProvider>,
    );

    // Router resolves asynchronously
    expect(
      await screen.findByRole('navigation', { name: /account sections/i }),
    ).toBeInTheDocument();
  });
});

// ---------------------------------------------------------------------------
// AccountProfileRoute — renders ProfileSection (display name input)
// ---------------------------------------------------------------------------

describe('AccountProfileRoute', () => {
  it('renders ProfileSection (contains display name input)', async () => {
    (api.auth.me as ReturnType<typeof vi.fn>).mockResolvedValue(meData);

    const qc = makeQC();
    render(
      <QCWrapper qc={qc}>
        <AccountProfileRoute />
      </QCWrapper>,
    );

    expect(
      await screen.findByRole('textbox', { name: /display name/i }),
    ).toBeInTheDocument();
  });

  it('shows loading state while me is fetching', () => {
    (api.auth.me as ReturnType<typeof vi.fn>).mockReturnValue(new Promise(() => undefined));

    const qc = makeQC();
    render(
      <QCWrapper qc={qc}>
        <AccountProfileRoute />
      </QCWrapper>,
    );

    // No form while pending
    expect(screen.queryByRole('textbox', { name: /display name/i })).not.toBeInTheDocument();
  });
});

// ---------------------------------------------------------------------------
// AccountSecurityRoute — renders SecuritySection (email change form)
// ---------------------------------------------------------------------------

describe('AccountSecurityRoute', () => {
  it('renders SecuritySection (contains current email reference)', async () => {
    (api.auth.me as ReturnType<typeof vi.fn>).mockResolvedValue(meData);
    (api.auth.sessions.list as ReturnType<typeof vi.fn>).mockResolvedValue([]);

    const qc = makeQC();
    render(
      <QCWrapper qc={qc}>
        <AccountSecurityRoute />
      </QCWrapper>,
    );

    expect(await screen.findByText(/ada@example\.com/i)).toBeInTheDocument();
  });
});

// ---------------------------------------------------------------------------
// AccountCliRoute — renders CliTokensSection
// ---------------------------------------------------------------------------

describe('AccountCliRoute', () => {
  it('renders CliTokensSection (CLI tokens heading)', async () => {
    (api.cli.tokens.list as ReturnType<typeof vi.fn>).mockResolvedValue([]);

    const qc = makeQC();
    render(
      <QCWrapper qc={qc}>
        <AccountCliRoute />
      </QCWrapper>,
    );

    expect(screen.getByText(/cli tokens/i)).toBeInTheDocument();
  });
});

// ---------------------------------------------------------------------------
// VerifyEmailRoute — success path
// ---------------------------------------------------------------------------

describe('VerifyEmailRoute (success path)', () => {
  it('calls useConfirmEmailChange, invalidates auth.me, and shows confirmation', async () => {
    (api.auth.email.confirm as ReturnType<typeof vi.fn>).mockResolvedValue({
      primaryEmail: 'new@example.com',
    });

    const qc = makeQC();
    const invalidateSpy = vi.spyOn(qc, 'invalidateQueries');

    render(
      <QCWrapper qc={qc}>
        <VerifyEmailRoute token="valid-token-abc" />
      </QCWrapper>,
    );

    // Success heading
    expect(
      await screen.findByRole('heading', { name: /email verified and updated/i }),
    ).toBeInTheDocument();

    // auth.me invalidated
    await waitFor(() =>
      expect(invalidateSpy).toHaveBeenCalledWith(
        expect.objectContaining({ queryKey: ['auth', 'me'] }),
      ),
    );

    // api was called with the correct token
    expect(api.auth.email.confirm).toHaveBeenCalledWith({ token: 'valid-token-abc' });
  });

  it('navigates away with replace: true after success to prevent token replay', async () => {
    (api.auth.email.confirm as ReturnType<typeof vi.fn>).mockResolvedValue({
      primaryEmail: 'new@example.com',
    });

    const qc = makeQC();
    render(
      <QCWrapper qc={qc}>
        <VerifyEmailRoute token="valid-token-abc" />
      </QCWrapper>,
    );

    await screen.findByText(/email verified and updated/i);

    // navigate must be called with replace: true within the post-success effect
    await waitFor(() =>
      expect(mockNavigate).toHaveBeenCalledWith(
        expect.objectContaining({ replace: true }),
      ),
    );
  });

  it('offers a link back to account/profile after success', async () => {
    (api.auth.email.confirm as ReturnType<typeof vi.fn>).mockResolvedValue({
      primaryEmail: 'new@example.com',
    });

    const qc = makeQC();
    render(
      <QCWrapper qc={qc}>
        <VerifyEmailRoute token="valid-token-abc" />
      </QCWrapper>,
    );

    // A link pointing to the account profile should appear
    const link = await screen.findByRole('link', { name: /go back to your account/i });
    expect(link).toBeInTheDocument();
    expect(link).toHaveAttribute('href', expect.stringContaining('account/profile'));
  });
});

// ---------------------------------------------------------------------------
// VerifyEmailRoute — expired / invalid token path
// ---------------------------------------------------------------------------

describe('VerifyEmailRoute (expired/invalid token path)', () => {
  it('shows "Link expired or already used" for 410 status', async () => {
    (api.auth.email.confirm as ReturnType<typeof vi.fn>).mockRejectedValue(
      new ApiError(410, 'Token expired'),
    );

    const qc = makeQC();
    render(
      <QCWrapper qc={qc}>
        <VerifyEmailRoute token="expired-token" />
      </QCWrapper>,
    );

    expect(
      await screen.findByText(/link expired or already used/i),
    ).toBeInTheDocument();
  });

  it('shows recoverable error for 400 (invalid/used token)', async () => {
    (api.auth.email.confirm as ReturnType<typeof vi.fn>).mockRejectedValue(
      new ApiError(400, 'Invalid token'),
    );

    const qc = makeQC();
    render(
      <QCWrapper qc={qc}>
        <VerifyEmailRoute token="bad-token" />
      </QCWrapper>,
    );

    // Heading should show the expired/already-used message
    expect(
      await screen.findByRole('heading', { name: /link expired or already used/i }),
    ).toBeInTheDocument();
  });

  it('does NOT invalidate auth.me on error', async () => {
    (api.auth.email.confirm as ReturnType<typeof vi.fn>).mockRejectedValue(
      new ApiError(410, 'Token expired'),
    );

    const qc = makeQC();
    const invalidateSpy = vi.spyOn(qc, 'invalidateQueries');

    render(
      <QCWrapper qc={qc}>
        <VerifyEmailRoute token="expired-token" />
      </QCWrapper>,
    );

    await screen.findByText(/link expired or already used/i);

    const meCalls = invalidateSpy.mock.calls.filter(
      (args) =>
        JSON.stringify(args[0]) === JSON.stringify({ queryKey: ['auth', 'me'] }),
    );
    expect(meCalls).toHaveLength(0);
  });

  it('offers a link to request a new email change on error', async () => {
    (api.auth.email.confirm as ReturnType<typeof vi.fn>).mockRejectedValue(
      new ApiError(410, 'Token expired'),
    );

    const qc = makeQC();
    render(
      <QCWrapper qc={qc}>
        <VerifyEmailRoute token="expired-token" />
      </QCWrapper>,
    );

    const link = await screen.findByRole('link', { name: /request a new email change/i });
    expect(link).toBeInTheDocument();
    expect(link).toHaveAttribute('href', expect.stringContaining('account/security'));
  });
});
