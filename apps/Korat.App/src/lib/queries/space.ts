import { queryOptions } from '@tanstack/react-query';
import { api, ApiError } from '@/lib/api';
import { queryKeys } from '@/lib/queryKeys';
import { POLL_INTERVAL_MS, POLL_STALE_TIME_MS } from '@/lib/polling';

/** Canonical /api/space query options. Consumed by AuthGate and useSpace
 *  (Task 9). tanstack-query observers share one network request via key. */
export const spaceQueryOptions = () =>
  queryOptions<Awaited<ReturnType<typeof api.space.get>>, Error>({
    queryKey: queryKeys.space.all,
    queryFn: () => api.space.get(),
    // Stop interval polling once a 401 is received — no point hammering the
    // server repeatedly while the user is unauthenticated. AuthGate still
    // redirects to /signin on the first 401 error. Polling resumes naturally
    // on a fresh page load / sign-in because the query state is reset then.
    refetchInterval: (query) => {
      const err = query.state.error;
      if (err instanceof ApiError && err.status === 401) return false;
      return POLL_INTERVAL_MS;
    },
    refetchIntervalInBackground: false,
    staleTime: POLL_STALE_TIME_MS,
    // Do not retry on 401 — it will not succeed without re-authentication.
    retry: (count: number, err: unknown) =>
      err instanceof ApiError && err.status >= 500 && count < 2,
  });
