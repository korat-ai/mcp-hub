import type { ReactElement } from 'react';
import { describe, expect, it } from 'vitest';
import { render, screen, waitFor } from '@testing-library/react';
import { http, HttpResponse } from 'msw';
import {
  createMemoryHistory,
  createRootRoute,
  createRoute,
  createRouter,
  RouterProvider,
} from '@tanstack/react-router';
import { server } from '../setup';
import { withQueryClient } from '../test-utils';
import { AuthGate } from '@/components/layout/AuthGate';

// ---------------------------------------------------------------------------
// Minimal router harness so AuthGate can call useNavigate / useRouterState.
// ---------------------------------------------------------------------------

function makeRouter(component: () => ReactElement, includeSignin = false) {
  const rootRoute = createRootRoute()
  const indexRoute = createRoute({
    getParentRoute: () => rootRoute,
    path: '/',
    component,
  })
  const children = includeSignin
    ? [
        indexRoute,
        createRoute({ getParentRoute: () => rootRoute, path: '/signin', component: () => <div>signin</div> }),
      ]
    : [indexRoute]
  return createRouter({
    routeTree: rootRoute.addChildren(children),
    history: createMemoryHistory({ initialEntries: ['/'] }),
  })
}

describe('AuthGate', () => {
  it('redirects to /signin when /api/space returns 401', async () => {
    server.use(http.get('/api/space', () => new HttpResponse('nope', { status: 401 })));
    const router = makeRouter(
      () => <AuthGate><div>protected</div></AuthGate>,
      true,
    )
    render(withQueryClient(<RouterProvider router={router} />))
    // After redirect, the signin stub should be visible.
    await waitFor(() => expect(screen.getByText('signin')).toBeInTheDocument())
  });

  it('renders children when /api/space succeeds', async () => {
    const router = makeRouter(() => <AuthGate><div>protected</div></AuthGate>)
    render(withQueryClient(<RouterProvider router={router} />))
    await waitFor(() => expect(screen.getByText('protected')).toBeInTheDocument());
  });
});
