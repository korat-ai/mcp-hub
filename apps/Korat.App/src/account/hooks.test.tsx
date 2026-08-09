/**
 * Account data hooks — unit tests.
 *
 * All network I/O is mocked at the api module boundary so these tests run
 * entirely in-process without MSW or fetch. We assert:
 *  - the correct api method is called
 *  - mutations invalidate exactly the right queryKeys
 *  - useSignOut calls queryClient.clear() and navigates to /signin
 */
import { describe, expect, it, vi, beforeEach } from 'vitest';
import { renderHook, act, waitFor } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import type { ReactNode } from 'react';

// ---------------------------------------------------------------------------
// Mock api module — individual method mocks are set per-test as needed.
// ---------------------------------------------------------------------------
vi.mock('@/lib/api', () => ({
  api: {
    auth: {
      me: vi.fn(),
      updateMe: vi.fn(),
      sessions: {
        list: vi.fn(),
        revoke: vi.fn(),
      },
      email: {
        change: vi.fn(),
        confirm: vi.fn(),
      },
      signout: vi.fn(),
    },
    cli: {
      tokens: {
        list: vi.fn(),
        revoke: vi.fn(),
      },
    },
  },
}));

// Mock router navigate — TanStack Router requires a router context to navigate;
// we replace the hook so we can assert it was called.
vi.mock('@tanstack/react-router', async (importOriginal) => {
  const original = await importOriginal<typeof import('@tanstack/react-router')>();
  return {
    ...original,
    useNavigate: () => mockNavigate,
  };
});

const mockNavigate = vi.fn();

import { api } from '@/lib/api';
import {
  useMe,
  useUpdateMe,
  useSessions,
  useRevokeSession,
  useRequestEmailChange,
  useConfirmEmailChange,
  useCliTokens,
  useRevokeCliToken,
  useSignOut,
} from '@/account/hooks';
import { queryKeys } from '@/lib/queryKeys';

// ---------------------------------------------------------------------------
// Test harness
// ---------------------------------------------------------------------------

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

beforeEach(() => {
  vi.clearAllMocks();
  mockNavigate.mockReset();
});

// ---------------------------------------------------------------------------
// useMe
// ---------------------------------------------------------------------------

describe('useMe', () => {
  it('calls api.auth.me and returns data', async () => {
    const meData = { userId: 'u1', displayName: 'Alice', primaryEmail: 'a@x.com', providers: [] };
    (api.auth.me as ReturnType<typeof vi.fn>).mockResolvedValue(meData);

    const { qc, wrapper } = makeWrapper();
    const { result } = renderHook(() => useMe(), { wrapper });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));
    expect(api.auth.me).toHaveBeenCalledOnce();
    expect(result.current.data).toEqual(meData);
    qc.clear();
  });
});

// ---------------------------------------------------------------------------
// useUpdateMe
// ---------------------------------------------------------------------------

describe('useUpdateMe', () => {
  it('calls api.auth.updateMe and invalidates auth.me on success', async () => {
    const updated = { userId: 'u1', displayName: 'Bob', primaryEmail: 'b@x.com', providers: [] };
    (api.auth.updateMe as ReturnType<typeof vi.fn>).mockResolvedValue(updated);

    const { qc, wrapper } = makeWrapper();
    const invalidateSpy = vi.spyOn(qc, 'invalidateQueries');

    const { result } = renderHook(() => useUpdateMe(), { wrapper });
    await act(async () => {
      await result.current.mutateAsync({ displayName: 'Bob' });
    });

    expect(api.auth.updateMe).toHaveBeenCalledWith({ displayName: 'Bob' });
    expect(invalidateSpy).toHaveBeenCalledWith({ queryKey: queryKeys.auth.me() });
    qc.clear();
  });
});

// ---------------------------------------------------------------------------
// useSessions
// ---------------------------------------------------------------------------

describe('useSessions', () => {
  it('calls api.auth.sessions.list and returns data', async () => {
    const sessions = [{ id: 's1', userAgent: 'Chrome', lastUsedAt: '2024-01-01', createdAt: '2024-01-01', current: false }];
    (api.auth.sessions.list as ReturnType<typeof vi.fn>).mockResolvedValue(sessions);

    const { qc, wrapper } = makeWrapper();
    const { result } = renderHook(() => useSessions(), { wrapper });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));
    expect(api.auth.sessions.list).toHaveBeenCalledOnce();
    expect(result.current.data).toEqual(sessions);
    qc.clear();
  });
});

// ---------------------------------------------------------------------------
// useRevokeSession
// ---------------------------------------------------------------------------

describe('useRevokeSession', () => {
  it('calls api.auth.sessions.revoke and invalidates auth.sessions on success', async () => {
    (api.auth.sessions.revoke as ReturnType<typeof vi.fn>).mockResolvedValue(undefined);

    const { qc, wrapper } = makeWrapper();
    const invalidateSpy = vi.spyOn(qc, 'invalidateQueries');

    const { result } = renderHook(() => useRevokeSession(), { wrapper });
    await act(async () => {
      await result.current.mutateAsync('s1');
    });

    expect(api.auth.sessions.revoke).toHaveBeenCalledWith('s1');
    expect(invalidateSpy).toHaveBeenCalledWith({ queryKey: queryKeys.auth.sessions() });
    qc.clear();
  });

  // Current-session revoke is handled by useSignOut (via UserMenu), not useRevokeSession.
  // The `current` parameter and the qc.clear()+navigate branch have been removed from
  // useRevokeSession — this comment documents the deliberate design decision.
});

// ---------------------------------------------------------------------------
// useRequestEmailChange
// ---------------------------------------------------------------------------

describe('useRequestEmailChange', () => {
  it('calls api.auth.email.change and does NOT invalidate auth.me', async () => {
    (api.auth.email.change as ReturnType<typeof vi.fn>).mockResolvedValue(undefined);

    const { qc, wrapper } = makeWrapper();
    const invalidateSpy = vi.spyOn(qc, 'invalidateQueries');

    const { result } = renderHook(() => useRequestEmailChange(), { wrapper });
    await act(async () => {
      await result.current.mutateAsync({ newEmail: 'new@x.com' });
    });

    expect(api.auth.email.change).toHaveBeenCalledWith({ newEmail: 'new@x.com' });
    // Must NOT invalidate me — email is not changed until confirmed
    const meCalls = invalidateSpy.mock.calls.filter(
      (args) => JSON.stringify(args[0]) === JSON.stringify({ queryKey: queryKeys.auth.me() }),
    );
    expect(meCalls).toHaveLength(0);
    qc.clear();
  });
});

// ---------------------------------------------------------------------------
// useConfirmEmailChange
// ---------------------------------------------------------------------------

describe('useConfirmEmailChange', () => {
  it('calls api.auth.email.confirm and invalidates auth.me on success', async () => {
    (api.auth.email.confirm as ReturnType<typeof vi.fn>).mockResolvedValue({ primaryEmail: 'new@x.com' });

    const { qc, wrapper } = makeWrapper();
    const invalidateSpy = vi.spyOn(qc, 'invalidateQueries');

    const { result } = renderHook(() => useConfirmEmailChange(), { wrapper });
    await act(async () => {
      await result.current.mutateAsync({ token: 'raw-token' });
    });

    expect(api.auth.email.confirm).toHaveBeenCalledWith({ token: 'raw-token' });
    expect(invalidateSpy).toHaveBeenCalledWith({ queryKey: queryKeys.auth.me() });
    qc.clear();
  });
});

// ---------------------------------------------------------------------------
// useCliTokens
// ---------------------------------------------------------------------------

describe('useCliTokens', () => {
  it('calls api.cli.tokens.list and returns data', async () => {
    const tokens = [{ id: 't1', name: 'My CLI', createdAt: '2024-01-01', lastUsedAt: null, expiresAt: null }];
    (api.cli.tokens.list as ReturnType<typeof vi.fn>).mockResolvedValue(tokens);

    const { qc, wrapper } = makeWrapper();
    const { result } = renderHook(() => useCliTokens(), { wrapper });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));
    expect(api.cli.tokens.list).toHaveBeenCalledOnce();
    expect(result.current.data).toEqual(tokens);
    qc.clear();
  });
});

// ---------------------------------------------------------------------------
// useRevokeCliToken
// ---------------------------------------------------------------------------

describe('useRevokeCliToken', () => {
  it('calls api.cli.tokens.revoke and invalidates cli.tokens on success', async () => {
    (api.cli.tokens.revoke as ReturnType<typeof vi.fn>).mockResolvedValue(undefined);

    const { qc, wrapper } = makeWrapper();
    const invalidateSpy = vi.spyOn(qc, 'invalidateQueries');

    const { result } = renderHook(() => useRevokeCliToken(), { wrapper });
    await act(async () => {
      await result.current.mutateAsync('t1');
    });

    expect(api.cli.tokens.revoke).toHaveBeenCalledWith('t1');
    expect(invalidateSpy).toHaveBeenCalledWith({ queryKey: queryKeys.cli.tokens() });
    qc.clear();
  });
});

// ---------------------------------------------------------------------------
// useSignOut
// ---------------------------------------------------------------------------

describe('useSignOut', () => {
  it('calls api.auth.signout, clears cache, and navigates to /signin', async () => {
    (api.auth.signout as ReturnType<typeof vi.fn>).mockResolvedValue(undefined);

    const { qc, wrapper } = makeWrapper();
    const clearSpy = vi.spyOn(qc, 'clear');

    const { result } = renderHook(() => useSignOut(), { wrapper });
    await act(async () => {
      await result.current.mutateAsync();
    });

    expect(api.auth.signout).toHaveBeenCalledOnce();
    expect(clearSpy).toHaveBeenCalled();
    expect(mockNavigate).toHaveBeenCalledWith({ to: '/signin' });
    qc.clear();
  });
});
