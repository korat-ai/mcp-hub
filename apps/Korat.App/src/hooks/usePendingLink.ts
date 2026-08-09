import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { api } from '@/lib/api'
import { queryKeys } from '@/lib/queryKeys'

export function usePendingLink() {
  return useQuery({
    queryKey: queryKeys.pendingLink.all,
    queryFn: api.auth.pendingLink.read,
    retry: false,
  })
}

export function useConfirmPendingLink() {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: api.auth.pendingLink.confirm,
    onSuccess: () => {
      // After confirm the pending cookie is gone server-side: removeQueries (not
      // invalidate) so an unmounting LinkConfirmPage doesn't trigger a guaranteed
      // 404 refetch.
      qc.removeQueries({ queryKey: queryKeys.pendingLink.all })
      // Reset (not invalidate) the space cache: if the user reached this page via
      // an unauthenticated IdP redirect, the cache holds ApiError(401). Confirm
      // issues a fresh session cookie, but AuthGate's useEffect would still see
      // the stale error and bounce back to /signin. Reset clears the error so the
      // next /api/space fetch fires fresh.
      qc.resetQueries({ queryKey: queryKeys.space.all })
    },
  })
}

export function useCancelPendingLink() {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: api.auth.pendingLink.cancel,
    onSuccess: () => {
      // Cookie deleted server-side; cache entry would 404 on refetch.
      qc.removeQueries({ queryKey: queryKeys.pendingLink.all })
    },
  })
}
