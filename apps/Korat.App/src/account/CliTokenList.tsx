/**
 * CliTokenList — display issued CLI tokens and allow revoking them.
 *
 * Behaviour (spec §3.5, SC-9):
 *  - Reads tokens from useCliTokens (GET /api/cli/tokens).
 *  - Each token row shows name, createdAt, and lastUsedAt (or "Never").
 *  - Revoke flows through ConfirmRevokeDialog (no accidental one-click revoke).
 *  - Successful revoke invalidates cli.tokens so the list refreshes automatically.
 *  - A failed revoke surfaces an inline error inside the dialog; the dialog
 *    stays open so the user can retry or cancel.
 *  - Empty state rendered when the list is empty.
 */
import { useState } from 'react';
import { useCliTokens, useRevokeCliToken } from '@/account/hooks';
import { ConfirmRevokeDialog } from '@/account/ConfirmRevokeDialog';

type PendingRevoke = { id: string; name: string };

function formatDate(iso: string | null | undefined): string {
  if (!iso) return 'Never';
  return new Date(iso).toLocaleString();
}

export function CliTokenList() {
  const { data: tokens, isLoading, isError } = useCliTokens();
  const revoke = useRevokeCliToken();
  const [pending, setPending] = useState<PendingRevoke | null>(null);
  const [revokeError, setRevokeError] = useState<string | null>(null);

  if (isLoading) {
    return (
      <div className="space-y-3">
        {[...Array(2)].map((_, i) => (
          <div key={i} className="h-14 rounded-lg bg-muted animate-pulse" />
        ))}
      </div>
    );
  }

  if (isError || !tokens) {
    return (
      <p className="text-sm text-destructive py-2" role="alert">
        Could not load CLI tokens — please refresh the page.
      </p>
    );
  }

  if (tokens.length === 0) {
    return (
      <p className="text-sm text-muted-foreground py-4 text-center">
        No CLI tokens issued yet.
      </p>
    );
  }

  function handleRevokeClick(id: string, name: string) {
    setRevokeError(null);
    setPending({ id, name });
  }

  function handleConfirm() {
    if (!pending) return;
    const { id } = pending;
    revoke.mutate(id, {
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

  return (
    <>
      <ul className="divide-y divide-border rounded-lg border">
        {tokens.map((token) => (
          <li key={token.id} className="flex items-center justify-between gap-4 px-4 py-3">
            <div className="min-w-0 flex-1">
              <p className="truncate text-sm font-medium">{token.name}</p>
              <p className="text-xs text-muted-foreground mt-0.5">
                Created: {formatDate(token.createdAt)}
                {' · '}
                Last used: {formatDate(token.lastUsedAt)}
                {token.expiresAt ? ` · Expires: ${formatDate(token.expiresAt)}` : ''}
              </p>
            </div>
            <div className="shrink-0">
              <button
                type="button"
                onClick={() => handleRevokeClick(token.id, token.name)}
                className="text-xs text-destructive hover:underline focus:outline-none"
                aria-label={`Revoke token ${token.name}`}
              >
                Revoke
              </button>
            </div>
          </li>
        ))}
      </ul>

      {pending && (
        <ConfirmRevokeDialog
          open
          onOpenChange={(open) => {
            if (!open) {
              setPending(null);
              setRevokeError(null);
            }
          }}
          title="Revoke CLI token?"
          body={`This will immediately revoke the token "${pending.name}". Any CLI session using it will lose access.`}
          onConfirm={handleConfirm}
          pending={revoke.isPending}
          error={revokeError}
          confirmLabel="Revoke token"
        />
      )}
    </>
  );
}
