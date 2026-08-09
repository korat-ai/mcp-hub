import { beforeEach, describe, expect, it } from 'vitest'
import { render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { http, HttpResponse } from 'msw'
import {
  createMemoryHistory,
  createRootRoute,
  createRoute,
  createRouter,
  RouterProvider,
} from '@tanstack/react-router'
import { server } from '../setup'
import { withQueryClient } from '../test-utils'
import { LinkConfirmPage } from '@/routes/signin.link-confirm'

// ---------------------------------------------------------------------------
// Minimal router harness
// ---------------------------------------------------------------------------

function makeLinkConfirmRouter(initialUrl = '/signin/link-confirm') {
  const rootRoute = createRootRoute()
  const route = createRoute({
    getParentRoute: () => rootRoute,
    path: '/signin/link-confirm',
    component: LinkConfirmPage,
  })
  const home = createRoute({
    getParentRoute: () => rootRoute,
    path: '/',
    component: () => <div>home</div>,
  })
  const signin = createRoute({
    getParentRoute: () => rootRoute,
    path: '/signin',
    component: () => <div>signin</div>,
  })
  return createRouter({
    routeTree: rootRoute.addChildren([route, home, signin]),
    history: createMemoryHistory({ initialEntries: [initialUrl] }),
  })
}

function renderLinkConfirm(initialUrl = '/signin/link-confirm') {
  const router = makeLinkConfirmRouter(initialUrl)
  render(withQueryClient(<RouterProvider router={router} />))
  return router
}

// ---------------------------------------------------------------------------
// MSW handlers (added on top of setup.ts defaults, reset after each test)
// ---------------------------------------------------------------------------

const PENDING_PAYLOAD = {
  provider: 'google',
  email: 'alice@example.com',
  displayName: 'Alice Example',
}

beforeEach(() => {
  server.use(
    http.get('/api/auth/pending-link', () => HttpResponse.json(PENDING_PAYLOAD)),
    http.post('/api/auth/pending-link/confirm', () => new HttpResponse(null, { status: 204 })),
    http.post('/api/auth/pending-link/cancel', () => new HttpResponse(null, { status: 204 })),
  )
})

// ---------------------------------------------------------------------------
// Tests
// ---------------------------------------------------------------------------

describe('LinkConfirmPage', () => {
  it('renders pending-link metadata when GET returns payload', async () => {
    renderLinkConfirm()

    expect(await screen.findByRole('heading', { name: /link your accounts/i })).toBeInTheDocument()
    expect(await screen.findByText('google')).toBeInTheDocument()
    expect(await screen.findByText('alice@example.com')).toBeInTheDocument()
    expect(await screen.findByText('Alice Example')).toBeInTheDocument()

    expect(screen.getByRole('button', { name: /yes, link my accounts/i })).toBeInTheDocument()
    expect(screen.getByRole('button', { name: /cancel/i })).toBeInTheDocument()
  })

  it('confirm button POSTs to /api/auth/pending-link/confirm and navigates to /', async () => {
    const user = userEvent.setup()
    let confirmHit = false
    server.use(
      http.post('/api/auth/pending-link/confirm', () => {
        confirmHit = true
        return new HttpResponse(null, { status: 204 })
      }),
    )
    const router = renderLinkConfirm()

    const confirmBtn = await screen.findByRole('button', { name: /yes, link my accounts/i })
    await user.click(confirmBtn)

    await waitFor(() => expect(confirmHit).toBe(true))
    await waitFor(() => expect(router.state.location.pathname).toBe('/'))
  })

  it('shows error alert when confirm POST returns 400', async () => {
    const user = userEvent.setup()
    server.use(
      http.post('/api/auth/pending-link/confirm', () => new HttpResponse(null, { status: 400 })),
    )
    renderLinkConfirm()

    const confirmBtn = await screen.findByRole('button', { name: /yes, link my accounts/i })
    await user.click(confirmBtn)

    await waitFor(() =>
      expect(screen.getByRole('alert')).toHaveTextContent(/couldn't link/i),
    )
  })

  it('shows fallback UI when GET /api/auth/pending-link returns 404', async () => {
    server.use(
      http.get('/api/auth/pending-link', () => new HttpResponse(null, { status: 404 })),
    )

    renderLinkConfirm()

    expect(await screen.findByText(/nothing to confirm/i)).toBeInTheDocument()
    expect(screen.getByRole('button', { name: /back to sign in/i })).toBeInTheDocument()
  })
})
