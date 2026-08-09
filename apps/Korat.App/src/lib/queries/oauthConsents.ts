import { queryOptions } from '@tanstack/react-query';
import { api, ApiError } from '@/lib/api';
import { queryKeys } from '@/lib/queryKeys';
import { POLL_INTERVAL_MS, POLL_STALE_TIME_MS } from '@/lib/polling';

/** Canonical /api/oauth/consents query options (Space-MCP inc-2a, Task 8). */
export const oauthConsentsQueryOptions = () =>
  queryOptions<Awaited<ReturnType<typeof api.oauthConsents.list>>, Error>({
    queryKey: queryKeys.oauthConsents.list(),
    queryFn: () => api.oauthConsents.list(),
    refetchInterval: POLL_INTERVAL_MS,
    refetchIntervalInBackground: false,
    staleTime: POLL_STALE_TIME_MS,
    retry: (count, err) => err instanceof ApiError && err.status >= 500 && count < 2,
  });
