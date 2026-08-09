import { useMutation, useQueryClient } from '@tanstack/react-query';
import { api, ApiError } from '@/lib/api';
import { queryKeys } from '@/lib/queryKeys';
import { toastReceipt } from '@/lib/toast';

interface RevokeArgs {
  consentId: string;
  /** Label for the toast only — not sent to the API. */
  clientName: string;
}

/** Space-MCP inc-2a, Task 8: revoke an OAuth consent — kills its tokens and tears down
 *  every live MCP session for the derived (client × owner × Space) identity server-side. */
export function useRevokeOAuthConsent() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: ({ consentId }: RevokeArgs) => api.oauthConsents.revoke(consentId),
    onSuccess: (_, vars) => {
      qc.invalidateQueries({ queryKey: queryKeys.oauthConsents.all });
      toastReceipt('bad', 'access revoked', vars.clientName);
    },
    onError: (err, vars) => {
      const subject = vars.clientName || (err instanceof ApiError ? err.message : 'unknown error');
      toastReceipt('bad', 'revoke failed', subject);
    },
  });
}
