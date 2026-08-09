/**
 * /account — AccountLayout (tab nav + Outlet) and the three child section routes.
 *
 * File-based TanStack Router layout route. Child routes (account.profile.tsx,
 * account.security.tsx, account.cli.tsx) nest inside this layout via the
 * TanStack Router parent/child mechanism (dot-notation filenames).
 *
 * AuthGate is already mounted in __root.tsx; the /account/* subtree is
 * automatically protected because AuthGate wraps the entire Outlet.
 */
import { useEffect } from 'react';
import { createFileRoute, Outlet, Link, useRouterState } from '@tanstack/react-router';
import { useMe } from '@/account/hooks';
import { ProfileSection } from '@/account/ProfileSection';
import { SecuritySection } from '@/account/SecuritySection';
import { CliTokensSection } from '@/account/CliTokensSection';
import { toastReceipt } from '@/lib/toast';

/** Human messages for the connect-provider (027) redirect error codes. */
const LINK_ERRORS: Record<string, string> = {
  provider_in_use: 'that account is already linked elsewhere',
  link_session: 'your session expired — sign in and try again',
  link_unverified: "the provider's email isn't verified",
};

/**
 * Surfaces the `?error=link_*` / `provider_in_use` codes the connect-provider flow
 * redirects back with, then strips the param so a refresh doesn't re-fire the toast.
 */
function useLinkErrorToast(): void {
  useEffect(() => {
    const params = new URLSearchParams(window.location.search);
    const code = params.get('error');
    if (code && code in LINK_ERRORS) {
      toastReceipt('bad', 'Connect provider', LINK_ERRORS[code]);
      params.delete('error');
      const qs = params.toString();
      window.history.replaceState({}, '', window.location.pathname + (qs ? `?${qs}` : ''));
    }
  }, []);
}

// ---------------------------------------------------------------------------
// Route definition
// ---------------------------------------------------------------------------

export const Route = createFileRoute('/account')({
  component: AccountLayout,
});

// ---------------------------------------------------------------------------
// Tab navigation items
// ---------------------------------------------------------------------------

const TAB_ITEMS = [
  { to: '/account/profile', label: 'Profile' },
  { to: '/account/security', label: 'Security' },
  { to: '/account/cli', label: 'CLI Tokens' },
] as const;

// ---------------------------------------------------------------------------
// AccountLayout component — exported for tests
// ---------------------------------------------------------------------------

export function AccountLayout() {
  const me = useMe();
  const pathname = useRouterState({ select: (s) => s.location.pathname });

  return (
    <div className="space-y-6">
      {/* ── Header ────────────────────────────────────────────────── */}
      <div className="space-y-1">
        <h2 className="text-2xl font-bold tracking-tight">Account</h2>
        {me.isPending && (
          <div className="h-4 w-40 rounded bg-muted animate-pulse" aria-hidden="true" />
        )}
        {me.isError && (
          <p className="text-xs text-destructive" role="alert">
            Could not load account details — please refresh.
          </p>
        )}
        {me.data && (
          <p className="text-muted-foreground text-sm">
            {me.data.displayName ?? me.data.primaryEmail}
          </p>
        )}
      </div>

      {/* ── Section tab navigation ────────────────────────────────── */}
      <nav
        aria-label="Account sections"
        className="flex gap-1 border-b border-border/40"
      >
        {TAB_ITEMS.map(({ to, label }) => {
          const isActive = pathname === to || pathname.startsWith(to + '/');
          return (
            <Link
              key={to}
              to={to}
              className={[
                'px-4 py-2 text-sm font-medium rounded-t transition-colors',
                isActive
                  ? 'border-b-2 border-primary text-foreground'
                  : 'text-muted-foreground hover:text-foreground',
              ].join(' ')}
              aria-current={isActive ? 'page' : undefined}
            >
              {label}
            </Link>
          );
        })}
      </nav>

      {/* ── Section content ───────────────────────────────────────── */}
      <Outlet />
    </div>
  );
}

// ---------------------------------------------------------------------------
// Exported section component shims — used by child routes AND by tests that
// want to mount a section in isolation with pre-seeded useMe data.
// ---------------------------------------------------------------------------

/** Renders ProfileSection; fetches me via useMe. */
export function AccountProfileRoute() {
  const me = useMe();
  useLinkErrorToast();

  if (me.isPending) {
    return (
      <div className="text-sm text-muted-foreground py-4">Loading…</div>
    );
  }

  if (me.isError || !me.data) {
    return (
      <div className="text-sm text-destructive py-4" role="alert">
        Could not load your account details. Please refresh.
      </div>
    );
  }

  return <ProfileSection me={me.data} />;
}

/** Renders SecuritySection; fetches me via useMe. */
export function AccountSecurityRoute() {
  const me = useMe();

  if (me.isPending) {
    return (
      <div className="text-sm text-muted-foreground py-4">Loading…</div>
    );
  }

  if (me.isError || !me.data) {
    return (
      <div className="text-sm text-destructive py-4" role="alert">
        Could not load your account details. Please refresh.
      </div>
    );
  }

  return <SecuritySection me={me.data} />;
}

/** Renders CliTokensSection; no props needed. */
export function AccountCliRoute() {
  return <CliTokensSection />;
}

