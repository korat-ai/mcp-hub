import { useMutation, useQueryClient } from '@tanstack/react-query';
import { api, ApiError } from '@/lib/api';
import { queryKeys } from '@/lib/queryKeys';
import { toastReceipt } from '@/lib/toast';

interface DisableArgs {
  serverId: string;
  displayName: string;
}

export function useDisableServer() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: ({ serverId }: DisableArgs) => api.mcpServers.disable(serverId),
    onSuccess: (_, vars) => {
      qc.invalidateQueries({ queryKey: queryKeys.space.all });
      toastReceipt('bad', 'server disabled', vars.displayName);
    },
    onError: (err, vars) => {
      toastReceipt(
        'bad',
        'server disable failed',
        vars.displayName || (err instanceof ApiError ? err.message : 'unknown error'),
      );
    },
  });
}
