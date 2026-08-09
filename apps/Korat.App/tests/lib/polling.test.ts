import { describe, expect, it } from 'vitest';
import {
  POLL_INTERVAL_MS,
  POLL_STALE_TIME_MS,
  POLL_FRESH_THRESHOLD_MS,
} from '@/lib/polling';

describe('polling constants invariants', () => {
  it('staleTime is less than interval (so refetch fires)', () => {
    expect(POLL_STALE_TIME_MS).toBeLessThan(POLL_INTERVAL_MS);
  });

  it('fresh threshold leaves render slack above interval', () => {
    expect(POLL_FRESH_THRESHOLD_MS).toBeGreaterThan(POLL_INTERVAL_MS);
  });
});
