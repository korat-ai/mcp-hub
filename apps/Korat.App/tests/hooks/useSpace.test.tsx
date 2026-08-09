import { describe, expect, it } from 'vitest';
import { renderHook, waitFor } from '@testing-library/react';
import { http, HttpResponse } from 'msw';
import { server } from '../setup';
import { useSpace } from '@/hooks/useSpace';
import { ApiError } from '@/lib/api';
import { withQueryClient } from '../test-utils';
import { spaceQueryOptions } from '@/lib/queries/space';
import { POLL_INTERVAL_MS } from '@/lib/polling';

describe('useSpace', () => {
  it('returns space data on success', async () => {
    const { result } = renderHook(() => useSpace(), {
      wrapper: ({ children }) => withQueryClient(children),
    });
    await waitFor(() => expect(result.current.isSuccess).toBe(true));
    expect(result.current.data?.displayName).toBe('Test Space');
  });

  it('exposes ApiError on 401', async () => {
    server.use(http.get('/api/space', () => new HttpResponse('nope', { status: 401 })));
    const { result } = renderHook(() => useSpace(), {
      wrapper: ({ children }) => withQueryClient(children),
    });
    await waitFor(() => expect(result.current.isError).toBe(true));
    expect(result.current.error).toBeInstanceOf(ApiError);
    expect((result.current.error as ApiError).status).toBe(401);
  });
});

// ---------------------------------------------------------------------------
// Unit-level tests for spaceQueryOptions polling strategy (no timers needed).
// ---------------------------------------------------------------------------
describe('spaceQueryOptions refetchInterval', () => {
  function makeQueryStub(error: unknown) {
    // Minimal Query-like object expected by the refetchInterval function.
    return { state: { error } } as Parameters<
      Exclude<ReturnType<typeof spaceQueryOptions>['refetchInterval'], number | boolean | undefined>
    >[0];
  }

  const getInterval = () => {
    const opts = spaceQueryOptions();
    // refetchInterval is guaranteed to be a function after Fix 2.
    return opts.refetchInterval as (query: ReturnType<typeof makeQueryStub>) => number | false;
  };

  it('returns POLL_INTERVAL_MS when there is no error', () => {
    const interval = getInterval();
    expect(interval(makeQueryStub(null))).toBe(POLL_INTERVAL_MS);
  });

  it('returns POLL_INTERVAL_MS for non-401 API errors (e.g. 500)', () => {
    const interval = getInterval();
    expect(interval(makeQueryStub(new ApiError(500, 'oops')))).toBe(POLL_INTERVAL_MS);
  });

  it('returns false (stops polling) when the error is ApiError 401', () => {
    const interval = getInterval();
    expect(interval(makeQueryStub(new ApiError(401, 'nope')))).toBe(false);
  });

  it('returns POLL_INTERVAL_MS for generic non-ApiError errors', () => {
    const interval = getInterval();
    expect(interval(makeQueryStub(new Error('network error')))).toBe(POLL_INTERVAL_MS);
  });
});
