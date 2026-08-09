/**
 * ConnectedProviders — OAuth providers linked to the account.
 *
 * Linked providers render as read-only chips (no unlink in v1 — locked decision).
 * Supported providers that are NOT yet linked render a "Connect" button that starts
 * the connect-provider OAuth flow (spec 027): a top-level navigation to
 * /signin/{provider}?link=1, which links the proven identity to the current account.
 */
import type { ProviderLinkDto } from '@/types/api';

interface Props {
  providers?: ProviderLinkDto[];
}

/** Providers a user can connect, in display order. */
const SUPPORTED_PROVIDERS = ['github', 'google'] as const;

/** Render a human-friendly label for a provider slug (e.g. "github" → "GitHub"). */
function providerLabel(provider: string): string {
  switch (provider.toLowerCase()) {
    case 'github':
      return 'GitHub';
    case 'google':
      return 'Google';
    default:
      // Capitalize first letter for unknown providers
      return provider.charAt(0).toUpperCase() + provider.slice(1);
  }
}

/** OAuth connect URL — full-page navigation (not fetch) so the IdP redirect works. */
function connectHref(provider: string): string {
  const returnUrl = encodeURIComponent('/app/account/profile');
  return `/signin/${provider}?link=1&returnUrl=${returnUrl}`;
}

export function ConnectedProviders({ providers }: Props) {
  const list = providers ?? [];
  const linked = new Set(list.map((p) => p.provider.toLowerCase()));
  const connectable = SUPPORTED_PROVIDERS.filter((p) => !linked.has(p));

  return (
    <div className="flex flex-col gap-3">
      {list.length === 0 ? (
        <p className="text-sm text-muted-foreground">No connected providers yet.</p>
      ) : (
        <div className="flex flex-wrap gap-2">
          {list.map((p) => (
            <span
              key={`${p.provider}:${p.externalId}`}
              className="inline-flex items-center rounded-full border border-border bg-muted px-3 py-1 text-xs font-medium text-foreground"
            >
              {providerLabel(p.provider)}
            </span>
          ))}
        </div>
      )}

      {connectable.length > 0 && (
        <div className="flex flex-wrap gap-2">
          {connectable.map((p) => (
            <a
              key={p}
              href={connectHref(p)}
              className="inline-flex items-center rounded-md border border-border bg-background px-3 py-1.5 text-sm font-medium text-foreground transition-colors hover:bg-muted focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring"
            >
              Connect {providerLabel(p)}
            </a>
          ))}
        </div>
      )}
    </div>
  );
}
