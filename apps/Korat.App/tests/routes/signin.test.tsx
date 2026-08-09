import { beforeEach, describe, expect, it } from 'vitest';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
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

// Import the page component lazily after the route module is loaded.
// SignInPage is the default internal component exported for testing purposes.
import { SignInPage } from '@/routes/signin';

// ---------------------------------------------------------------------------
// Minimal router harness — wraps SignInPage at /signin without full AppShell.
// ---------------------------------------------------------------------------

function makeSignInRouter(initialUrl = '/signin') {
  const rootRoute = createRootRoute()
  const signinRoute = createRoute({
    getParentRoute: () => rootRoute,
    path: '/signin',
    component: SignInPage,
    validateSearch: (search: Record<string, unknown>) => ({
      returnUrl: typeof search.returnUrl === 'string' ? search.returnUrl : undefined,
    }),
  })
  const homeRoute = createRoute({
    getParentRoute: () => rootRoute,
    path: '/',
    component: () => <div>home</div>,
  })
  return createRouter({
    routeTree: rootRoute.addChildren([signinRoute, homeRoute]),
    history: createMemoryHistory({ initialEntries: [initialUrl] }),
  })
}

function renderSignIn(initialUrl = '/signin') {
  const router = makeSignInRouter(initialUrl)
  render(withQueryClient(<RouterProvider router={router} />))
  return router
}

// ---------------------------------------------------------------------------
// MSW handlers (added on top of setup.ts defaults, reset after each test)
// ---------------------------------------------------------------------------

beforeEach(() => {
  server.use(
    http.post('/signin/magic-link', () => new HttpResponse(null, { status: 204 })),
  )
})

// ---------------------------------------------------------------------------
// Tests
// ---------------------------------------------------------------------------

describe('SignIn page', () => {
  it('renders provider buttons and email form', async () => {
    renderSignIn()
    expect(await screen.findByText(/sign in to korat/i)).toBeInTheDocument()
    expect(screen.getByRole('link', { name: /continue with github/i })).toBeInTheDocument()
    expect(screen.getByRole('link', { name: /continue with google/i })).toBeInTheDocument()
    expect(screen.getByLabelText(/email address/i)).toBeInTheDocument()
    expect(screen.getByRole('button', { name: /send sign-in link/i })).toBeInTheDocument()
  })

  it('shows anti-enumeration confirmation copy after magic-link submit', async () => {
    const user = userEvent.setup()
    renderSignIn()

    const emailInput = await screen.findByLabelText(/email address/i)
    await user.type(emailInput, 'test@example.com')
    await user.click(screen.getByRole('button', { name: /send sign-in link/i }))

    await waitFor(() =>
      expect(screen.getByText(/a sign-in link is on its way/i)).toBeInTheDocument(),
    )
    // New confirmation UX: shows the target address (catches typos), validity + junk hint, and a way back.
    expect(screen.getByText('test@example.com')).toBeInTheDocument()
    expect(screen.getByText(/1 hour/i)).toBeInTheDocument()
    expect(screen.getByText(/spam \/ junk/i)).toBeInTheDocument()
    expect(screen.getByRole('button', { name: /use a different email/i })).toBeInTheDocument()
  })

  it('propagates returnUrl into provider href', async () => {
    renderSignIn('/signin?returnUrl=%2Fgrants')

    const githubLink = await screen.findByRole('link', { name: /continue with github/i })
    expect(githubLink).toHaveAttribute('href', expect.stringContaining('returnUrl='))
    expect(githubLink).toHaveAttribute('href', expect.stringContaining('%2Fgrants'))
  })
})
