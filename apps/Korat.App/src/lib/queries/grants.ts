import { queryOptions } from '@tanstack/react-query';
import { api, ApiError } from '@/lib/api';
import { queryKeys } from '@/lib/queryKeys';
import { POLL_INTERVAL_MS, POLL_STALE_TIME_MS } from '@/lib/polling';

/** Canonical /api/grants query options. */
export const grantsQueryOptions = () =>
  queryOptions<Awaited<ReturnType<typeof api.grants.list>>, Error>({
    queryKey: queryKeys.grants.all,
    queryFn: () => api.grants.list(),
    refetchInterval: POLL_INTERVAL_MS,
    refetchIntervalInBackground: false,
    staleTime: POLL_STALE_TIME_MS,
    retry: (count, err) => err instanceof ApiError && err.status >= 500 && count < 2,
  });
