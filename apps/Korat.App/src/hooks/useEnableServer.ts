import { useMutation, useQueryClient } from '@tanstack/react-query';
import { api, ApiError } from '@/lib/api';
import { queryKeys } from '@/lib/queryKeys';
import { toastReceipt } from '@/lib/toast';

interface EnableArgs {
  serverId: string;
  displayName: string;
}

export function useEnableServer() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: ({ serverId }: EnableArgs) => api.mcpServers.enable(serverId),
    onSuccess: (_, vars) => {
      qc.invalidateQueries({ queryKey: queryKeys.space.all });
      toastReceipt('good', 'server enabled', vars.displayName);
    },
    onError: (err, vars) => {
      toastReceipt(
        'bad',
        'server enable failed',
        vars.displayName || (err instanceof ApiError ? err.message : 'unknown error'),
      );
    },
  });
}
