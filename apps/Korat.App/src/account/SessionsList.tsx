/**
 * SessionsList — display active sessions, badge the current one, and allow
 * revoking any non-current session via a confirmation dialog.
 *
 * Behaviour (spec §3.3, SC-2, SC-3):
 *  - Reads sessions from useSessions (GET /api/auth/sessions).
 *  - The backend marks the caller's own session with `current: true`; this
 *    component badges it "This device" and hides its Revoke button.
 *  - Sign-out of the current device is handled by UserMenu (useSignOut), not here.
 *  - Revoking a non-current session invalidates auth.sessions so the list
 *    refreshes automatically.
 *  - Revoke flows through ConfirmRevokeDialog (no accidental one-click revoke).
 *  - A failed revoke surfaces an inline error inside the dialog; the dialog
 *    stays open so the user can retry or cancel.
 *  - A failed sessions load shows an error message instead of pulsing forever.
 */
import { useState } from 'react';
import { useSessions, useRevokeSession, useRevokeOtherSessions } from '@/account/hooks';
import { ConfirmRevokeDialog } from '@/account/ConfirmRevokeDialog';
import { Badge } from '@/components/ui/badge';

function formatDate(iso: string | null | undefined): string {
  if (!iso) return 'Unknown';
  const d = new Date(iso);
  return isNaN(d.getTime()) ? 'Unknown' : d.toLocaleString();
}

type PendingRevoke = { id: string };

export function SessionsList() {
  const { data: sessions, isLoading, isError } = useSessions();
  const revoke = useRevokeSession();
  const revokeOthers = useRevokeOtherSessions();
  const [pending, setPending] = useState<PendingRevoke | null>(null);
  const [revokeError, setRevokeError] = useState<string | null>(null);
  const [confirmOthers, setConfirmOthers] = useState(false);
  const [othersError, setOthersError] = useState<string | null>(null);

  if (isLoading) {
    return (
      <div className="space-y-3">
        {[...Array(2)].map((_, i) => (
          <div key={i} className="h-14 rounded-lg bg-muted animate-pulse" />
        ))}
      </div>
    );
  }

  if (isError || !sessions) {
    return (
      <p className="text-sm text-destructive py-2" role="alert">
        Could not load sessions — please refresh the page.
      </p>
    );
  }

  function handleRevokeClick(id: string) {
    setRevokeError(null);
    setPending({ id });
  }

  function handleConfirm() {
    if (!pending) return;
    revoke.mutate(pending.id, {
      onSuccess: () => {
        setPending(null);
      },
      onError: (err) => {
        const msg =
          err instanceof Error ? err.message : 'Something went wrong. Please try again.';
        setRevokeError(msg);
      },
    });
  }

  function handleConfirmOthers() {
    revokeOthers.mutate(undefined, {
      onSuccess: () => setConfirmOthers(false),
      onError: (err) => {
        setOthersError(
          err instanceof Error ? err.message : 'Something went wrong. Please try again.',
        );
      },
    });
  }

  const otherCount = sessions.filter((s) => !s.current).length;

  return (
    <>
      <ul className="divide-y divide-border rounded-lg border">
        {sessions.map((session) => (
          <li key={session.id} className="flex items-center justify-between gap-4 px-4 py-3">
            <div className="min-w-0 flex-1">
              <p className="truncate text-sm font-medium">
                {session.userAgent ?? 'Unknown device'}
              </p>
              <p className="text-xs text-muted-foreground mt-0.5">
                Last used: {formatDate(session.lastUsedAt)}
                {session.createdFromIp ? ` · ${session.createdFromIp}` : ''}
              </p>
            </div>
            <div className="flex items-center gap-2 shrink-0">
              {session.current && (
                <Badge variant="secondary">This device</Badge>
              )}
              {!session.current && (
                <button
                  type="button"
                  onClick={() => handleRevokeClick(session.id)}
                  className="text-xs text-destructive hover:underline focus:outline-none"
                  aria-label={`Revoke session for ${session.userAgent ?? 'unknown device'}`}
                >
                  Revoke
                </button>
              )}
            </div>
          </li>
        ))}
      </ul>

      {otherCount > 0 && (
        <div className="mt-3 flex justify-end">
          <button
            type="button"
            onClick={() => {
              setOthersError(null);
              setConfirmOthers(true);
            }}
            className="text-xs text-destructive hover:underline focus:outline-none"
          >
            Revoke all other sessions
          </button>
        </div>
      )}

      {confirmOthers && (
        <ConfirmRevokeDialog
          open
          onOpenChange={(open) => {
            if (!open) {
              setConfirmOthers(false);
              setOthersError(null);
            }
          }}
          title="Revoke all other sessions?"
          body="This signs out every device except this one. They will need to sign in again."
          onConfirm={handleConfirmOthers}
          pending={revokeOthers.isPending}
          error={othersError}
        />
      )}

      {pending && (
        <ConfirmRevokeDialog
          open
          onOpenChange={(open) => {
            if (!open) {
              setPending(null);
              setRevokeError(null);
            }
          }}
          title="Revoke session?"
          body="This will immediately revoke the session. The device will be signed out."
          onConfirm={handleConfirm}
          pending={revoke.isPending}
          error={revokeError}
        />
      )}
    </>
  );
}
