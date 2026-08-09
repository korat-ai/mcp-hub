// HTTP MCP (Increment 1, direct-to-Space) — request/response DTOs for the owner
// POST/PATCH /api/mcp-servers endpoints (McpServerEndpoints.cs, Task 3). Distinct from
// McpServerDto (types/api.ts), which is the GET /api/space catalog projection shaped around a
// publisher node (publisherNodeId/-Name/-Status/-LastSeenAt) — the create/patch response here
// never carries those fields at all (an http_cloud server has no publisher node), but does carry
// remoteUrl/authMode/authHeaderName/hasSecret/secretHint, which the catalog projection doesn't.
// Naming mirrors types/inference.ts's Create.../Created.../Patch.../Patched... split.

export type McpServerAuthMode = 'none' | 'bearer' | 'header' | 'oauth';

export interface CreateHttpMcpServerRequest {
  displayName: string;
  remoteUrl: string;
  authMode: McpServerAuthMode;
  authHeaderName?: string;
  secret?: string;
  // Increment 2 (oauth manual-client-credentials fallback): only sent when authMode === 'oauth'
  // and the owner is manually supplying credentials instead of DCR.
  clientId?: string;
  clientSecret?: string;
}

export interface PatchHttpMcpServerRequest {
  remoteUrl?: string;
  authMode?: McpServerAuthMode;
  authHeaderName?: string;
  clearAuthHeaderName?: boolean;
  secret?: string; // omit = keep, "" = clear
  clientId?: string;
  clientSecret?: string;
}

/** Increment 2: the shape POST/PATCH/reconnect all return for an oauth server's connect action —
 * never token material, only a URL to redirect the owner's browser to, or a safe error string. */
export interface ConnectActionDto {
  authorizeUrl: string | null;
  error: string | null;
}

/** Response body from POST /api/mcp-servers (create) and PATCH /api/mcp-servers/{id} (patch) —
 * both endpoints return the same shape. Never includes the plaintext secret, only a masked
 * secretHint (e.g. "…ab12") and a hasSecret flag. */
export interface CreatedHttpMcpServerDto {
  id: string;
  displayName: string;
  transport: string;
  remoteUrl: string | null;
  authMode: string | null;
  authHeaderName: string | null;
  hasSecret: boolean;
  secretHint: string | null;
  status: string;
  createdAt?: string;
  connect?: ConnectActionDto | null;
}

export type PatchedHttpMcpServerDto = CreatedHttpMcpServerDto;

/**
 * "Auto-detect auth mode" feature: response from POST /api/mcp-servers/detect-auth, probed on the
 * Add-HTTP-MCP-server form's Remote URL field onBlur. A best-effort classification — 'unknown'
 * covers every non-conclusive signal (network error, timeout, a 401 with no RFC 9728 challenge,
 * 5xx, ...), never a thrown error; the console leaves the Auth dropdown on manual pick in that
 * case rather than guessing.
 */
export interface DetectAuthModeResponse {
  authMode: 'none' | 'oauth' | 'unknown';
}
