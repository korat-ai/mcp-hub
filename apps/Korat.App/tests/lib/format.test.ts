import { describe, expect, it } from 'vitest';
import { shortId, isOpenSession, formatBytes } from '@/lib/format';

describe('shortId', () => {
  it('returns the id unchanged when <= 12 chars', () => {
    expect(shortId('abc')).toBe('abc');
    expect(shortId('123456789012')).toBe('123456789012');
  });

  it('truncates to 12 chars when longer', () => {
    expect(shortId('a'.repeat(20))).toBe('a'.repeat(12));
  });
});

describe('isOpenSession', () => {
  it('returns true for Opening, Active, Closing', () => {
    expect(isOpenSession('Opening')).toBe(true);
    expect(isOpenSession('Active')).toBe(true);
    expect(isOpenSession('Closing')).toBe(true);
  });

  it('returns false for Closed, Failed, Denied', () => {
    expect(isOpenSession('Closed')).toBe(false);
    expect(isOpenSession('Failed')).toBe(false);
    expect(isOpenSession('Denied')).toBe(false);
  });
});

describe('formatBytes', () => {
  it('uses B for n < 1024', () => {
    expect(formatBytes(0)).toBe('0 B');
    expect(formatBytes(1023)).toBe('1023 B');
  });

  it('uses kB for n < 1MB', () => {
    expect(formatBytes(1024)).toBe('1.0 kB');
    expect(formatBytes(1024 * 1024 - 1)).toMatch(/kB$/);
  });

  it('uses MB for n >= 1MB', () => {
    expect(formatBytes(1024 * 1024)).toBe('1.0 MB');
    expect(formatBytes(5 * 1024 * 1024)).toBe('5.0 MB');
  });

  it('uses GB for n >= 1GB (prototype parity, data.js:118-125)', () => {
    expect(formatBytes(2.5 * 1024 * 1024 * 1024)).toBe('2.5 GB');
  });
});
