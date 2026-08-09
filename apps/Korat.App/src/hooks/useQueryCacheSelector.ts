import { useEffect, useState } from 'react';
import type { QueryState } from '@tanstack/react-query';
import { useQueryClient } from '@tanstack/react-query';

/**
 * Read-only subscription to a react-query cache entry.
 *
 * Does NOT trigger fetches (no observer registered). Useful for UI that
 * derives display state from a query somebody else owns (e.g. AuthBanner
 * watching for 401 errors on /api/space without participating in the
 * polling lifecycle).
 *
 * @param rootKey  The first segment of the query key to filter on (e.g. ['space']).
 * @param select   Projection from QueryState to the value you want. MUST be
 *                 stable — wrap in useCallback at the call site or define
 *                 outside the component, otherwise the subscription will
 *                 re-attach on every render.
 */
export function useQueryCacheSelector<T>(
  rootKey: readonly unknown[],
  select: (state: QueryState | undefined) => T,
): T {
  const qc = useQueryClient();
  const [value, setValue] = useState<T>(() => select(qc.getQueryState(rootKey)));

  useEffect(() => {
    const recompute = () => setValue(select(qc.getQueryState(rootKey)));
    recompute();
    return qc.getQueryCache().subscribe((event) => {
      if (
        event.type === 'updated' &&
        Array.isArray(event.query.queryKey) &&
        event.query.queryKey[0] === rootKey[0]
      ) {
        recompute();
      }
    });
  }, [qc, rootKey, select]);

  return value;
}
