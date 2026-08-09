/**
 * Sentry / GlitchTip error-tracking initialisation.
 *
 * Design rules:
 *  1. No-op when VITE_SENTRY_DSN is unset or empty — the SDK is never imported
 *     conditionally (that would break tree-shaking), but init() is a no-op when
 *     dsn is falsy, so the bundle is the same in both paths.
 *  2. Opt-out: DNT=1 OR localStorage key "korat.telemetry" === "off" skips init.
 *     A deployment that configures telemetry must disclose this in its privacy
 *     notice.
 *  3. beforeSend scrubs: Authorization headers, bearer tokens, and sensitive
 *     query-string params (token=, code=, invite=) are dropped before transmission.
 *  4. No PII: sendDefaultPii=false; no user email/name attached.
 *  5. Errors only: tracesSampleRate=0, replaysSessionSampleRate=0,
 *     replaysOnErrorSampleRate=0.
 */
import * as Sentry from '@sentry/react';

/** Params that must be redacted from query strings. */
const SENSITIVE_PARAMS = ['token', 'code', 'invite'];

/**
 * Derives the Sentry environment from the current host. `vite build` always sets
 * MODE=production, so the host is the only runtime signal of which deploy this is.
 *  - *.korat.ai  → production
 *  - *.korat.dev → development
 *  - localhost / 127.0.0.1 → development
 *  - anything else → production (safe default; never silently mislabel real traffic)
 */
function deriveEnvironmentFromHost(): string {
  const host = window.location.hostname;
  if (host === 'localhost' || host === '127.0.0.1' || host.endsWith('.korat.dev')) {
    return 'development';
  }
  return 'production';
}

/** Returns true when the user has opted out via DNT or localStorage. */
function isOptedOut(): boolean {
  // Honour Do Not Track (browser signal, user-agent-set)
  if (navigator.doNotTrack === '1') return true;
  // Honour explicit localStorage opt-out: localStorage.setItem('korat.telemetry', 'off')
  try {
    if (localStorage.getItem('korat.telemetry') === 'off') return true;
  } catch {
    // localStorage may be blocked in sandboxed iframes — treat as opted-in
  }
  return false;
}

/**
 * Redacts secrets from free text (exception messages, log messages, breadcrumbs).
 * Mirrors the CLI/cloud scrubbers so no surface ships tokens/emails/paths. The web
 * is owner-only telemetry, but error *messages* can still interpolate an auth token
 * or email, and request-data scrubbing alone never touched them.
 */
export function scrubText(value: string | undefined): string | undefined {
  if (!value) return value;
  let v = value;
  // Bearer / token= / cli_token= style secrets (keep the label, drop the value).
  v = v.replace(/(Bearer\s+|(?:cli_)?token[=:]\s*|invite[=:]\s*|code[=:]\s*)[^\s"'&;,]+/gi,
    '$1<redacted>');
  // Bare DSN (publicKey@host/projectId).
  v = v.replace(/https?:\/\/[^@\s/]+@[^\s/]+\/\d+/gi, '<dsn-redacted>');
  // Email addresses.
  v = v.replace(/[a-zA-Z0-9._%+-]+@[a-zA-Z0-9.-]+\.[a-zA-Z]{2,}/g, '<email-redacted>');
  // Unix absolute home-ish paths (/Users/<name>/…, /home/<name>/…).
  v = v.replace(/\/(?:Users|home)\/[^/\s:]+/g, '/~');
  return v;
}

/** Scrubs an exception's message + value text in place across all values. */
function scrubException(ex: { type?: string; value?: string }): void {
  if (ex.value) ex.value = scrubText(ex.value);
}

/** Strips sensitive params from a URL string. Returns the sanitised URL. */
function scrubUrl(rawUrl: string | undefined): string | undefined {
  if (!rawUrl) return rawUrl;
  try {
    const url = new URL(rawUrl);
    for (const param of SENSITIVE_PARAMS) {
      if (url.searchParams.has(param)) url.searchParams.set(param, '[Filtered]');
    }
    return url.toString();
  } catch {
    return rawUrl;
  }
}

export function initSentry(): void {
  const dsn = import.meta.env.VITE_SENTRY_DSN;
  if (!dsn) return; // no DSN → silent no-op, app runs normally

  if (isOptedOut()) return; // user opted out → skip init

  // import.meta.env.MODE is always "production" for any `vite build`, so it can't
  // distinguish the dev deploy (my.korat.dev) from prod (my.korat.ai). Prefer an
  // explicit build-arg, else derive from the host so dev errors don't pollute the
  // production environment in GlitchTip.
  const environment =
    import.meta.env.VITE_SENTRY_ENVIRONMENT ?? deriveEnvironmentFromHost();

  Sentry.init({
    dsn,
    release: import.meta.env.VITE_COMMIT_SHA,
    environment,

    // Errors only — no performance/traces/replay
    tracesSampleRate: 0,
    replaysSessionSampleRate: 0,
    replaysOnErrorSampleRate: 0,

    // Never attach user email, IP, cookies, or other PII
    sendDefaultPii: false,

    beforeSend(event) {
      // --- Drop Authorization / bearer token headers ---
      if (event.request?.headers) {
        const headers = event.request.headers as Record<string, string>;
        delete headers['Authorization'];
        delete headers['authorization'];
      }

      // --- Scrub sensitive query-string params from the event URL ---
      if (event.request) {
        event.request.url = scrubUrl(event.request.url);
        event.request.query_string = undefined; // drop raw query string
      }

      // --- Scrub free text: message + exception values may carry tokens/emails ---
      if (event.message) event.message = scrubText(event.message);
      event.exception?.values?.forEach(scrubException);
      event.breadcrumbs?.forEach((b) => {
        if (b.message) b.message = scrubText(b.message);
      });

      return event;
    },
  });
}

// Re-export ErrorBoundary from @sentry/react for use in component tree
export { Sentry };
