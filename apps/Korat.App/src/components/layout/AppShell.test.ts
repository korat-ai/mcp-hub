/**
 * Unit tests for resolveSpaceLabel — the sidebar space label logic.
 *
 * Covers:
 *  - Legacy "default" placeholder → friendly "{displayName}'s space"
 *  - Custom stored name → shown as-is
 *  - Fallbacks: displayName null/empty → email local-part; both null → raw name
 *  - Case-insensitivity of the "default" sentinel
 *  - Null-safety throughout
 */
import { describe, expect, it } from 'vitest';
import { resolveSpaceLabel } from './AppShell';

describe('resolveSpaceLabel', () => {
  // ── Legacy placeholder ─────────────────────────────────────────────────────

  it('stored "default" + displayName → "{displayName}\'s space"', () => {
    expect(resolveSpaceLabel('default', 'Alice', 'alice@x.io'))
      .toBe("Alice's space");
  });

  it('stored "Default" (capital D) is still treated as placeholder', () => {
    expect(resolveSpaceLabel('Default', 'Alice', 'alice@x.io'))
      .toBe("Alice's space");
  });

  it('stored "DEFAULT" (all caps) is still treated as placeholder', () => {
    expect(resolveSpaceLabel('DEFAULT', 'Alice', 'alice@x.io'))
      .toBe("Alice's space");
  });

  it('stored "default" + null displayName → email local-part fallback', () => {
    expect(resolveSpaceLabel('default', null, 'alice@x.io'))
      .toBe("alice's space");
  });

  it('stored "default" + empty displayName → email local-part fallback', () => {
    expect(resolveSpaceLabel('default', '', 'alice@x.io'))
      .toBe("alice's space");
  });

  it('stored "default" + whitespace-only displayName → email local-part fallback', () => {
    expect(resolveSpaceLabel('default', '   ', 'alice@x.io'))
      .toBe("alice's space");
  });

  it('stored "default" + no displayName + no email → returns raw "default"', () => {
    expect(resolveSpaceLabel('default', null, null))
      .toBe('default');
  });

  it('stored "default" + no displayName + no email (undefined) → returns raw "default"', () => {
    expect(resolveSpaceLabel('default', undefined, undefined))
      .toBe('default');
  });

  // ── Custom stored name ─────────────────────────────────────────────────────

  it('custom stored name is shown as-is, ignoring displayName', () => {
    expect(resolveSpaceLabel("Alice's space", 'Alice', 'a@x.io'))
      .toBe("Alice's space");
  });

  it('custom stored name "My Team" is shown as-is', () => {
    expect(resolveSpaceLabel('My Team', null, null))
      .toBe('My Team');
  });

  // ── Edge cases ──────────────────────────────────────────────────────────────

  it('null stored name falls through as empty → not "default" → empty string → returns "default" sentinel', () => {
    // storedName=null → name='', name.toLowerCase() is '' not 'default' →
    // custom path → name || 'default' → 'default'
    expect(resolveSpaceLabel(null, 'Alice', 'alice@x.io'))
      .toBe('default');
  });

  it('undefined stored name behaves like null', () => {
    expect(resolveSpaceLabel(undefined, 'Alice', 'alice@x.io'))
      .toBe('default');
  });
});
