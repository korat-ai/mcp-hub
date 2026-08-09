import { useMutation, useQueryClient } from '@tanstack/react-query';
import { api, ApiError } from '@/lib/api';
import { queryKeys } from '@/lib/queryKeys';
import { toastReceipt } from '@/lib/toast';

interface RevokeArgs {
  grantId: string;
  /** Human-readable labels for the success toast only (friendly name, falling
   *  back to a shortId — see grants.tsx targetAgentName/targetServerName) —
   *  not sent to the API (the revoke endpoint only needs grantId). */
  agentName: string;
  serverName: string;
}

export function useRevokeGrant() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: ({ grantId }: RevokeArgs) => api.grants.revoke(grantId),
    onSuccess: (_, vars) => {
      qc.invalidateQueries({ queryKey: queryKeys.grants.all });
      qc.invalidateQueries({ queryKey: queryKeys.space.all });
      toastReceipt('bad', 'permission revoked', `${vars.agentName} → ${vars.serverName}`);
    },
    onError: (err, vars) => {
      const subject =
        vars.agentName && vars.serverName
          ? `${vars.agentName} → ${vars.serverName}`
          : err instanceof ApiError
            ? err.message
            : 'unknown error';
      toastReceipt('bad', 'permission revoke failed', subject);
    },
  });
}
