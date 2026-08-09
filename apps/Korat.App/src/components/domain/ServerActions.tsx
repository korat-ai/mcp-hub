// ServerActions — the server action control (spec 021 Layer 3; Increment 2 adds NeedsReauth):
//   Available   → Disable    (reversible, no confirm)
//   Disabled    → Enable     (reversible, no confirm)
//   NeedsReauth → Reconnect  (starts a fresh oauth authorize round trip — full-page redirect)
//   Unavailable → Delete     (destructive, confirm dialog — purges an orphan
//                             server whose publishing node is gone for good)
//
// Shared between servers.tsx (list row) and servers.$serverId.tsx (detail
// header) so the availability→action mapping, the per-server isPending
// derivation (`mutation.variables?.serverId === serverId`, since the caller
// passes one shared mutation instance across every row), and the
// delete→ConfirmDialog wiring live in exactly one place.
import { useState } from 'react';
import { Button } from '@/components/ui/button';
import { ConfirmDialog } from '@/components/domain/ConfirmDialog';
import type { ServerAvailability } from '@/lib/presence';
import type { useDisableServer } from '@/hooks/useDisableServer';
import type { useEnableServer } from '@/hooks/useEnableServer';
import type { useDeleteServer } from '@/hooks/useDeleteServer';
import type { useReconnectServer } from '@/hooks/useReconnectServer';

interface Props {
  serverId: string;
  displayName: string;
  availability: ServerAvailability;
  disable: ReturnType<typeof useDisableServer>;
  enable: ReturnType<typeof useEnableServer>;
  deleteSrv: ReturnType<typeof useDeleteServer>;
  reconnect: ReturnType<typeof useReconnectServer>;
  /** Called after a successful delete — e.g. the detail page navigates back to /servers. */
  onDeleted?: () => void;
}

export function ServerActions({
  serverId, displayName, availability, disable, enable, deleteSrv, reconnect, onDeleted,
}: Props) {
  const [confirmDelete, setConfirmDelete] = useState(false);

  const disablePending = disable.isPending && disable.variables?.serverId === serverId;
  const enablePending = enable.isPending && enable.variables?.serverId === serverId;
  const deletePending = deleteSrv.isPending && deleteSrv.variables?.serverId === serverId;
  const reconnectPending = reconnect.isPending && reconnect.variables?.serverId === serverId;
  const anyPending = disablePending || enablePending || deletePending || reconnectPending;

  return (
    <>
      {availability === 'Available' ? (
        // No confirm dialog: disable is reversible.
        <Button
          variant="outline"
          size="sm"
          disabled={anyPending}
          onClick={() => disable.mutate({ serverId, displayName })}
        >
          Disable
        </Button>
      ) : availability === 'Disabled' ? (
        // Disabled → reversible: bring it back instead of forcing a delete.
        <Button
          variant="outline"
          size="sm"
          disabled={anyPending}
          onClick={() => enable.mutate({ serverId, displayName })}
        >
          Enable
        </Button>
      ) : availability === 'NeedsReauth' ? (
        // Increment 2: a pending-consent or refresh-failed oauth server — Reconnect starts a
        // fresh authorize round trip (full-page redirect), never a Delete-by-default like the
        // generic Unavailable branch below.
        <Button
          variant="default"
          size="sm"
          disabled={anyPending}
          onClick={() => reconnect.mutate({ serverId, displayName })}
        >
          {reconnectPending ? 'Reconnecting…' : 'Reconnect'}
        </Button>
      ) : (
        // Unavailable (published but node offline/not asserting): offer delete
        // to purge orphan servers (spec 021 Layer 3).
        <Button
          variant="destructive"
          size="sm"
          disabled={anyPending}
          onClick={() => setConfirmDelete(true)}
        >
          Delete
        </Button>
      )}

      <ConfirmDialog
        open={confirmDelete}
        onOpenChange={setConfirmDelete}
        title={`Delete "${displayName}"?`}
        description="This permanently removes the server from the Space catalog. Any agents currently connected through it will lose access immediately."
        confirmLabel="Delete"
        destructive
        isPending={deleteSrv.isPending}
        onConfirm={() => {
          deleteSrv.mutate(
            { serverId, displayName },
            {
              onSuccess: () => {
                setConfirmDelete(false);
                onDeleted?.();
              },
            },
          );
        }}
      />
    </>
  );
}
