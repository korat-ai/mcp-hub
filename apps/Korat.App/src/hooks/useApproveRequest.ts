import { useMutation, useQueryClient } from '@tanstack/react-query';
import { api, ApiError } from '@/lib/api';
import { queryKeys } from '@/lib/queryKeys';
import { toastReceipt } from '@/lib/toast';

interface ApproveArgs {
  requestId: string;
  /** Human-readable agent label for the toast only (display name ?? short id). */
  agentLabel: string;
  /** Human-readable server label for the success toast only — not sent to the API
   *  (the approve endpoint resolves the server from requestId). */
  serverLabel: string;
}

export function useApproveRequest() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: ({ requestId }: ApproveArgs) => api.accessRequests.approve(requestId),
    onSuccess: (_, vars) => {
      qc.invalidateQueries({ queryKey: queryKeys.space.all });
      qc.invalidateQueries({ queryKey: queryKeys.grants.all });
      qc.invalidateQueries({ queryKey: queryKeys.accessRequests.byId(vars.requestId) });
      toastReceipt('good', 'access approved', `${vars.agentLabel} → ${vars.serverLabel}`);
    },
    onError: (err, vars) => {
      const subject =
        vars.agentLabel && vars.serverLabel
          ? `${vars.agentLabel} → ${vars.serverLabel}`
          : err instanceof ApiError
            ? err.message
            : 'unknown error';
      toastReceipt('bad', 'access approval failed', subject);
    },
  });
}
