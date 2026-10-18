import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import {
  createMemoryHistory,
  createRouter,
  RouterProvider,
} from '@tanstack/react-router'
import { render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { http, HttpResponse } from 'msw'
import { describe, expect, it } from 'vitest'
import { routeTree } from '@/routeTree.gen'
import { ThemeProvider } from '@/components/layout/ThemeProvider'
import { server } from '../setup'

const PENDING_LINK = {
  provider: 'google',
  email: 'alice@example.com',
  displayName: 'Alice Example',
}

function renderRoute(initialEntry: string) {
  const queryClient = new QueryClient({
    defaultOptions: {
      queries: { retry: false },
      mutations: { retry: false },
    },
  })
  const router = createRouter({
    routeTree,
    basepath: '/app',
    history: createMemoryHistory({ initialEntries: [initialEntry] }),
  })

  render(
    <ThemeProvider>
      <QueryClientProvider client={queryClient}>
        <RouterProvider router={router} />
      </QueryClientProvider>
    </ThemeProvider>,
  )

  return router
}

describe('authentication in the generated route hierarchy', () => {
  it('keeps an unauthenticated pending-link confirmation visible under /app', async () => {
    server.use(
      http.get('/api/space', () => new HttpResponse('unauthorized', { status: 401 })),
      http.get('/api/auth/pending-link', () => HttpResponse.json(PENDING_LINK)),
      http.get('/api/auth/me', () => new HttpResponse(null, { status: 401 })),
      http.get('/api/version', () => HttpResponse.json({ version: 'test' })),
    )

    const router = renderRoute('/app/signin/link-confirm')

    expect(
      await screen.findByRole('heading', { name: /link your accounts/i }),
    ).toBeInTheDocument()
    expect(screen.queryByRole('heading', { name: /sign in to korat/i })).toBeNull()
    await waitFor(() =>
      expect(router.state.location.pathname).toBe('/signin/link-confirm'),
    )
  })

  it('still gates protected routes and preserves their return URL under /app', async () => {
    server.use(
      http.get('/api/space', () => new HttpResponse('unauthorized', { status: 401 })),
      http.get('/api/auth/me', () => new HttpResponse(null, { status: 401 })),
      http.get('/api/version', () => HttpResponse.json({ version: 'test' })),
    )

    const router = renderRoute('/app/grants?filter=active')

    expect(
      await screen.findByRole('heading', { name: /sign in to korat/i }),
    ).toBeInTheDocument()
    await waitFor(() => expect(router.state.location.pathname).toBe('/signin'))
    expect(router.state.location.search).toEqual({
      returnUrl: '/grants?filter=active',
    })
  })

  it('confirms a pending link and clears the cached 401 before entering the app', async () => {
    let signedIn = false
    let authenticatedReads = 0
    server.use(
      http.get('/api/space', () => {
        if (!signedIn) return new HttpResponse(null, { status: 401 })
        authenticatedReads++
        return HttpResponse.json({
          id: { value: 'default' }, displayName: 'Test Space',
          nodes: [], mcpServers: [], pendingAccessRequests: [],
        })
      }),
      http.get('/api/auth/pending-link', () => HttpResponse.json(PENDING_LINK)),
      http.post('/api/auth/pending-link/confirm', () => {
        signedIn = true
        return new HttpResponse(null, { status: 204 })
      }),
      http.get('/api/auth/me', () => new HttpResponse(null, { status: 401 })),
      http.get('/api/version', () => HttpResponse.json({ version: 'test' })),
    )
    const user = userEvent.setup()
    const router = renderRoute('/app/signin/link-confirm')

    await user.click(await screen.findByRole('button', { name: /yes, link my accounts/i }))

    await waitFor(() => expect(authenticatedReads).toBeGreaterThan(0))
    await waitFor(() => expect(router.state.location.pathname).toBe('/'))
    expect(screen.queryByRole('heading', { name: /sign in to korat/i })).toBeNull()
  })
})
