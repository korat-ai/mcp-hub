import type {
  AccessRequestDto,
  ApproveAccessRequestResponseDto,
  CliTokenDto,
  GrantDto,
  IdValue,
  MeDto,
  OAuthConsentDto,
  PatchNodeRequest,
  PatchedNodeDto,
  SessionDto,
  SpaceDto,
  VersionDto,
} from '@/types/api';
import type {
  CreateHttpMcpServerRequest,
  PatchHttpMcpServerRequest,
  CreatedHttpMcpServerDto,
  PatchedHttpMcpServerDto,
  ConnectActionDto,
  DetectAuthModeResponse,
} from '@/types/mcpServers';
import { attachCsrfHeader } from './csrf';

export type { IdValue } from '@/types/api';

const BASE = '';

export class ApiError extends Error {
  constructor(public status: number, public body: string) {
    super(`HTTP ${status}${body ? `: ${body}` : ''}`);
    this.name = 'ApiError';
  }
}

/**
 * Unwrap a strongly-typed Id struct serialized by ASP.NET as { value: string }.
 * Mutation endpoints accept a plain string in the URL; callers should pass
 * getIdValue(dto.id) rather than dto.id directly.
 */
export function getIdValue(id: IdValue | string): string {
  return typeof id === 'string' ? id : id.value;
}

async function request<T>(
  path: string,
  init?: RequestInit & { json?: unknown },
): Promise<T> {
  const { json, headers: rawHeaders, body, method = 'GET', ...rest } = init ?? {};
  const headers = new Headers(rawHeaders as HeadersInit | undefined);
  if (json !== undefined) headers.set('Content-Type', 'application/json');
  attachCsrfHeader(headers, method.toUpperCase());
  const res = await fetch(`${BASE}${path}`, {
    ...rest,
    method,
    credentials: 'same-origin',
    headers,
    body: json !== undefined ? JSON.stringify(json) : body,
  });
  if (!res.ok) {
    const errBody = await res.text().catch(() => '');
    throw new ApiError(res.status, errBody);
  }
  if (res.status === 204) return undefined as T;
  const ct = res.headers.get('content-type') ?? '';
  if (!ct.includes('application/json')) return undefined as T;
  return (await res.json()) as T;
}

export const api = {
  meta: {
    version: () => request<VersionDto>('/api/version'),
  },
  space: {
    get: () => request<SpaceDto>('/api/space'),
  },
  nodes: {
    // node-visibility-doctor (2026-07-02): the only mutable field is the owner note (null
    // clears it). There is no dedicated GET /api/nodes/{id} — node data is read from
    // /api/space, mirroring how inference points have no GET-by-id (see api.inference.update).
    update: (id: IdValue | string, body: PatchNodeRequest) =>
      request<PatchedNodeDto>(`/api/nodes/${encodeURIComponent(getIdValue(id))}`, {
        method: 'PATCH',
        json: body,
      }),
  },
  grants: {
    list: () => request<GrantDto[]>('/api/grants'),
    revoke: (id: IdValue | string) =>
      request<void>(`/api/grants/${encodeURIComponent(getIdValue(id))}/revoke`, { method: 'POST' }),
  },
  oauthConsents: {
    // Space-MCP inc-2a, Task 8: owner console — OAuth consents (client × Space).
    list: () => request<OAuthConsentDto[]>('/api/oauth/consents'),
    revoke: (id: string) =>
      request<void>(`/api/oauth/consents/${encodeURIComponent(id)}/revoke`, { method: 'POST' }),
  },
  accessRequests: {
    get: (id: IdValue | string) =>
      request<AccessRequestDto>(`/api/access-requests/${encodeURIComponent(getIdValue(id))}`),
    approve: (id: IdValue | string) =>
      request<ApproveAccessRequestResponseDto>(
        `/api/access-requests/${encodeURIComponent(getIdValue(id))}/approve`,
        { method: 'POST' },
      ),
    deny: (id: IdValue | string) =>
      request<void>(`/api/access-requests/${encodeURIComponent(getIdValue(id))}/deny`, { method: 'POST' }),
  },
  mcpServers: {
    // Increment 1 (HTTP MCP direct-to-Space, Task 3/Task 7): owner registers a cloud-hosted
    // HTTP MCP server. `body` serializes 1:1 to CreateHttpMcpServerRequest — no field remapping.
    // Static auth only — authMode 'oauth' is Increment 2 and is not offered by this type.
    create: (body: CreateHttpMcpServerRequest) =>
      request<CreatedHttpMcpServerDto>('/api/mcp-servers', { method: 'POST', json: body }),
    // Partial update — see PatchHttpMcpServerRequest's partial-update contract (omitted secret
    // = keep, "" = clear). Not yet driven by any console UI this increment (no edit form) —
    // added alongside `create` for API-surface parity with inference points; ready for a future
    // edit form to call.
    patch: (id: IdValue | string, body: PatchHttpMcpServerRequest) =>
      request<PatchedHttpMcpServerDto>(`/api/mcp-servers/${encodeURIComponent(getIdValue(id))}`, {
        method: 'PATCH',
        json: body,
      }),
    disable: (id: IdValue | string) =>
      request<void>(`/api/mcp-servers/${encodeURIComponent(getIdValue(id))}/disable`, { method: 'POST' }),
    enable: (id: IdValue | string) =>
      request<void>(`/api/mcp-servers/${encodeURIComponent(getIdValue(id))}/enable`, { method: 'POST' }),
    delete: (id: IdValue | string) =>
      request<void>(`/api/mcp-servers/${encodeURIComponent(getIdValue(id))}`, { method: 'DELETE' }),
    // Increment 2 (HTTP MCP OAuth, Task 6): produces a fresh connect action for a server already
    // in NeedsReauth (initial consent never finished, or a refresh failure).
    reconnect: (id: IdValue | string) =>
      request<ConnectActionDto>(`/api/mcp-servers/${encodeURIComponent(getIdValue(id))}/reconnect`, { method: 'POST' }),
    // "Auto-detect auth mode" feature: probed on the Add-HTTP-MCP-server form's Remote URL field
    // onBlur — classifies the endpoint's auth challenge without running full OAuth discovery/DCR.
    // Best-effort: the server never 500s here, always 200 with authMode 'none'|'oauth'|'unknown'.
    detectAuth: (remoteUrl: string) =>
      request<DetectAuthModeResponse>('/api/mcp-servers/detect-auth', { method: 'POST', json: { remoteUrl } }),
  },
  sessions: {
    list: () => request<SessionDto[]>('/api/sessions'),
  },
  auth: {
    me: () => request<MeDto>('/api/auth/me'),
    updateMe: (body: { displayName: string }) => request<MeDto>('/api/auth/me', { method: 'PUT', json: body }),
    sessions: {
      list: () => request<Array<{ id: string; userAgent: string | null; createdFromIp: string | null; createdAt: string; lastUsedAt: string; expiresAt: string; current: boolean }>>('/api/auth/sessions'),
      revoke: (id: string) => request<void>(`/api/auth/sessions/${id}/revoke`, { method: 'POST' }),
      revokeOthers: () => request<void>('/api/auth/sessions/revoke-others', { method: 'POST' }),
    },
    email: {
      change: (body: { newEmail: string }) =>
        request<void>('/api/auth/email/change', { method: 'POST', json: body }),
      confirm: (body: { token: string }) =>
        request<{ primaryEmail: string }>('/api/auth/email/change/confirm', { method: 'POST', json: body }),
    },
    signout: () => request<void>('/api/auth/signout', { method: 'POST' }),
    requestMagicLink: (email: string) =>
      request<void>('/signin/magic-link', { method: 'POST', json: { email } }),
    pendingLink: {
      read: () => request<{ provider: string; email: string; displayName: string | null }>('/api/auth/pending-link'),
      confirm: () => request<void>('/api/auth/pending-link/confirm', { method: 'POST' }),
      cancel: () => request<void>('/api/auth/pending-link/cancel', { method: 'POST' }),
    },
  },
  cli: {
    approve: (userCode: string) =>
      request<void>('/api/auth/cli/approve', { method: 'POST', json: { user_code: userCode } }),
    deny: (userCode: string) =>
      request<void>('/api/auth/cli/deny', { method: 'POST', json: { user_code: userCode } }),
    tokens: {
      list: () => request<CliTokenDto[]>('/api/cli/tokens'),
      revoke: (id: string) => request<void>(`/api/cli/tokens/${id}/revoke`, { method: 'POST' }),
    },
  },
};
