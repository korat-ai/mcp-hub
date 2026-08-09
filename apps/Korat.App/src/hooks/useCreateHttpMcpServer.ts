import { useMutation, useQueryClient } from '@tanstack/react-query';
import { api } from '@/lib/api';
import { queryKeys } from '@/lib/queryKeys';
import { toastReceipt } from '@/lib/toast';
import type { CreateHttpMcpServerRequest } from '@/types/mcpServers';

/**
 * POST /api/mcp-servers (Increment 1, HTTP MCP direct-to-Space — Task 3/Task 7). On success,
 * invalidates the space query so the new server appears in the catalog on refetch — the create
 * response itself is not cached directly (mirrors useCreateInferencePoint.ts's rationale).
 * gcTime: 0 — mirrors useCreateInferencePoint.ts (m13): the mutation's `variables` holds the
 * plaintext secret for the duration the mutation object is retained; do not let it linger in
 * the MutationCache after every observer has unmounted.
 */
export function useCreateHttpMcpServer() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (body: CreateHttpMcpServerRequest) => api.mcpServers.create(body),
    onSuccess: (_created, body) => {
      qc.invalidateQueries({ queryKey: queryKeys.space.all });
      toastReceipt('good', 'HTTP MCP server created', body.displayName);
    },
    // Mirrors useCreateInferencePoint.ts's B3 rationale: create failures are surfaced INLINE by
    // the form; this empty handler suppresses the global mutations.onError toast so a failure
    // doesn't show BOTH the form's mapped inline error AND a raw-JSON toast.
    onError: () => {},
    gcTime: 0,
  });
}
