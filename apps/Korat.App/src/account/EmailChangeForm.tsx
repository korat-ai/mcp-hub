/**
 * EmailChangeForm — submit a new email address for re-verification.
 *
 * Behaviour (spec §3.2, SC-5, SC-10):
 *  - Shows the user's current primary email for reference.
 *  - Submits to POST /api/auth/email/change via useRequestEmailChange.
 *  - On success (202): shows "check your inbox" pending state; does NOT mutate auth.me
 *    (the email is not promoted until the user clicks the verification link).
 *  - Anti-enumeration: the backend returns 202 even when the address is taken, so no
 *    409 "already in use" branch exists here. A verification link is sent only when
 *    the address is genuinely available; otherwise the request is silently absorbed.
 *  - 429 → "Too many requests — try again later."
 *  - Other API errors → generic message from error.message.
 *  - Submit disabled while mutation is pending (no double-submit).
 */
import { useState } from 'react';
import type { MeDto } from '@/types/api';
import { useRequestEmailChange } from '@/account/hooks';
import { ApiError } from '@/lib/api';

interface Props {
  me: MeDto;
}

function mapError(err: unknown): string {
  if (err instanceof ApiError) {
    // No 409 branch: the backend returns 202 for both success and email-already-in-use
    // (anti-enumeration posture). A 409 from this endpoint is unexpected.
    if (err.status === 429) return 'Too many requests — try again later.';
    if (err.status === 400) {
      // Parse structured backend error when available; avoid surfacing raw ApiError string.
      try {
        const body = JSON.parse(err.body) as { error?: string };
        if (body.error === 'same-as-current')
          return 'That is already your primary email address.';
      } catch {
        // non-JSON 400 — fall through to generic message
      }
    }
  }
  return 'Something went wrong. Please try again.';
}

export function EmailChangeForm({ me }: Props) {
  const [newEmail, setNewEmail] = useState('');
  // Local sent state supplements the server-side pendingEmailChange — local state
  // covers the immediate "just submitted" UX while server state covers page reloads.
  const [sent, setSent] = useState(false);
  const [sentTo, setSentTo] = useState('');
  const [localError, setLocalError] = useState<string | null>(null);

  const request = useRequestEmailChange();

  // Derive the effective "pending" display from either local state (just submitted)
  // or server-persisted state (page reload). Local takes precedence.
  const serverPending = me.pendingEmailChange;
  const showPending = sent || !!serverPending;
  const pendingAddress = sent ? sentTo : serverPending?.newEmail ?? '';
  const pendingExpiresAt = serverPending?.expiresAt;

  function handleSubmit(e: React.FormEvent) {
    e.preventDefault();
    setLocalError(null);

    const trimmed = newEmail.trim();
    if (!trimmed) {
      setLocalError('Please enter a new email address.');
      return;
    }
    if (trimmed.toLowerCase() === me.primaryEmail.toLowerCase()) {
      setLocalError('That is already your primary email address.');
      return;
    }

    request.mutate(
      { newEmail: trimmed },
      {
        onSuccess: () => {
          setSentTo(trimmed);
          setSent(true);
          setNewEmail('');
        },
        onError: (err) => {
          setLocalError(mapError(err));
        },
      },
    );
  }

  const errorMessage = localError;

  return (
    <form onSubmit={handleSubmit} className="space-y-3">
      <p className="text-sm text-muted-foreground">
        Current email: <span className="font-medium text-foreground">{me.primaryEmail}</span>
      </p>

      {showPending ? (
        <div className="rounded-md bg-muted px-4 py-3 text-sm">
          <p className="font-medium">Check your inbox</p>
          <p className="text-muted-foreground mt-1">
            A verification link has been sent to <strong>{pendingAddress}</strong>. Click it to
            confirm the change.
          </p>
          {pendingExpiresAt && (
            <p className="text-muted-foreground text-xs mt-1">
              Link expires at {new Date(pendingExpiresAt).toLocaleTimeString()}.
            </p>
          )}
          <p className="text-xs text-amber-600 dark:text-amber-400 mt-1">
            Requesting a new address will invalidate this link.
          </p>
          <button
            type="button"
            onClick={() => setSent(false)}
            className="mt-2 text-xs underline underline-offset-2 hover:no-underline"
          >
            Send to a different address
          </button>
        </div>
      ) : (
        <>
          <div className="space-y-1.5">
            <label htmlFor="new-email" className="block text-sm font-medium">
              New email address
            </label>
            <input
              id="new-email"
              type="email"
              value={newEmail}
              onChange={(e) => {
                setNewEmail(e.target.value);
                setLocalError(null);
              }}
              placeholder="new@example.com"
              className="w-full rounded-md border border-input bg-background px-3 py-2 text-sm focus:outline-none focus:ring-2 focus:ring-ring"
              aria-describedby={errorMessage ? 'email-change-error' : undefined}
              disabled={request.isPending}
            />
            {errorMessage && (
              <p
                id="email-change-error"
                role="alert"
                className="text-xs text-destructive"
              >
                {errorMessage}
              </p>
            )}
          </div>
          <button
            type="submit"
            disabled={request.isPending}
            className="inline-flex items-center rounded-md bg-primary px-3 py-1.5 text-sm font-medium text-primary-foreground hover:bg-primary/90 disabled:opacity-50 transition-colors"
            aria-label="Send verification link"
          >
            {request.isPending ? 'Sending...' : 'Send verification link'}
          </button>
        </>
      )}
    </form>
  );
}
