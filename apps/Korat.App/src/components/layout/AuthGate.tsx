import type { ReactNode } from 'react';
import { useEffect } from 'react';
import { useQuery } from '@tanstack/react-query';
import { useNavigate, useRouterState } from '@tanstack/react-router';
import { ApiError } from '@/lib/api';
import { spaceQueryOptions } from '@/lib/queries/space';

/**
 * Owns the canonical /api/space polling query and redirects to /signin on 401.
 * Other shell components (AuthBanner, SyncIndicator) read from this cache
 * passively.
 *
 * Note: useSpace() reads the same query key via shared spaceQueryOptions()
 * — tanstack-query de-duplicates concurrent observers.
 */
export function AuthGate({ children }: { children: ReactNode }) {
  const { error } = useQuery(spaceQueryOptions());
  const navigate = useNavigate();
  const location = useRouterState({ select: (s) => s.location });

  const is401 = error instanceof ApiError && error.status === 401;
  const atSignIn = location.pathname === '/signin';

  useEffect(() => {
    if (is401 && !atSignIn) {
      // Preserve the originating URL so e.g. /grants?filter=active round-trips
      // back intact after sign-in. TanStack Router exposes `search` as a parsed
      // object; `searchStr` is the serialized form needed for URL concatenation.
      void navigate({
        to: '/signin',
        search: { returnUrl: location.pathname + location.searchStr },
        replace: true,
      });
    }
  }, [is401, atSignIn, navigate, location.pathname, location.searchStr]);

  return <>{children}</>;
}
