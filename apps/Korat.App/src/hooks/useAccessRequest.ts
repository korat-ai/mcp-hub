import { useQuery } from '@tanstack/react-query';
import { api, ApiError } from '@/lib/api';
import { queryKeys } from '@/lib/queryKeys';
import { POLL_INTERVAL_MS, POLL_STALE_TIME_MS } from '@/lib/polling';

/**
 * Per-id polling of /api/access-requests/:id. Polls every 5s while the request
 * is Pending; stops once it reaches a terminal state (Approved/Denied/Expired/
 * Canceled) so the page doesn't keep hammering /api after the user has acted.
 */
export function useAccessRequest(requestId: string) {
  return useQuery({
    queryKey: queryKeys.accessRequests.byId(requestId),
    queryFn: () => api.accessRequests.get(requestId),
    refetchInterval: (q) => {
      const data = q.state.data;
      return data && data.status !== 'Pending' ? false : POLL_INTERVAL_MS;
    },
    refetchIntervalInBackground: false,
    staleTime: POLL_STALE_TIME_MS,
    retry: (count, err) => err instanceof ApiError && err.status >= 500 && count < 2,
  });
}
