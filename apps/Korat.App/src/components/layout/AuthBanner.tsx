import type { QueryState } from '@tanstack/react-query';
import { Link } from '@tanstack/react-router';
import { queryKeys } from '@/lib/queryKeys';
import { useQueryCacheSelector } from '@/hooks/useQueryCacheSelector';
import { ApiError } from '@/lib/api';

// Module-scoped select — stable identity, no useCallback needed at call sites.
function selectUnauth(state: QueryState | undefined): boolean {
  return state?.error instanceof ApiError && state.error.status === 401;
}

/**
 * Renders a 401 banner if /api/space's cached error is ApiError(401).
 *
 * Coupling note: this component does NOT fetch /api/space itself — it reads
 * cached state populated by AuthGate (Task 7) and useSpace (Task 9). If
 * neither is mounted, the banner stays silent. Test coverage in Task 16
 * (`app-shell.test.tsx`) verifies the gate-banner pairing.
 */
export function AuthBanner() {
  const unauth = useQueryCacheSelector(queryKeys.space.all, selectUnauth);
  if (!unauth) return null;
  return (
    <div
      role="alert"
      className="border-b border-destructive/30 bg-destructive/10 text-destructive px-6 py-2 text-sm flex items-center gap-2"
    >
      Not authenticated.{' '}
      <Link to="/signin" className="underline underline-offset-2 hover:opacity-80 font-medium">
        Sign in
      </Link>
    </div>
  );
}
