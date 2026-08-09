import { queryOptions } from '@tanstack/react-query';
import { api, ApiError } from '@/lib/api';
import { queryKeys } from '@/lib/queryKeys';
import { POLL_INTERVAL_MS, POLL_STALE_TIME_MS } from '@/lib/polling';

/** Canonical /api/sessions query options. */
export const sessionsQueryOptions = () =>
  queryOptions<Awaited<ReturnType<typeof api.sessions.list>>, Error>({
    queryKey: queryKeys.sessions.all,
    queryFn: () => api.sessions.list(),
    refetchInterval: POLL_INTERVAL_MS,
    refetchIntervalInBackground: false,
    staleTime: POLL_STALE_TIME_MS,
    retry: (count, err) => err instanceof ApiError && err.status >= 500 && count < 2,
  });
