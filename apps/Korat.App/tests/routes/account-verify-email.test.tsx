/**
 * Tests for VerifyEmailRoute (account.verify-email.tsx).
 *
 * Covered invariants (cov C5):
 *  - Token-stripping: mounting with ?token= in the URL causes history.replaceState to
 *    remove the token from window.location.search before the confirm POST fires.
 *  - Missing-token branch: mounting without a token renders the "invalid link" error UI.
 */
import { describe, expect, it, vi, beforeEach } from 'vitest'
import { render, screen, waitFor } from '@testing-library/react'
import { VerifyEmailRoute } from '@/routes/account.verify-email'

// ---------------------------------------------------------------------------
// Mock hooks used by VerifyEmailRoute
// ---------------------------------------------------------------------------

// Mock useConfirmEmailChange so tests don't hit the network.
// The component fires confirm.mutate({ token }) on mount; we stub to a no-op
// by default and expose controls for individual tests.
const mockMutate = vi.fn()
const mockConfirm = {
  mutate: mockMutate,
  isPending: false,
  isIdle: true,
  isSuccess: false,
  isError: false,
  error: null,
  data: undefined,
}

vi.mock('@/account/hooks', () => ({
  useConfirmEmailChange: () => mockConfirm,
}))

// Mock useNavigate — VerifyEmailRoute calls navigate on success.
const mockNavigate = vi.fn()
vi.mock('@tanstack/react-router', async (importOriginal) => {
  const original = await importOriginal<typeof import('@tanstack/react-router')>()
  return {
    ...original,
    useNavigate: () => mockNavigate,
  }
})

// ---------------------------------------------------------------------------
// Tests
// ---------------------------------------------------------------------------

beforeEach(() => {
  vi.clearAllMocks()
  mockConfirm.mutate = mockMutate
  mockConfirm.isPending = false
  mockConfirm.isIdle = true
  mockConfirm.isSuccess = false
  mockConfirm.isError = false
  mockConfirm.error = null
  mockConfirm.data = undefined
})

describe('VerifyEmailRoute — token stripping (cov C5)', () => {
  it('strips ?token= from window.location.search on mount', async () => {
    // jsdom does not process history.replaceState automatically — manually set the
    // URL as jsdom would have it before the component mounts, then verify it was cleared.
    const rawToken = 'test-token-secret-value'

    // Simulate the browser URL containing ?token=abc before the component mounts.
    window.history.replaceState(null, '', `/?token=${rawToken}`)
    expect(window.location.search).toContain(`token=${rawToken}`)

    render(<VerifyEmailRoute token={rawToken} />)

    // history.replaceState should have been called synchronously in useEffect,
    // removing the token from the address bar.
    await waitFor(() => {
      expect(window.location.search).not.toContain('token=')
    })
  })

  it('calls confirm.mutate with the provided token on mount', async () => {
    const rawToken = 'my-secret-token'
    render(<VerifyEmailRoute token={rawToken} />)

    await waitFor(() => {
      expect(mockMutate).toHaveBeenCalledWith({ token: rawToken })
    })
  })
})

describe('VerifyEmailRoute — missing token branch (cov C5)', () => {
  it('renders the "invalid verification link" error UI when no token is provided', () => {
    render(<VerifyEmailRoute token={undefined} />)

    expect(screen.getByText(/invalid verification link/i)).toBeInTheDocument()
    expect(screen.getByRole('link', { name: /request a new email change/i })).toBeInTheDocument()
  })

  it('does NOT call confirm.mutate when no token is provided', () => {
    render(<VerifyEmailRoute token={undefined} />)
    expect(mockMutate).not.toHaveBeenCalled()
  })
})
