import { useMutation, useQueryClient } from '@tanstack/react-query';
import { api, ApiError } from '@/lib/api';
import { queryKeys } from '@/lib/queryKeys';
import { toastReceipt } from '@/lib/toast';

interface DeleteArgs {
  serverId: string;
  displayName: string;
}

export function useDeleteServer() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: ({ serverId }: DeleteArgs) => api.mcpServers.delete(serverId),
    onSuccess: (_, vars) => {
      qc.invalidateQueries({ queryKey: queryKeys.space.all });
      toastReceipt('bad', 'server deleted', vars.displayName);
    },
    onError: (err, vars) => {
      toastReceipt(
        'bad',
        'server delete failed',
        vars.displayName || (err instanceof ApiError ? err.message : 'unknown error'),
      );
    },
  });
}
