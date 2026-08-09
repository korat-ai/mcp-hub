import { describe, expect, it } from 'vitest';
import { computeSkew, deriveServerAvailability, isNodeOnline } from './presence';

// ---------------------------------------------------------------------------
// Helpers
// ---------------------------------------------------------------------------

const STALE_SECONDS = 90;
const STALE_MS = STALE_SECONDS * 1000;

/** Build an ISO timestamp that is `deltaMs` before `nowMs`. */
function msAgo(deltaMs: number, nowMs: number): string {
  return new Date(nowMs - deltaMs).toISOString();
}

// ---------------------------------------------------------------------------
// isNodeOnline
// ---------------------------------------------------------------------------

describe('isNodeOnline', () => {
  const now = Date.now();
  const skew = 0; // no skew for most tests

  it('returns false when status is Offline', () => {
    // Fresh lastSeenAt — but status is Offline (clean disconnect)
    const lastSeenAt = msAgo(1000, now); // 1s ago
    expect(isNodeOnline('Offline', lastSeenAt, STALE_SECONDS, skew, now)).toBe(false);
  });

  it('returns false when lastSeenAt is older than the stale threshold', () => {
    const lastSeenAt = msAgo(STALE_MS + 1000, now); // 1s past threshold
    expect(isNodeOnline('Online', lastSeenAt, STALE_SECONDS, skew, now)).toBe(false);
  });

  it('returns true when status is Online and lastSeenAt is within threshold', () => {
    const lastSeenAt = msAgo(STALE_MS - 5000, now); // 5s before threshold
    expect(isNodeOnline('Online', lastSeenAt, STALE_SECONDS, skew, now)).toBe(true);
  });

  it('returns false when lastSeenAt is exactly at the threshold boundary', () => {
    const lastSeenAt = msAgo(STALE_MS, now); // exactly at boundary — not less than staleMs
    expect(isNodeOnline('Online', lastSeenAt, STALE_SECONDS, skew, now)).toBe(false);
  });

  it('returns false when lastSeenAt is null', () => {
    expect(isNodeOnline('Online', null, STALE_SECONDS, skew, now)).toBe(false);
  });

  it('returns false when lastSeenAt is undefined', () => {
    expect(isNodeOnline('Online', undefined, STALE_SECONDS, skew, now)).toBe(false);
  });

  it('returns false when lastSeenAt is an invalid date string', () => {
    expect(isNodeOnline('Online', 'not-a-date', STALE_SECONDS, skew, now)).toBe(false);
  });

  it('uses default 90s threshold when presenceStaleSeconds is undefined', () => {
    // 80s ago — should be online with default 90s
    const lastSeenAt = msAgo(80_000, now);
    expect(isNodeOnline('Online', lastSeenAt, undefined, skew, now)).toBe(true);
    // 95s ago — should be offline with default 90s
    const lastSeenAtOld = msAgo(95_000, now);
    expect(isNodeOnline('Online', lastSeenAtOld, undefined, skew, now)).toBe(false);
  });

  describe('clock-skew correction', () => {
    it('remains online when client clock is ahead of server (positive skew)', () => {
      // Client is 60s ahead of server: skew = +60_000.
      // The node last heartbeated 70s ago in server time — still within 90s threshold.
      // Without skew correction, ageMs = 70_000 — online (would pass anyway).
      // Key test: without correction, if client is 60s ahead, it thinks lastSeen was 130s ago.
      const clientSkew = 60_000; // client is 60s ahead
      // Node heartbeated 70s ago relative to server time
      const serverLastSeenMs = now - clientSkew - 70_000; // from server's perspective 70s ago
      const lastSeenAt = new Date(serverLastSeenMs).toISOString();

      // With skew correction: serverNow = now - 60_000; age = serverNow - lastSeenMs = 70_000 → online
      expect(isNodeOnline('Online', lastSeenAt, STALE_SECONDS, clientSkew, now)).toBe(true);

      // Without correction (skew=0): age = now - serverLastSeenMs = 130_000 → offline
      expect(isNodeOnline('Online', lastSeenAt, STALE_SECONDS, 0, now)).toBe(false);
    });

    it('marks offline when node is genuinely stale, even with skew', () => {
      // Client is 5s ahead; node heartbeated 100s ago in server time — past 90s threshold.
      const clientSkew = 5_000;
      const serverLastSeenMs = now - clientSkew - 100_000;
      const lastSeenAt = new Date(serverLastSeenMs).toISOString();
      expect(isNodeOnline('Online', lastSeenAt, STALE_SECONDS, clientSkew, now)).toBe(false);
    });
  });
});

// ---------------------------------------------------------------------------
// computeSkew
// ---------------------------------------------------------------------------

describe('computeSkew', () => {
  it('returns 0 when serverTime is undefined', () => {
    expect(computeSkew(undefined)).toBe(0);
  });

  it('returns 0 when serverTime is an invalid date string', () => {
    expect(computeSkew('not-a-date')).toBe(0);
  });

  it('returns approximately 0 when serverTime is the current time', () => {
    const serverTime = new Date().toISOString();
    const skew = computeSkew(serverTime);
    // Allow up to 100ms for the computation
    expect(Math.abs(skew)).toBeLessThan(100);
  });

  it('returns a positive skew when client clock is ahead of server', () => {
    // Simulate server being 30s behind (server time is 30s in the past)
    const serverTime = new Date(Date.now() - 30_000).toISOString();
    const skew = computeSkew(serverTime);
    // skew = Date.now() - serverMs ≈ 30_000
    expect(skew).toBeGreaterThan(29_000);
    expect(skew).toBeLessThan(31_000);
  });
});

// ---------------------------------------------------------------------------
// deriveServerAvailability (spec 021 tri-state)
// ---------------------------------------------------------------------------

describe('deriveServerAvailability', () => {
  const now = Date.now();
  const skew = 0;
  const STALE = 90; // seconds

  /** freshLastSeen: a timestamp well within the stale window */
  const fresh = new Date(now - 30_000).toISOString();    // 30s ago
  /** staleLastSeen: a timestamp past the stale window */
  const stale = new Date(now - 100_000).toISOString();   // 100s ago

  it('returns Disabled when status is Disabled, regardless of other fields', () => {
    expect(
      deriveServerAvailability('Disabled', true, 'Online', fresh, STALE, skew, now),
    ).toBe('Disabled');
  });

  it('Disabled wins even when isAsserted=false and node is online', () => {
    expect(
      deriveServerAvailability('Disabled', false, 'Online', fresh, STALE, skew, now),
    ).toBe('Disabled');
  });

  it('Disabled wins even when owner node is offline', () => {
    expect(
      deriveServerAvailability('Disabled', true, 'Offline', fresh, STALE, skew, now),
    ).toBe('Disabled');
  });

  it('returns Available when Published + isAsserted + owner online', () => {
    expect(
      deriveServerAvailability('Published', true, 'Online', fresh, STALE, skew, now),
    ).toBe('Available');
  });

  it('returns Unavailable when not isAsserted (even if owner node is online)', () => {
    expect(
      deriveServerAvailability('Published', false, 'Online', fresh, STALE, skew, now),
    ).toBe('Unavailable');
  });

  it('returns Unavailable when owner node status is Offline', () => {
    expect(
      deriveServerAvailability('Published', true, 'Offline', fresh, STALE, skew, now),
    ).toBe('Unavailable');
  });

  it('returns Unavailable when owner node lastSeenAt is stale (past threshold)', () => {
    expect(
      deriveServerAvailability('Published', true, 'Online', stale, STALE, skew, now),
    ).toBe('Unavailable');
  });

  it('returns Unavailable when publisherNodeLastSeenAt is null', () => {
    expect(
      deriveServerAvailability('Published', true, 'Online', null, STALE, skew, now),
    ).toBe('Unavailable');
  });

  it('returns Unavailable when publisherNodeStatus is null', () => {
    expect(
      deriveServerAvailability('Published', true, null, fresh, STALE, skew, now),
    ).toBe('Unavailable');
  });

  it('flips to Unavailable exactly at the stale boundary (ageMs === staleMs)', () => {
    const exactBoundary = new Date(now - STALE * 1000).toISOString();
    expect(
      deriveServerAvailability('Published', true, 'Online', exactBoundary, STALE, skew, now),
    ).toBe('Unavailable');
  });

  it('stays Available 1ms before the stale boundary', () => {
    const justBefore = new Date(now - (STALE * 1000 - 1)).toISOString();
    expect(
      deriveServerAvailability('Published', true, 'Online', justBefore, STALE, skew, now),
    ).toBe('Available');
  });

  it('handles pre-021 responses: isAsserted defaults true → Available when owner online', () => {
    // Simulate older server that omits isAsserted (caller passes ?? true)
    expect(
      deriveServerAvailability('Published', true, 'Online', fresh, STALE, skew, now),
    ).toBe('Available');
  });
});

describe('deriveServerAvailability — http_cloud (Finding 16, M5)', () => {
  it('is Available when Published, regardless of null publisherNodeStatus', () => {
    expect(deriveServerAvailability('Published', true, null, null, 90, 0, Date.now(), 'http_cloud')).toBe('Available');
  });

  it('is Unavailable when not Published', () => {
    expect(deriveServerAvailability('Unavailable', true, null, null, 90, 0, Date.now(), 'http_cloud')).toBe('Unavailable');
  });

  it('Disabled still wins over transport', () => {
    expect(deriveServerAvailability('Disabled', true, null, null, 90, 0, Date.now(), 'http_cloud')).toBe('Disabled');
  });

  it('returns NeedsReauth for an http_cloud server whose status is NeedsReauth', () => {
    const result = deriveServerAvailability(
      'NeedsReauth', true, null, null, undefined, 0, Date.now(), 'http_cloud',
    );
    expect(result).toBe('NeedsReauth');
  });

  it('still returns Available for a Published http_cloud server (NeedsReauth branch does not shadow it)', () => {
    const result = deriveServerAvailability(
      'Published', true, null, null, undefined, 0, Date.now(), 'http_cloud',
    );
    expect(result).toBe('Available');
  });
});
