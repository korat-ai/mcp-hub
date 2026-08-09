import { useMutation, useQueryClient } from '@tanstack/react-query';
import { api, ApiError } from '@/lib/api';
import { queryKeys } from '@/lib/queryKeys';
import { toastReceipt } from '@/lib/toast';

interface DenyArgs {
  requestId: string;
  /** Human-readable agent label for the toast only (display name ?? short id). */
  agentLabel: string;
  /** Human-readable server label for the toast only — not sent to the API
   *  (the deny endpoint resolves the server from requestId). */
  serverLabel: string;
}

export function useDenyRequest() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: ({ requestId }: DenyArgs) => api.accessRequests.deny(requestId),
    onSuccess: (_, vars) => {
      qc.invalidateQueries({ queryKey: queryKeys.space.all });
      qc.invalidateQueries({ queryKey: queryKeys.accessRequests.byId(vars.requestId) });
      toastReceipt('bad', 'access denied', `${vars.agentLabel} → ${vars.serverLabel}`);
    },
    onError: (err, vars) => {
      const subject =
        vars.agentLabel && vars.serverLabel
          ? `${vars.agentLabel} → ${vars.serverLabel}`
          : err instanceof ApiError
            ? err.message
            : 'unknown error';
      toastReceipt('bad', 'access denial failed', subject);
    },
  });
}
