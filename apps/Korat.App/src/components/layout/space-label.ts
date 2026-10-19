/**
 * Derives the sidebar space label from stored space name and user display name.
 *
 * - When the stored name is the legacy placeholder "default" (case-insensitive),
 *   fall back to "{displayName}'s space".
 * - displayName fallback: email local-part (before @), else the raw stored name
 *   (never crash on missing data).
 * - Custom stored names are shown as-is.
 */
export function resolveSpaceLabel(
  storedName: string | null | undefined,
  displayName: string | null | undefined,
  primaryEmail?: string | null | undefined,
): string {
  const name = storedName ?? '';
  if (name.toLowerCase() !== 'default') {
    // Custom name — show as-is (handles empty too, though that shouldn't occur).
    return name || 'default';
  }
  // Legacy placeholder — derive a friendly label.
  const friendly = displayName?.trim()
    || primaryEmail?.split('@')[0]?.trim()
    || null;
  return friendly ? `${friendly}'s space` : name;
}
