import { describe, expect, it, vi } from 'vitest';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { http, HttpResponse } from 'msw';
import { server } from '../setup';
import { withQueryClient } from '../test-utils';
import { AccessRequestCard } from '@/components/domain/AccessRequestCard';
import { useApproveRequest } from '@/hooks/useApproveRequest';
import {
  createMemoryHistory,
  createRootRoute,
  createRoute,
  createRouter,
  RouterProvider,
} from '@tanstack/react-router';
import type { AccessRequestSummaryDto } from '@/types/api';

// Sonner's Toaster component calls useTheme() from next-themes.
// In jsdom there is no ThemeProvider, so we stub the hook.
vi.mock('next-themes', () => ({ useTheme: () => ({ theme: 'light' }) }));

// Import Toaster AFTER the mock is registered.
const { Toaster } = await import('@/components/ui/sonner');

const sampleReq: AccessRequestSummaryDto = {
  id: { value: 'r1' },
  consumerId: { value: '@planner' },
  consumerDisplayName: 'claude-code',
  mcpServerId: { value: 'postgres' },
  mcpServerDisplayName: 'korat-repo-fs',
  status: 'Pending',
  requestedAt: new Date().toISOString(),
};

function Harness() {
  const approve = useApproveRequest();
  return (
    <>
      <AccessRequestCard
        request={sampleReq}
        onApprove={() =>
          approve.mutate({ requestId: 'r1', agentLabel: '@planner', serverLabel: 'postgres' })
        }
        onDeny={() => undefined}
        approvePending={approve.isPending}
        denyPending={false}
      />
      <Toaster />
    </>
  );
}

function makeRouter() {
  const rootRoute = createRootRoute();
  const indexRoute = createRoute({
    getParentRoute: () => rootRoute,
    path: '/',
    component: Harness,
  });
  const approveRoute = createRoute({
    getParentRoute: () => rootRoute,
    path: '/approve/$requestId',
    component: () => null,
  });
  const routeTree = rootRoute.addChildren([indexRoute, approveRoute]);
  return createRouter({
    routeTree,
    history: createMemoryHistory({ initialEntries: ['/'] }),
  });
}

const sampleReqA: AccessRequestSummaryDto = {
  id: { value: 'r1' },
  consumerId: { value: '@a' },
  mcpServerId: { value: 's1' },
  status: 'Pending',
  requestedAt: new Date().toISOString(),
};

const sampleReqB: AccessRequestSummaryDto = {
  id: { value: 'r2' },
  consumerId: { value: '@b' },
  mcpServerId: { value: 's2' },
  status: 'Pending',
  requestedAt: new Date().toISOString(),
};

function MultiCardHarness() {
  const approve = useApproveRequest();
  const rowPending = (rid: string) =>
    approve.isPending && approve.variables?.requestId === rid;
  return (
    <>
      <AccessRequestCard
        request={sampleReqA}
        onApprove={() => approve.mutate({ requestId: 'r1', agentLabel: '@a', serverLabel: 's1' })}
        onDeny={() => undefined}
        approvePending={rowPending('r1')}
        denyPending={false}
      />
      <AccessRequestCard
        request={sampleReqB}
        onApprove={() => approve.mutate({ requestId: 'r2', agentLabel: '@b', serverLabel: 's2' })}
        onDeny={() => undefined}
        approvePending={rowPending('r2')}
        denyPending={false}
      />
      <Toaster />
    </>
  );
}

function makeMultiCardRouter() {
  const rootRoute = createRootRoute();
  const indexRoute = createRoute({
    getParentRoute: () => rootRoute,
    path: '/',
    component: MultiCardHarness,
  });
  const approveRoute = createRoute({
    getParentRoute: () => rootRoute,
    path: '/approve/$requestId',
    component: () => null,
  });
  const routeTree = rootRoute.addChildren([indexRoute, approveRoute]);
  return createRouter({
    routeTree,
    history: createMemoryHistory({ initialEntries: ['/'] }),
  });
}

describe('AccessRequestCard + useApproveRequest', () => {
  it('approve click POSTs and shows receipt toast', async () => {
    server.use(
      http.post('/api/access-requests/r1/approve', () =>
        HttpResponse.json({ id: 'g1', status: 'Active' }),
      ),
    );
    const user = userEvent.setup();
    const router = makeRouter();
    render(withQueryClient(<RouterProvider router={router} />));
    // Each card renders two Approve buttons (mobile + desktop copies).
    // Click the first one — both are wired to the same handler.
    const approveBtns = await screen.findAllByRole('button', { name: /approve/i });
    await user.click(approveBtns[0]);
    await waitFor(() =>
      expect(screen.getByText(/access approved/i)).toBeInTheDocument(),
    );
    expect(screen.getByText(/@planner → postgres/)).toBeInTheDocument();
  });

  it('per-row pending — clicking approve on one card does not disable the other', async () => {
    // Slow down the POST so we can observe the in-flight state.
    let releasePost: () => void = () => undefined;
    const inflight = new Promise<void>((resolve) => {
      releasePost = resolve;
    });
    server.use(
      http.post('/api/access-requests/r1/approve', async () => {
        await inflight;
        return HttpResponse.json({ id: 'g1', status: 'Active' });
      }),
    );

    const user = userEvent.setup();
    const router = makeMultiCardRouter();
    render(withQueryClient(<RouterProvider router={router} />));

    // Each card renders two Approve buttons (mobile full-width + desktop inline).
    // Two cards → 4 Approve buttons total. Indices 0,1 belong to card A; 2,3 to card B.
    const approveButtons = await screen.findAllByRole('button', { name: /approve/i });
    expect(approveButtons).toHaveLength(4);

    // Click the FIRST card's first (mobile) Approve.
    await user.click(approveButtons[0]);

    // Both of card A's Approve buttons become disabled; both of card B's stay enabled.
    await waitFor(() => expect(approveButtons[0]).toBeDisabled());
    expect(approveButtons[1]).toBeDisabled();
    expect(approveButtons[2]).toBeEnabled();
    expect(approveButtons[3]).toBeEnabled();

    // Release the stuck mutation so the test cleans up cleanly.
    releasePost();
  });

  it('renders consumerDisplayName and mcpServerDisplayName when present', async () => {
    const router = makeRouter();
    render(withQueryClient(<RouterProvider router={router} />));
    // sampleReq has consumerDisplayName='claude-code' and mcpServerDisplayName='korat-repo-fs'.
    // The heading div renders "<agent> → <server>"; both names must appear.
    expect(await screen.findByText(/claude-code/)).toBeInTheDocument();
    expect(screen.getByText(/korat-repo-fs/)).toBeInTheDocument();
    // The raw ids (@planner, postgres) must not appear as standalone text in the card header.
    // (They only show in the muted request-id suffix, not as primary labels.)
    const heading = screen.getByText((_content, el) => {
      if (!el || typeof el.className !== 'string') return false;
      return el.className.includes('font-semibold') && (el.textContent ?? '').includes('→');
    });
    expect(heading).toHaveTextContent('claude-code → korat-repo-fs');
    // Regression guard: relativeFromNow already includes the " ago" suffix — the card
    // must NOT append another one ("5m ago ago" / "just now ago" / "— ago").
    expect(screen.queryByText(/ago ago|just now ago|— ago/)).toBeNull();
  });

  it('falls back to raw id when display names are absent', async () => {
    const reqNoNames: AccessRequestSummaryDto = {
      id: { value: 'r-fallback' },
      consumerId: { value: 'agentXXX' },
      mcpServerId: { value: 'serverYYY' },
      status: 'Pending',
      requestedAt: new Date().toISOString(),
    };

    const rootRoute = createRootRoute();
    const idxRoute = createRoute({
      getParentRoute: () => rootRoute,
      path: '/',
      component: () => (
        <AccessRequestCard
          request={reqNoNames}
          onApprove={() => undefined}
          onDeny={() => undefined}
          approvePending={false}
          denyPending={false}
        />
      ),
    });
    const approveRoute = createRoute({
      getParentRoute: () => rootRoute,
      path: '/approve/$requestId',
      component: () => null,
    });
    const fallbackRouter = createRouter({
      routeTree: rootRoute.addChildren([idxRoute, approveRoute]),
      history: createMemoryHistory({ initialEntries: ['/'] }),
    });

    render(withQueryClient(<RouterProvider router={fallbackRouter} />));
    const heading = await screen.findByText((_content, el) => {
      if (!el || typeof el.className !== 'string') return false;
      return el.className.includes('font-semibold') && (el.textContent ?? '').includes('→');
    });
    expect(heading).toHaveTextContent('agentXXX → serverYYY');
  });
});
