import type { QueryState } from '@tanstack/react-query';
import { queryKeys } from '@/lib/queryKeys';
import { useQueryCacheSelector } from './useQueryCacheSelector';

/**
 * Tri-state sync status for the /api/space poll — mirrors the design ref's
 * SyncIndicator modes (shell.jsx:143-163: 'live' | 'syncing' | 'error').
 *
 * - 'syncing': no successful fetch has landed yet (initial load, or the first
 *   fetch is still in flight / hasn't started).
 * - 'live': at least one fetch has succeeded and the query isn't currently
 *   failing. `updatedAt` is the ms-epoch of that last successful fetch.
 * - 'error': the query has no data at all (first fetch failed), or a later
 *   background poll failed after an earlier success — react-query keeps
 *   cached `data`/`status: 'success'` in that case, so we detect it by
 *   comparing `errorUpdatedAt` against `dataUpdatedAt` rather than trusting
 *   `status` alone.
 */
export type SyncState =
  | { status: 'syncing'; updatedAt: null }
  | { status: 'live'; updatedAt: number }
  | { status: 'error'; updatedAt: number | null };

// Module-scoped select — stable identity, no useCallback needed at call sites.
function selectSyncState(state: QueryState | undefined): SyncState {
  if (!state) return { status: 'syncing', updatedAt: null };

  const dataAt =
    typeof state.dataUpdatedAt === 'number' && state.dataUpdatedAt > 0
      ? state.dataUpdatedAt
      : null;

  // A settled error more recent than the last successful fetch means the
  // most recent poll failed — even if cached data keeps `status` at 'success'.
  const erroredSinceLastData =
    state.errorUpdatedAt > 0 && state.errorUpdatedAt > state.dataUpdatedAt;

  if (state.status === 'error' || erroredSinceLastData) {
    return { status: 'error', updatedAt: dataAt };
  }

  return dataAt === null
    ? { status: 'syncing', updatedAt: null }
    : { status: 'live', updatedAt: dataAt };
}

/** Tri-state sync status of the most recent /api/space fetch. */
export function useLastSynced(): SyncState {
  return useQueryCacheSelector(queryKeys.space.all, selectSyncState);
}
