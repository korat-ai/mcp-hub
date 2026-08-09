import { useEffect, useState } from 'react';

/**
 * Returns a `nowMs` value that refreshes on the given interval.
 * Shared across components in the same render tree — but since it returns
 * a plain number and React batches state updates, multiple consumers simply
 * re-render at the same time rather than spawning per-row timers.
 *
 * Used by the nodes view to tick presence badges live without server polling.
 */
export function useNow(intervalMs = 8_000): number {
  const [nowMs, setNowMs] = useState(() => Date.now());

  useEffect(() => {
    const id = setInterval(() => setNowMs(Date.now()), intervalMs);
    return () => clearInterval(id);
  }, [intervalMs]);

  return nowMs;
}
