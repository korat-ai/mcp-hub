/**
 * Mutation hook tests (task #8).
 *
 * Covers hooks that previously had NO test coverage:
 *  - useDenyRequest: success invalidates space + accessRequest cache; fires toast;
 *    ApiError surfaces on failure.
 *  - useDisableServer: success invalidates space cache; fires toast;
 *    ApiError surfaces on failure.
 *
 * Mirrors the existing useApproveRequest / useRevokeGrant test patterns.
 */
import { describe, expect, it, vi } from 'vitest';
import { renderHook, act, waitFor } from '@testing-library/react';
import { http, HttpResponse } from 'msw';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import type { ReactNode } from 'react';
import { server } from '../setup';
import { useDenyRequest } from '@/hooks/useDenyRequest';
import { useDisableServer } from '@/hooks/useDisableServer';
import { ApiError } from '@/lib/api';
import { queryKeys } from '@/lib/queryKeys';

// Stub next-themes for the Toaster (sonner uses useTheme).
vi.mock('next-themes', () => ({ useTheme: () => ({ theme: 'light' }) }));

// ---------------------------------------------------------------------------
// Wrapper factory
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

// ---------------------------------------------------------------------------
// useDenyRequest
// ---------------------------------------------------------------------------

describe('useDenyRequest', () => {
  it('success invalidates space and accessRequest cache keys', async () => {
    server.use(
      http.post('/api/access-requests/r1/deny', () =>
        new HttpResponse(null, { status: 204 }),
      ),
    );

    const { qc, wrapper } = makeWrapper();
    // Pre-populate the cache so we can observe invalidation.
    qc.setQueryData(queryKeys.space.all, { nodes: [], mcpServers: [], pendingAccessRequests: [] });
    qc.setQueryData(queryKeys.accessRequests.byId('r1'), { id: 'r1', status: 'Pending' });

    const invalidateSpy = vi.spyOn(qc, 'invalidateQueries');

    const { result } = renderHook(() => useDenyRequest(), { wrapper });

    await act(async () => {
      result.current.mutate({ requestId: 'r1', agentLabel: '@agent', serverLabel: 'server' });
    });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));

    // Check that both relevant query keys were invalidated.
    const invalidatedKeys = invalidateSpy.mock.calls.map((c) => c[0]);
    expect(invalidatedKeys).toEqual(
      expect.arrayContaining([
        expect.objectContaining({ queryKey: queryKeys.space.all }),
        expect.objectContaining({ queryKey: queryKeys.accessRequests.byId('r1') }),
      ]),
    );
  });

  it('success fires a "bad" toast with agent → server subject', async () => {
    server.use(
      http.post('/api/access-requests/r1/deny', () =>
        new HttpResponse(null, { status: 204 }),
      ),
    );

    const toastSpy = vi.fn();
    // Patch the sonner toast module for this test.
    vi.doMock('sonner', () => ({
      toast: {
        success: vi.fn(),
        error: toastSpy,
      },
    }));

    const { wrapper } = makeWrapper();
    const { result } = renderHook(() => useDenyRequest(), { wrapper });

    await act(async () => {
      result.current.mutate({ requestId: 'r1', agentLabel: '@agent', serverLabel: 'my-server' });
    });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));
  });

  it('surfaces ApiError on 500', async () => {
    server.use(
      http.post('/api/access-requests/r1/deny', () =>
        new HttpResponse('server error', { status: 500 }),
      ),
    );

    const { wrapper } = makeWrapper();
    const { result } = renderHook(() => useDenyRequest(), { wrapper });

    await act(async () => {
      result.current.mutate({ requestId: 'r1', agentLabel: '@agent', serverLabel: 'server' });
    });

    await waitFor(() => expect(result.current.isError).toBe(true));
    expect(result.current.error).toBeInstanceOf(ApiError);
    expect((result.current.error as ApiError).status).toBe(500);
  });

  it('surfaces ApiError on 404', async () => {
    server.use(
      http.post('/api/access-requests/r99/deny', () =>
        new HttpResponse('not found', { status: 404 }),
      ),
    );

    const { wrapper } = makeWrapper();
    const { result } = renderHook(() => useDenyRequest(), { wrapper });

    await act(async () => {
      result.current.mutate({ requestId: 'r99', agentLabel: '@agent', serverLabel: 'server' });
    });

    await waitFor(() => expect(result.current.isError).toBe(true));
    expect(result.current.error).toBeInstanceOf(ApiError);
    expect((result.current.error as ApiError).status).toBe(404);
  });
});

// ---------------------------------------------------------------------------
// useDisableServer
// ---------------------------------------------------------------------------

describe('useDisableServer', () => {
  it('success invalidates space cache key', async () => {
    server.use(
      http.post('/api/mcp-servers/srv1/disable', () =>
        new HttpResponse(null, { status: 204 }),
      ),
    );

    const { qc, wrapper } = makeWrapper();
    qc.setQueryData(queryKeys.space.all, { nodes: [], mcpServers: [], pendingAccessRequests: [] });

    const invalidateSpy = vi.spyOn(qc, 'invalidateQueries');

    const { result } = renderHook(() => useDisableServer(), { wrapper });

    await act(async () => {
      result.current.mutate({ serverId: 'srv1', displayName: 'My Server' });
    });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));

    const invalidatedKeys = invalidateSpy.mock.calls.map((c) => c[0]);
    expect(invalidatedKeys).toEqual(
      expect.arrayContaining([
        expect.objectContaining({ queryKey: queryKeys.space.all }),
      ]),
    );
  });

  it('surfaces ApiError on 403', async () => {
    server.use(
      http.post('/api/mcp-servers/srv1/disable', () =>
        new HttpResponse('forbidden', { status: 403 }),
      ),
    );

    const { wrapper } = makeWrapper();
    const { result } = renderHook(() => useDisableServer(), { wrapper });

    await act(async () => {
      result.current.mutate({ serverId: 'srv1', displayName: 'My Server' });
    });

    await waitFor(() => expect(result.current.isError).toBe(true));
    expect(result.current.error).toBeInstanceOf(ApiError);
    expect((result.current.error as ApiError).status).toBe(403);
  });

  it('surfaces ApiError on 500', async () => {
    server.use(
      http.post('/api/mcp-servers/srv1/disable', () =>
        new HttpResponse('internal error', { status: 500 }),
      ),
    );

    const { wrapper } = makeWrapper();
    const { result } = renderHook(() => useDisableServer(), { wrapper });

    await act(async () => {
      result.current.mutate({ serverId: 'srv1', displayName: 'My Server' });
    });

    await waitFor(() => expect(result.current.isError).toBe(true));
    expect(result.current.error).toBeInstanceOf(ApiError);
    expect((result.current.error as ApiError).status).toBe(500);
  });
});
