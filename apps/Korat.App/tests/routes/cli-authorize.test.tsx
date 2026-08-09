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
import { CliAuthorizePage } from '@/routes/cli.authorize'

// ---------------------------------------------------------------------------
// Minimal router harness — wraps CliAuthorizePage at /cli/authorize.
// ---------------------------------------------------------------------------

function makeRouter(initialUrl = '/cli/authorize') {
  const rootRoute = createRootRoute()
  const cliRoute = createRoute({
    getParentRoute: () => rootRoute,
    path: '/cli/authorize',
    component: CliAuthorizePage,
    validateSearch: (search: Record<string, unknown>) => ({
      code: typeof search.code === 'string' ? search.code.trim().toUpperCase() : undefined,
    }),
  })
  return createRouter({
    routeTree: rootRoute.addChildren([cliRoute]),
    history: createMemoryHistory({ initialEntries: [initialUrl] }),
  })
}

function renderPage(initialUrl = '/cli/authorize') {
  const router = makeRouter(initialUrl)
  render(withQueryClient(<RouterProvider router={router} />))
  return router
}

// ---------------------------------------------------------------------------
// MSW handlers (reset after each test by setup.ts afterEach)
// ---------------------------------------------------------------------------

beforeEach(() => {
  server.use(
    http.post('/api/auth/cli/approve', () => new HttpResponse(null, { status: 204 })),
    http.post('/api/auth/cli/deny', () => new HttpResponse(null, { status: 204 })),
  )
})

// ---------------------------------------------------------------------------
// Tests
// ---------------------------------------------------------------------------

describe('CliAuthorizePage', () => {
  it('renders heading and Approve/Deny buttons', async () => {
    renderPage()
    expect(await screen.findByText(/authorize cli access/i)).toBeInTheDocument()
    expect(screen.getByRole('button', { name: /approve/i })).toBeInTheDocument()
    expect(screen.getByRole('button', { name: /deny/i })).toBeInTheDocument()
  })

  it('shows the user code from ?code= search param', async () => {
    renderPage('/cli/authorize?code=ABCD1234')
    expect(await screen.findByText('ABCD1234')).toBeInTheDocument()
  })

  it('approve button calls POST /api/auth/cli/approve and shows success', async () => {
    const user = userEvent.setup()
    renderPage('/cli/authorize?code=ABCD1234')

    const approveBtn = await screen.findByRole('button', { name: /approve/i })
    await user.click(approveBtn)

    await waitFor(() =>
      expect(screen.getByText(/cli access authorized/i)).toBeInTheDocument(),
    )
  })

  it('deny button calls POST /api/auth/cli/deny and shows denial message', async () => {
    const user = userEvent.setup()
    renderPage('/cli/authorize?code=ABCD1234')

    const denyBtn = await screen.findByRole('button', { name: /deny/i })
    await user.click(denyBtn)

    await waitFor(() =>
      expect(screen.getByText(/cli access denied/i)).toBeInTheDocument(),
    )
  })

  it('shows error message on 404 (code not found or expired)', async () => {
    server.use(
      http.post('/api/auth/cli/approve', () => new HttpResponse(null, { status: 404, statusText: 'Not Found' })),
    )
    const user = userEvent.setup()
    renderPage('/cli/authorize?code=EXPIRED1')

    const approveBtn = await screen.findByRole('button', { name: /approve/i })
    await user.click(approveBtn)

    await waitFor(() =>
      expect(screen.getByRole('alert')).toHaveTextContent(/no longer valid/i),
    )
  })

  it('buttons are disabled while request is in flight', async () => {
    let release: () => void = () => undefined
    const inflight = new Promise<void>((resolve) => {
      release = resolve
    })
    server.use(
      http.post('/api/auth/cli/approve', async () => {
        await inflight
        return new HttpResponse(null, { status: 204 })
      }),
    )

    const user = userEvent.setup()
    renderPage('/cli/authorize?code=ABCD1234')

    const approveBtn = await screen.findByRole('button', { name: /approve/i })
    const denyBtn = screen.getByRole('button', { name: /deny/i })
    await user.click(approveBtn)

    await waitFor(() => expect(approveBtn).toBeDisabled())
    expect(denyBtn).toBeDisabled()

    release()
  })

  it('shows code input field when no ?code= param is provided', async () => {
    renderPage('/cli/authorize')
    expect(await screen.findByLabelText(/enter the code/i)).toBeInTheDocument()
  })

  it('approve with manually entered code sends the correct user_code', async () => {
    let capturedBody: unknown = null
    server.use(
      http.post('/api/auth/cli/approve', async ({ request }) => {
        capturedBody = await request.json()
        return new HttpResponse(null, { status: 204 })
      }),
    )

    const user = userEvent.setup()
    renderPage('/cli/authorize')

    const input = await screen.findByLabelText(/enter the code/i)
    await user.type(input, 'MYCODE12')

    const approveBtn = screen.getByRole('button', { name: /approve/i })
    await user.click(approveBtn)

    await waitFor(() =>
      expect(screen.getByText(/cli access authorized/i)).toBeInTheDocument(),
    )
    expect(capturedBody).toEqual({ user_code: 'MYCODE12' })
  })
})
