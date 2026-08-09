import { useMutation, useQueryClient } from '@tanstack/react-query';
import { api, ApiError } from '@/lib/api';
import { queryKeys } from '@/lib/queryKeys';
import { toastReceipt } from '@/lib/toast';

interface ReconnectArgs {
  serverId: string;
  displayName: string;
}

/**
 * Increment 2 (HTTP MCP OAuth, Task 6): mirrors useEnableServer.ts's shape. On success, if the
 * server returned an authorizeUrl, redirects the browser to it (starting the OAuth consent round
 * trip) — otherwise (a discovery/DCR failure) surfaces the connect.error as a toast.
 */
export function useReconnectServer() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: ({ serverId }: ReconnectArgs) => api.mcpServers.reconnect(serverId),
    onSuccess: (connect, _vars) => {
      qc.invalidateQueries({ queryKey: queryKeys.space.all });
      if (connect.authorizeUrl) {
        window.location.href = connect.authorizeUrl;
        return;
      }
      toastReceipt('bad', 'reconnect failed', connect.error ?? 'unknown error');
    },
    onError: (err, vars) => {
      toastReceipt(
        'bad',
        'reconnect failed',
        vars.displayName || (err instanceof ApiError ? err.message : 'unknown error'),
      );
    },
  });
}
