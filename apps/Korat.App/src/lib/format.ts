import type { SessionEffectiveStatus } from '@/types/api';

/** Truncated ID display length for table cells. Long enough to disambiguate
 *  in practice, short enough not to overflow narrow columns. */
const SHORT_ID_LENGTH = 12;

export function shortId(id: string): string {
  return id.length <= SHORT_ID_LENGTH ? id : id.slice(0, SHORT_ID_LENGTH);
}

/** Statuses that count as "open" (occupying a connection slot). */
const OPEN_SESSION_STATUSES = ['Opening', 'Active', 'Closing'] as const;

// Accepts the derived effectiveStatus too: a 'Stale' session is NOT open (its participant is offline).
export function isOpenSession(status: SessionEffectiveStatus): boolean {
  return (OPEN_SESSION_STATUSES as readonly SessionEffectiveStatus[]).includes(status);
}

/** Unit ladder + log1024 tier pick — mirrors the prototype's fmtBytes (data.js:118-125)
 *  so multi-GB session transfers render as e.g. "2.5 GB" instead of clipping at MB. */
const BYTE_UNITS = ['B', 'kB', 'MB', 'GB'] as const;

export function formatBytes(n: number): string {
  if (n === 0) return '0 B';
  const tier = Math.min(BYTE_UNITS.length - 1, Math.floor(Math.log(n) / Math.log(1024)));
  const value = n / 1024 ** tier;
  const formatted = tier === 0 ? value : value.toFixed(value < 10 ? 1 : 0);
  return `${formatted} ${BYTE_UNITS[tier]}`;
}
