import { describe, expect, it } from 'vitest';
import { formatTimestamp, relativeFromNow } from '@/lib/time';

describe('formatTimestamp', () => {
  it('returns dash for null/undefined', () => {
    expect(formatTimestamp(null)).toBe('—');
    expect(formatTimestamp(undefined)).toBe('—');
  });

  it('returns input string when not parseable', () => {
    expect(formatTimestamp('not-a-date')).toBe('not-a-date');
  });

  it('formats an ISO string via toLocaleString', () => {
    const input = '2026-05-28T12:00:00Z';
    const out = formatTimestamp(input);
    expect(out).not.toBe('—');
    expect(out).not.toBe(input); // proves it was actually formatted, not passed through
  });
});

describe('relativeFromNow', () => {
  const now = new Date('2026-05-28T12:00:00Z').getTime();

  it('returns just now for < 5s deltas', () => {
    expect(relativeFromNow('2026-05-28T11:59:58Z', now)).toBe('just now');
  });

  it('returns seconds for < 60s deltas', () => {
    expect(relativeFromNow('2026-05-28T11:59:30Z', now)).toBe('30s ago');
  });

  it('returns minutes for < 1h deltas', () => {
    expect(relativeFromNow('2026-05-28T11:30:00Z', now)).toBe('30m ago');
  });

  it('returns hours for < 1d deltas', () => {
    expect(relativeFromNow('2026-05-28T08:00:00Z', now)).toBe('4h ago');
  });

  it('returns days for >= 1d deltas', () => {
    expect(relativeFromNow('2026-05-26T12:00:00Z', now)).toBe('2d ago');
  });

  it('returns seconds at exactly 5s (boundary)', () => {
    expect(relativeFromNow('2026-05-28T11:59:55Z', now)).toBe('5s ago');
  });

  it('returns minutes at exactly 60s (boundary)', () => {
    expect(relativeFromNow('2026-05-28T11:59:00Z', now)).toBe('1m ago');
  });

  it('returns hours at exactly 3600s (boundary)', () => {
    expect(relativeFromNow('2026-05-28T11:00:00Z', now)).toBe('1h ago');
  });

  it('returns days at exactly 86400s (boundary)', () => {
    expect(relativeFromNow('2026-05-27T12:00:00Z', now)).toBe('1d ago');
  });

  it('returns dash for null', () => {
    expect(relativeFromNow(null, now)).toBe('—');
  });
});
