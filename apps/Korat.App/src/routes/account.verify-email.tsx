/**
 * /account/verify-email — email-change verification callback.
 *
 * The user lands here after clicking the magic-link sent to their new address.
 * This route:
 *  1. Reads the `?token=` search param.
 *  2. Immediately strips the token from the URL via history.replaceState before
 *     any subresource or XHR fires, so the raw token never travels in a Referer
 *     header to analytics / fonts / error reporters.
 *  3. Calls POST /api/auth/email/change/confirm via useConfirmEmailChange.
 *  4. On success: invalidates auth.me (handled by the hook), shows confirmation,
 *     and navigates away with `replace: true` so the token is NOT kept in history.
 *  5. On error (expired/used/invalid): shows a recoverable error UI with a link
 *     back to /account/security to request a new change. Never promotes email.
 *
 * Security notes:
 *  - A `<meta name="referrer" content="no-referrer">` tag is rendered on every
 *    branch of this page so the browser suppresses the Referer header for all
 *    subresources while the token could still be in the URL or address bar.
 *  - history.replaceState is called synchronously on mount (first useEffect) to
 *    remove ?token= from the address bar before the confirm POST fires, so the raw
 *    token is not retained in browser history and does not appear in Referer if
 *    the user navigates away before success.
 *  - replace=true on success navigation removes the verify-email URL from the
 *    back-stack so clicking Back does not re-attempt the (now-consumed) token.
 *
 * Note: plain <a> links are used instead of TanStack <Link> here because this
 * is a terminal callback page; it renders in isolation during tests (no full
 * RouterProvider needed) and the links always point to stable known paths.
 */
import { createFileRoute, useSearch } from '@tanstack/react-router';
import { useEffect, useState } from 'react';
import { useNavigate } from '@tanstack/react-router';
import { useConfirmEmailChange } from '@/account/hooks';
import { ApiError } from '@/lib/api';

// ---------------------------------------------------------------------------
// Route definition
// ---------------------------------------------------------------------------

export const Route = createFileRoute('/account/verify-email')({
  validateSearch: (search: Record<string, unknown>): { token?: string } => {
    return typeof search.token === 'string' ? { token: search.token } : {};
  },
  component: VerifyEmailPage,
});

// ---------------------------------------------------------------------------
// Page wrapper (reads search param from router context)
// ---------------------------------------------------------------------------

function VerifyEmailPage() {
  const { token } = useSearch({ from: '/account/verify-email' });
  return <VerifyEmailRoute token={token} />;
}

// ---------------------------------------------------------------------------
// VerifyEmailRoute — exported for tests (accepts token as prop)
// ---------------------------------------------------------------------------

/**
 * Core verification UI. Accepts `token` as a prop so tests can mount it
 * without a full router context (the search param variant is handled by the
 * `VerifyEmailPage` wrapper above).
 */
export function VerifyEmailRoute({ token }: { token?: string }) {
  const navigate = useNavigate();
  const confirm = useConfirmEmailChange();
  // Capture the initial token value with useState so that history.replaceState
  // clearing the URL (and any consequent re-renders) does not change which
  // token we POST. useState's lazy initializer only runs on the first render,
  // giving us a stable value for both render branching and the confirm effect.
  const [initialToken] = useState(token);

  // Strip the token from the URL immediately — before any subresource fires —
  // so the raw secret is never sent in a Referer header to analytics / fonts /
  // error reporters. This runs on the first effect cycle, before the confirm POST.
  useEffect(() => {
    if (typeof window !== 'undefined' && window.location.search.includes('token=')) {
      const cleanUrl = window.location.pathname + (window.location.hash ?? '');
      window.history.replaceState(null, '', cleanUrl);
    }
  }, []);

  // Fire the confirmation request once on mount (idempotent — token is
  // single-use on the server, so a double-submit returns 410).
  useEffect(() => {
    if (!initialToken) return;
    confirm.mutate({ token: initialToken });
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  // Navigate away (replace history) after a successful confirm so the raw
  // ?token= is not retained in the browser history / Referer. replace=true
  // removes the verify-email URL from the back-stack so clicking Back
  // doesn't attempt to replay the token.
  useEffect(() => {
    if (confirm.isSuccess) {
      navigate({ to: '/account/profile', replace: true });
    }
  }, [confirm.isSuccess, navigate]);

  // ── Referrer-Policy guard injected on every render branch ─────────────────
  // Rendered inline via <meta> so the policy applies regardless of which branch
  // is shown, including the brief window before replaceState strips the token.
  const referrerMeta = <meta name="referrer" content="no-referrer" />;

  // ── Missing token ──────────────────────────────────────────────────────────
  if (!initialToken) {
    return (
      <>
        {referrerMeta}
        <div className="max-w-md mx-auto py-12 text-center space-y-4">
          <h2 className="text-xl font-semibold">Invalid verification link</h2>
          <p className="text-muted-foreground text-sm">
            This link is missing the verification token. Please click the link
            directly from your email.
          </p>
          <a
            href="/app/account/security"
            className="text-sm text-primary underline underline-offset-2"
          >
            Request a new email change
          </a>
        </div>
      </>
    );
  }

  // ── Pending ────────────────────────────────────────────────────────────────
  if (confirm.isPending || confirm.isIdle) {
    return (
      <>
        {referrerMeta}
        <div className="max-w-md mx-auto py-12 text-center space-y-4">
          <p className="text-muted-foreground text-sm">Verifying your email…</p>
        </div>
      </>
    );
  }

  // ── Success ────────────────────────────────────────────────────────────────
  if (confirm.isSuccess) {
    const newEmail = confirm.data?.primaryEmail;
    return (
      <>
        {referrerMeta}
        <div className="max-w-md mx-auto py-12 text-center space-y-4">
          <div className="text-4xl" aria-hidden="true">✓</div>
          <h2 className="text-xl font-semibold">Email verified and updated</h2>
          <p className="text-muted-foreground text-sm">
            Your primary email has been successfully updated
            {newEmail ? ` to ${newEmail}` : ''}.
          </p>
          <a
            href="/app/account/profile"
            className="text-sm text-primary underline underline-offset-2"
          >
            Go back to your account
          </a>
        </div>
      </>
    );
  }

  // ── Error ──────────────────────────────────────────────────────────────────
  const isExpiredOrUsed =
    confirm.error instanceof ApiError &&
    (confirm.error.status === 410 || confirm.error.status === 400);

  return (
    <>
      {referrerMeta}
      <div className="max-w-md mx-auto py-12 text-center space-y-4">
        <div className="text-4xl" aria-hidden="true">✗</div>
        <h2 className="text-xl font-semibold">
          {isExpiredOrUsed ? 'Link expired or already used' : 'Verification failed'}
        </h2>
        <p className="text-muted-foreground text-sm">
          {isExpiredOrUsed
            ? 'This link has expired or has already been used. Please request a new email change.'
            : 'Something went wrong. Please try again or request a new email change.'}
        </p>
        <a
          href="/app/account/security"
          className="text-sm text-primary underline underline-offset-2"
        >
          Request a new email change
        </a>
      </div>
    </>
  );
}
