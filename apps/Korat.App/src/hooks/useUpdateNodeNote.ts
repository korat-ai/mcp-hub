import { useMutation, useQueryClient } from '@tanstack/react-query';
import { api } from '@/lib/api';
import { queryKeys } from '@/lib/queryKeys';
import { toastReceipt } from '@/lib/toast';

interface UpdateNodeNoteArgs {
  nodeId: string;
  displayName: string;
  /** null clears the note. */
  note: string | null;
}

/**
 * PATCH /api/nodes/{id} — owner-editable Note (node-visibility-doctor design, 2026-07-02).
 * Mirrors useUpdateInferencePoint's shape: on success, invalidates the query that actually
 * surfaces node.note — there is no dedicated GET /api/nodes/{id}, /api/space is the only place
 * a node's fields (including Note) are read, by both the nodes list and the node detail page.
 */
export function useUpdateNodeNote() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: ({ nodeId, note }: UpdateNodeNoteArgs) => api.nodes.update(nodeId, { note }),
    onSuccess: (_updated, vars) => {
      qc.invalidateQueries({ queryKey: queryKeys.space.all });
      toastReceipt('good', vars.note ? 'node note saved' : 'node note cleared', vars.displayName);
    },
    // Mirrors useUpdateInferencePoint: the caller surfaces failures INLINE (next to the note
    // editor) via its own per-call onError — this empty handler suppresses main.tsx's global
    // mutations.onError default so the failure isn't toasted twice.
    onError: () => {},
  });
}
