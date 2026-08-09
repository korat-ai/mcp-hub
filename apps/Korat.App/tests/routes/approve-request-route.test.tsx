/**
 * approve.$requestId route tests (task #9).
 *
 * Covers:
 *  - Pending request → renders Allow/Deny buttons; clicking Allow POSTs
 *    /api/access-requests/:id/approve and fires a success toast.
 *  - Non-Pending status → renders the "was {status}" empty state.
 *  - Load error → renders ErrorState.
 *
 * Uses a full in-memory TanStack Router setup so the route resolves params
 * and loads the component identically to production.
 */
import { describe, expect, it, vi } from 'vitest';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import {
  createMemoryHistory,
  createRootRoute,
  createRoute,
  createRouter,
  RouterProvider,
} from '@tanstack/react-router';
import { http, HttpResponse } from 'msw';
import { server } from '../setup';
import { withQueryClient } from '../test-utils';
import type { AccessRequestDto } from '@/types/api';

// Stub next-themes for Toaster dependency.
vi.mock('next-themes', () => ({ useTheme: () => ({ theme: 'light' }) }));
const { Toaster } = await import('@/components/ui/sonner');

// ---------------------------------------------------------------------------
// Import the approve page component.
// The route module only exports `Route` (the TanStack route definition).
// We extract the component from it to mount inside a test-controlled router.
// ---------------------------------------------------------------------------

// We import at module level (ESM static import) — the $ in the filename is
// valid in import specifiers even though it's not allowed in require().
import { Route as ApproveRoute } from '@/routes/approve.$requestId';

// ---------------------------------------------------------------------------
// Shared fixtures
// ---------------------------------------------------------------------------

const PENDING_REQUEST: AccessRequestDto = {
  id: 'r1',
  status: 'Pending',
  consumerId: '@planner',
  agentNodeId: 'node-1',
  agentNodeName: 'planner-node',
  mcpServerId: 'postgres',
  mcpServerName: 'postgres',
  publisherNodeId: 'pub-1',
  publisherNodeName: 'publisher-node',
  requestedAt: new Date().toISOString(),
};

const APPROVED_REQUEST: AccessRequestDto = {
  ...PENDING_REQUEST,
  status: 'Approved',
};

const DENIED_REQUEST: AccessRequestDto = {
  ...PENDING_REQUEST,
  status: 'Denied',
};

// ---------------------------------------------------------------------------
// Router factory
// ---------------------------------------------------------------------------

function makeApproveRouter(requestId: string) {
  const rootRoute = createRootRoute();

  const grantsRoute = createRoute({
    getParentRoute: () => rootRoute,
    path: '/grants',
    component: () => <div>grants</div>,
  });

  // Re-parent the real approve route under our test root.
  // TanStack Router lets us call .update() to swap the parent reference.
  const approveRoute = createRoute({
    getParentRoute: () => rootRoute,
    path: '/approve/$requestId',
    component: ApproveRoute.options.component,
  });

  const routeTree = rootRoute.addChildren([approveRoute, grantsRoute]);

  return createRouter({
    routeTree,
    history: createMemoryHistory({ initialEntries: [`/approve/${requestId}`] }),
  });
}

function renderApproveRoute(requestId = 'r1') {
  const router = makeApproveRouter(requestId);
  return render(
    withQueryClient(
      <>
        <RouterProvider router={router} />
        <Toaster />
      </>,
    ),
  );
}

// ---------------------------------------------------------------------------
// Tests
// ---------------------------------------------------------------------------

describe('approve.$requestId route', () => {
  it('Pending request → renders Allow and Deny buttons', async () => {
    server.use(
      http.get('/api/access-requests/r1', () =>
        HttpResponse.json(PENDING_REQUEST),
      ),
    );

    renderApproveRoute('r1');

    expect(await screen.findByRole('button', { name: /allow access/i })).toBeInTheDocument();
    expect(screen.getByRole('button', { name: /deny/i })).toBeInTheDocument();
  });

  it('clicking Allow POSTs /api/access-requests/:id/approve', async () => {
    server.use(
      http.get('/api/access-requests/r1', () =>
        HttpResponse.json(PENDING_REQUEST),
      ),
      http.post('/api/access-requests/r1/approve', () =>
        HttpResponse.json({ id: 'g1', status: 'Active' }),
      ),
    );

    const user = userEvent.setup();
    renderApproveRoute('r1');

    const allowBtn = await screen.findByRole('button', { name: /allow access/i });
    await user.click(allowBtn);

    // Toast should appear with success message.
    await waitFor(() =>
      expect(screen.getByText(/access approved/i)).toBeInTheDocument(),
    );
  });

  it('Approved status → renders "was approved" empty state', async () => {
    server.use(
      http.get('/api/access-requests/r1', () =>
        HttpResponse.json(APPROVED_REQUEST),
      ),
    );

    renderApproveRoute('r1');

    await waitFor(() =>
      expect(screen.getByText(/request was approved/i)).toBeInTheDocument(),
    );
    expect(screen.queryByRole('button', { name: /allow access/i })).not.toBeInTheDocument();
  });

  it('Denied status → renders "was denied" empty state', async () => {
    server.use(
      http.get('/api/access-requests/r1', () =>
        HttpResponse.json(DENIED_REQUEST),
      ),
    );

    renderApproveRoute('r1');

    await waitFor(() =>
      expect(screen.getByText(/request was denied/i)).toBeInTheDocument(),
    );
  });

  it('load error (5xx/4xx other than 404) → renders ErrorState', async () => {
    server.use(
      http.get('/api/access-requests/r1', () =>
        new HttpResponse('forbidden', { status: 403 }),
      ),
    );

    renderApproveRoute('r1');

    await waitFor(() =>
      expect(screen.getByText(/could not load request/i)).toBeInTheDocument(),
    );
  });

  it('404 → renders the distinct "Request not found" state, not the generic ErrorState', async () => {
    server.use(
      http.get('/api/access-requests/r1', () =>
        new HttpResponse('not found', { status: 404 }),
      ),
    );

    renderApproveRoute('r1');

    await waitFor(() =>
      expect(screen.getByText(/request not found/i)).toBeInTheDocument(),
    );
    expect(screen.queryByText(/could not load request/i)).not.toBeInTheDocument();
  });
});
