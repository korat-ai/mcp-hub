/**
 * Form for /servers/new — registers a cloud-hosted HTTP MCP server via POST /api/mcp-servers
 * (Increment 1, HTTP MCP direct-to-Space). Mirrors InferenceCreateForms.tsx's ByoEndpointTab
 * structure (closest shape: url + optional auth header name + optional secret) — controlled
 * inputs, inline error mapping via <SecretInput/>, submit → useCreateHttpMcpServer.
 *
 * SECRET DISCIPLINE: `secret` is write-only. It lives only in this component's local state, is
 * rendered through <SecretInput/> (type=password + reveal toggle, autoComplete=new-password),
 * is never included in any URL/query/toast/log, and is cleared from state immediately on a
 * successful submit (component also unmounts on navigate, dropping the state entirely).
 *
 * Increment 2 (HTTP MCP OAuth, Task 6): `authMode` also offers 'oauth' — no static header-name
 * or secret field for it (there is no static secret; the owner authorizes via redirect instead).
 * On a create success carrying `connect.authorizeUrl`, this component redirects the browser to
 * it immediately (starting the consent round trip) instead of calling `onCreated`. On
 * `connect.error` (no authorizeUrl — e.g. the AS doesn't support dynamic client registration),
 * the row still exists (in NeedsReauth) so `onCreated` fires as usual; the owner reconnects from
 * the server detail page (see ServerActions's NeedsReauth branch).
 *
 * "Auto-detect auth mode" feature: on the Remote URL field's onBlur, probes POST
 * /api/mcp-servers/detect-auth and — on a conclusive `none`/`oauth` result — auto-selects the Auth
 * dropdown. Never blocks submit, never re-probes the same URL twice, and never clobbers a mode the
 * owner picked manually while a probe was still in flight (see authModeEditGenRef below).
 */
import { useRef, useState, type ReactNode } from 'react';
import { Globe, Loader2Icon } from 'lucide-react';
import { Card } from '@/components/ui/card';
import { Button } from '@/components/ui/button';
import { SecretInput } from '@/components/domain/SecretInput';
import { useCreateHttpMcpServer } from '@/hooks/useCreateHttpMcpServer';
import { api, ApiError } from '@/lib/api';
import type { CreateHttpMcpServerRequest, McpServerAuthMode } from '@/types/mcpServers';

const INPUT_CLASS =
  'w-full rounded-md border border-input bg-background px-3 py-2 text-sm focus:outline-none focus:ring-2 focus:ring-ring disabled:opacity-50';

function Field({
  label,
  htmlFor,
  hint,
  children,
}: {
  label: string;
  htmlFor: string;
  hint?: string;
  children: ReactNode;
}) {
  return (
    <div className="flex flex-col gap-1.5">
      <label htmlFor={htmlFor} className="text-sm font-medium">
        {label}
      </label>
      {children}
      {hint && <p className="text-xs text-muted-foreground">{hint}</p>}
    </div>
  );
}

/** Maps a POST /api/mcp-servers failure to a user-facing inline message — mirrors
 * mapCreateInferencePointError's shape (lib/inferenceValidation.ts): prefer the server's
 * `{ error }` body (SSRF/header-name/authMode-oauth validation all return one), fall back to a
 * generic message keyed off the status. */
function mapCreateHttpMcpServerError(err: unknown): string | null {
  if (!(err instanceof ApiError)) return null;
  if (err.status === 409) return 'An MCP server with that name already exists.';
  try {
    const body = JSON.parse(err.body) as { error?: string };
    if (body.error) return body.error;
  } catch {
    // non-JSON body — fall through to a generic message below
  }
  return err.status === 400 ? 'Invalid server configuration.' : null;
}

interface FormState {
  displayName: string;
  remoteUrl: string;
  authMode: McpServerAuthMode;
  authHeaderName: string;
  secret: string;
}

const DEFAULT_FORM: FormState = {
  displayName: '',
  remoteUrl: '',
  authMode: 'none',
  authHeaderName: '',
  secret: '',
};

/** Cheap client-side mirror of the server's authoritative checks — same rationale as
 * lib/inferenceValidation.ts's validateByoForm (gates the submit button; the server remains
 * the source of truth and its `{ error }` is still surfaced via mapCreateHttpMcpServerError
 * on a 400 this client check didn't catch, e.g. SSRF-unsafe URLs). */
function validate(form: FormState): string | null {
  if (!form.displayName.trim()) return 'Name is required.';
  if (!form.remoteUrl.trim()) return 'Remote URL is required.';
  if (form.authMode === 'header' && !form.authHeaderName.trim()) {
    return 'Header name is required for custom-header auth.';
  }
  if ((form.authMode === 'bearer' || form.authMode === 'header') && !form.secret.trim()) {
    return 'A secret is required for this auth mode.';
  }
  return null;
}

/** "Auto-detect auth mode" feature: cheap client-side pre-filter so the probe only ever fires for
 * something that could plausibly be a real MCP endpoint — mirrors SsrfGuard.ValidateUrl's
 * https-only rule (the server rejects anything else with a 400 anyway; this just avoids firing a
 * request that would obviously come back rejected). */
function looksLikeAbsoluteHttpsUrl(value: string): boolean {
  try {
    return new URL(value).protocol === 'https:';
  } catch {
    return false;
  }
}

interface Props {
  onCreated: (serverId: string) => void;
}

export function McpServerCreateForm({ onCreated }: Props) {
  const [form, setForm] = useState<FormState>(DEFAULT_FORM);
  const [submitError, setSubmitError] = useState<string | null>(null);
  const create = useCreateHttpMcpServer();

  // "Auto-detect auth mode" feature state — kept separate from FormState since none of it is
  // submitted, it only drives the detect-on-blur UX.
  const [detecting, setDetecting] = useState(false);
  const [detectHint, setDetectHint] = useState<string | null>(null);
  const [lastDetectedUrl, setLastDetectedUrl] = useState<string | null>(null);
  // Bumped on every MANUAL Auth-dropdown change. A detect call captures this value when it
  // starts; if it has changed by the time the call resolves, the owner picked a mode themselves
  // while the probe was in flight and the (now-stale) detected result must not clobber it.
  const authModeEditGenRef = useRef(0);

  const validationError = validate(form);

  function handleAuthModeChange(next: string) {
    if (next === 'none' || next === 'bearer' || next === 'header' || next === 'oauth') {
      authModeEditGenRef.current += 1;
      setForm((f) => ({ ...f, authMode: next }));
    }
  }

  async function handleRemoteUrlBlur() {
    const url = form.remoteUrl.trim();
    if (!url || !looksLikeAbsoluteHttpsUrl(url) || url === lastDetectedUrl) return;

    setLastDetectedUrl(url);
    setDetecting(true);
    setDetectHint(null);
    const genAtRequest = authModeEditGenRef.current;
    try {
      const result = await api.mcpServers.detectAuth(url);
      // The owner manually picked a mode while this probe was in flight — leave it alone.
      if (authModeEditGenRef.current !== genAtRequest) return;
      if (result.authMode === 'none' || result.authMode === 'oauth') {
        setForm((f) => ({ ...f, authMode: result.authMode as McpServerAuthMode }));
      } else {
        setDetectHint("Couldn't detect — pick manually.");
      }
    } catch {
      if (authModeEditGenRef.current !== genAtRequest) return;
      setDetectHint("Couldn't detect — pick manually.");
    } finally {
      setDetecting(false);
    }
  }

  function handleSubmit(e: React.FormEvent) {
    e.preventDefault();
    setSubmitError(null);
    const err = validate(form);
    if (err) {
      setSubmitError(err);
      return;
    }

    // M4-style trim discipline (mirrors ByokTab/ByoEndpointTab): send trimmed values, only
    // include authHeaderName/secret when the auth mode actually needs them.
    const body: CreateHttpMcpServerRequest = {
      displayName: form.displayName.trim(),
      remoteUrl: form.remoteUrl.trim(),
      authMode: form.authMode,
      ...(form.authMode === 'header' ? { authHeaderName: form.authHeaderName.trim() } : {}),
      ...(form.authMode === 'bearer' || form.authMode === 'header' ? { secret: form.secret.trim() } : {}),
    };

    create.mutate(body, {
      onSuccess: (created) => {
        setForm(DEFAULT_FORM); // write-only secret: drop it from state immediately
        if (created.connect?.authorizeUrl) {
          window.location.href = created.connect.authorizeUrl;
          return;
        }
        onCreated(created.id);
      },
      onError: (err2) => setSubmitError(mapCreateHttpMcpServerError(err2) ?? 'Could not create the server.'),
    });
  }

  return (
    <Card className="p-5">
      <form onSubmit={handleSubmit} className="flex flex-col gap-4">
        <div className="flex items-center gap-2">
          <Globe className="size-4 text-primary" aria-hidden="true" />
          <h2 className="text-sm font-semibold">Register a cloud-hosted HTTP MCP server</h2>
        </div>
        <p className="text-xs text-muted-foreground max-w-xl">
          Korat Cloud speaks MCP Streamable-HTTP to this endpoint directly — no publisher runtime
          required, but also no end-to-end encryption (the cloud is the terminus, disclosed as
          "Cloud-terminated" once created). Auth is optional — leave it as None for an
          unauthenticated endpoint.
        </p>

        <Field label="Name" htmlFor="mcp-http-name">
          <input
            id="mcp-http-name"
            type="text"
            value={form.displayName}
            onChange={(e) => setForm((f) => ({ ...f, displayName: e.target.value }))}
            placeholder="my-remote-server"
            maxLength={64}
            autoComplete="off"
            className={INPUT_CLASS}
          />
        </Field>

        <Field label="Remote URL" htmlFor="mcp-http-url" hint="The server's Streamable-HTTP MCP endpoint.">
          <input
            id="mcp-http-url"
            type="url"
            value={form.remoteUrl}
            onChange={(e) => setForm((f) => ({ ...f, remoteUrl: e.target.value }))}
            onBlur={handleRemoteUrlBlur}
            placeholder="https://example.com/mcp"
            autoComplete="off"
            className={INPUT_CLASS}
          />
        </Field>

        <Field label="Auth" htmlFor="mcp-http-auth-mode">
          <select
            id="mcp-http-auth-mode"
            value={form.authMode}
            onChange={(e) => handleAuthModeChange(e.target.value)}
            className={INPUT_CLASS}
          >
            <option value="none">None</option>
            <option value="bearer">Bearer token</option>
            <option value="header">Custom header</option>
            <option value="oauth">OAuth 2.1</option>
          </select>
          {/* "Auto-detect auth mode" feature: non-blocking status — the select above stays fully
              editable while a probe is in flight. */}
          {detecting && (
            <p role="status" className="flex items-center gap-1.5 text-xs text-muted-foreground">
              <Loader2Icon className="size-3 animate-spin" aria-hidden="true" />
              Detecting auth…
            </p>
          )}
          {!detecting && detectHint && <p className="text-xs text-muted-foreground">{detectHint}</p>}
        </Field>

        {form.authMode === 'header' && (
          <Field label="Header name" htmlFor="mcp-http-header-name">
            <input
              id="mcp-http-header-name"
              type="text"
              value={form.authHeaderName}
              onChange={(e) => setForm((f) => ({ ...f, authHeaderName: e.target.value }))}
              placeholder="X-Api-Key"
              autoComplete="off"
              className={INPUT_CLASS}
            />
          </Field>
        )}

        {(form.authMode === 'bearer' || form.authMode === 'header') && (
          <Field label="Secret" htmlFor="mcp-http-secret">
            <SecretInput
              id="mcp-http-secret"
              value={form.secret}
              onChange={(v) => setForm((f) => ({ ...f, secret: v }))}
              placeholder={form.authMode === 'bearer' ? 'Bearer token value' : 'Header value'}
            />
          </Field>
        )}

        {submitError && (
          <p role="alert" className="text-xs text-destructive">
            {submitError}
          </p>
        )}
        {!submitError && validationError && (
          <p className="text-xs text-muted-foreground">{validationError}</p>
        )}

        <div className="flex justify-end">
          <Button type="submit" disabled={!!validationError || create.isPending}>
            {create.isPending ? 'Creating…' : 'Add HTTP MCP server'}
          </Button>
        </div>
      </form>
    </Card>
  );
}
