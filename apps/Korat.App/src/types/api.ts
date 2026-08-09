// Mirror the JSON shape produced by apps/Korat.Cloud/Web/Endpoints.cs.
// If a field is renamed server-side, the build here breaks immediately — that
// is the design.
//
// RECONCILIATION NOTES (vs. plan template):
//
// NodeStatus: /api/space returns raw Offline/Online plus LastSeenAt/serverTime.
//   Consumers derive effective presence with lib/presence.ts.
//
// McpServerStatus mirrors every current domain value.
//
// SessionStatus: domain enum has Opening/Active/Closing/Closed/Failed/Denied.
//   The /api/sessions endpoint projects s.Status directly (enum name as string).
//
// GrantDto and SessionDto mirror the explicit anonymous projections in Endpoints.cs.
//   Those projections intentionally flatten most IDs to strings.
//
// JsonStringEnumConverter is registered globally in Program.cs
//   (builder.Services.ConfigureHttpJsonOptions) so all enum fields are serialized
//   as string names on every HTTP endpoint, not integers.

/** ASP.NET serializes our readonly record struct ID types as { value: string }. */
export interface IdValue { value: string; }

export type NodeStatus = 'Online' | 'Offline';
export type NodeKind = 'publisher' | 'agent';
export type McpServerStatus = 'Published' | 'Disabled' | 'Unavailable' | 'NeedsReauth';
export type AccessRequestStatus =
  | 'Pending' | 'Approved' | 'Denied' | 'Expired' | 'Canceled';
export type GrantStatus = 'Active' | 'Revoked';
export type SessionStatus = 'Opening' | 'Active' | 'Closing' | 'Closed' | 'Failed' | 'Denied';
/** 025: derived liveness — raw status, or 'Stale' when Active/Opening but a participant node is offline. */
export type SessionEffectiveStatus = SessionStatus | 'Stale';
export type SessionCloseReason =
  | 'Completed'
  | 'Revoked'
  | 'ServerDisabled'
  | 'ServerUnavailable'
  | 'PublisherOffline'
  | 'PayloadLimitExceeded'
  | 'CryptoFailure'
  | 'UserClosed'
  | 'ServiceRestart'
  | 'Error'
  | 'Abandoned';

// ── /api/space ────────────────────────────────────────────────────────────────
// Projected as anonymous object: Id, DisplayName, nodes[], mcpServers[],
// pendingAccessRequests[]. ASP.NET camelCases all properties.
// Strongly-typed Id structs serialize as { "value": "…" } — use IdValue.

export interface NodeDto {
  id: IdValue;
  displayName: string;
  status: NodeStatus;
  /** Node kind as returned by /api/space. Absent on older server versions → treat as 'publisher'. */
  kind?: NodeKind;
  lastSeenAt: string | null;
  /** Creation time used as the fallback age for never-seen cleanup; absent on older clouds. */
  createdAt?: string;
  /**
   * node-visibility-doctor (2026-07-02): host metadata collected from NodeHello, refreshed on
   * every heartbeat. Null when never sent (e.g. a legacy CLI) or not yet reported.
   */
  hostname?: string | null;
  os?: string | null;
  arch?: string | null;
  cliVersion?: string | null;
  /** Owner-editable note (≤500 chars), set via PATCH /api/nodes/{id}. Null when never set. */
  note?: string | null;
}

export interface McpServerDto {
  id: IdValue;
  displayName: string;
  status: McpServerStatus;
  /**
   * Increment 1 (HTTP MCP direct-to-Space): null for an `http_cloud` server — it has no
   * publisher node at all (PublisherNodeId is "" server-side by design, projected as null).
   * Every consumer MUST null-guard before calling getIdValue()/rendering a node link.
   */
  publisherNodeId: IdValue | null;
  lastSeenAt: string | null;
  /** Whether the publisher daemon currently asserts this server (021). */
  isAsserted: boolean;
  /** Raw publisher node id string (021). Null for `http_cloud` (Increment 1). */
  publisherNodeName: string | null;
  /** ISO-8601 datetime of the publisher node's last heartbeat, or null (021, or http_cloud). */
  publisherNodeLastSeenAt: string | null;
  /** Raw status of the publisher node ("Online" | "Offline"), or null (021, or http_cloud). */
  publisherNodeStatus: string | null;
  /** Increment 1 (HTTP MCP direct-to-Space, Finding 16 M5): "Stdio" | "http_cloud". */
  transport: string;
}

/** Р27: what a server's launch definition was before it most recently changed. */
export interface DefinitionChangeDto {
  changedAt: string;
  previousCommand?: string | null;
  previousArguments?: string | null;
  currentCommand?: string | null;
  currentArguments?: string | null;
}

export interface AccessRequestSummaryDto {
  id: IdValue;
  consumerId: IdValue;
  /** 028: resolved display name; falls back to short id when unavailable. */
  consumerDisplayName?: string;
  mcpServerId: IdValue;
  /** 028: resolved display name; falls back to short id when unavailable. */
  mcpServerDisplayName?: string;
  /**
   * O2: publisher node display name (mirrors AccessRequestDto.publisherNodeName on the detail
   * endpoint). Falls back to short id when unavailable; absent on older server versions.
   */
  publisherNodeName?: string | null;
  status: AccessRequestStatus;
  requestedAt: string;
  /**
   * Р27: present only when this server's launch definition changed after a previous approval.
   * Its presence is the reason the owner is being asked again — Р26 suspends permissions when a
   * re-publish changes what runs behind an approved name. Rendering it as a plain "changed" note
   * would defeat the purpose: the owner has to see the old and new command side by side, or the
   * safe default becomes a reflexive yes.
   */
  definitionChange?: DefinitionChangeDto | null;
}

export interface SpaceDto {
  id: IdValue;
  displayName: string;
  nodes: NodeDto[];
  mcpServers: McpServerDto[];
  pendingAccessRequests: AccessRequestSummaryDto[];
  /**
   * UTC ISO-8601 timestamp from the server at response time.
   * Used by the frontend to compute clock-skew-safe presence age.
   * Absent on older server versions — treat as undefined; degrade gracefully.
   */
  serverTime?: string;
  /**
   * Seconds after which a node's lastSeenAt is considered stale (presence = Offline).
   * Matches NodePresenceRules.StaleThreshold on the server.
   * Absent on older server versions — default to 90.
   */
  presenceStaleSeconds?: number;
}

// ── /api/access-requests/:id ─────────────────────────────────────────────────
// Projected as anonymous object with explicit lowercase field names.
// This endpoint already unwraps the Id structs to plain strings in
// Web/Endpoints.cs — keep flat strings here (no IdValue).

export interface AccessRequestDto {
  id: string;
  status: AccessRequestStatus;
  consumerId: string;
  agentNodeId: string;
  agentNodeName: string;
  mcpServerId: string;
  mcpServerName: string;
  /** Increment 1 (HTTP MCP direct-to-Space): null when mcpServerId is an http_cloud server —
   * no publisher node exists at all. Consumers MUST null-guard before rendering a node link. */
  publisherNodeId: string | null;
  publisherNodeName: string | null;
  requestedAt: string;
}

// ── /api/grants ──────────────────────────────────────────────────────────────
// Explicit projection from MapGrantEndpoints. IDs are flat strings and internal
// ownership/audit fields are deliberately omitted from this owner-facing contract.

export interface GrantDto {
  id: string;
  consumerId: string;
  mcpServerId: string;
  /** Resolved agent display name (consumerId → node DisplayName). Fall back to short id if absent. */
  agentName?: string;
  /** Resolved MCP server display name (mcpServerId → McpServer.DisplayName). Fall back to short id if absent. */
  serverName?: string;
  status: GrantStatus;
  approvedAt: string;
  revokedAt: string | null;
}

// ── POST /api/access-requests/:id/approve ────────────────────────────────────
// approve endpoint hand-projects grant.Id.Value → string and grant.Status.ToString()
// → GrantStatus name (safe because JsonStringEnumConverter is registered globally).

/** Response shape from POST /api/access-requests/{id}/approve. */
export interface ApproveAccessRequestResponseDto {
  id: string;        // approve endpoint hand-projects grant.Id.Value → string
  status: GrantStatus;  // grant.Status.ToString() → GrantStatus name (now safe with global JsonStringEnumConverter)
}

// ── /api/auth/me ─────────────────────────────────────────────────────────────
// GET + PUT /api/auth/me — account self-service identity response.

export interface ProviderLinkDto {
  provider: string;
  externalId: string;
  linkedAt: string;
}

export interface PendingEmailChangeDto {
  newEmail: string;
  expiresAt: string; // ISO-8601
}

export interface MeDto {
  userId: string;
  displayName: string | null;
  primaryEmail: string;
  /**
   * OAuth providers linked to this account.
   * The current backend does not project this field yet — treat as optional so
   * callers handle undefined defensively rather than crashing at `.length`.
   */
  providers?: ProviderLinkDto[];
  /** Non-null when there is an outstanding unconfirmed email-change request. */
  pendingEmailChange: PendingEmailChangeDto | null;
}

// ── /api/cli/tokens ──────────────────────────────────────────────────────────
// GET /api/cli/tokens — list of issued CLI tokens for the authenticated user.

export interface CliTokenDto {
  id: string;
  name: string;
  createdAt: string;
  lastUsedAt: string | null;
  expiresAt: string | null;
}

// ── /api/sessions ─────────────────────────────────────────────────────────────
// Projected as anonymous object. s.Id is a SessionId struct → { "value": "…" }.
// s.Status is a SessionStatus enum → string name.
// s.CloseReason is a SessionCloseReason? enum → string name or null (not integer).

export interface SessionDto {
  id: IdValue;
  /** Raw agent-client id (Agent-DX: id-based cross-nav to /grants?agent=). */
  consumerId: string;
  /** Resolved agent display name. Fall back to short session id if absent. */
  agentName?: string;
  /** Raw MCP server id (Agent-DX: id-based cross-nav to /grants?server=). */
  mcpServerId: string;
  /** Resolved MCP server display name. Fall back to short session id if absent. */
  serverName?: string;
  /**
   * Raw publisher-node id (Agent-DX: id-based cross-nav to /servers?node=). Increment 1 (HTTP
   * MCP direct-to-Space): null for a session against an http_cloud server — no publisher node
   * exists at all. Consumers MUST null-guard before calling getIdValue()/rendering a node link.
   */
  publisherNodeId: string | null;
  /** Resolved publisher-node display name. Fall back to short node id if absent. Null for
   * http_cloud (Increment 1). */
  publisherNodeName?: string | null;
  status: SessionStatus;
  /** 025: derived liveness from participant-node presence; render this, not raw `status`. */
  effectiveStatus: SessionEffectiveStatus;
  startedAt: string;
  endedAt: string | null;
  bytesClientToServer: number;
  bytesServerToClient: number;
  closeReason: SessionCloseReason | null;
}

// ── PATCH /api/nodes/{id} ────────────────────────────────────────────────────
// node-visibility-doctor (2026-07-02): owner-editable Note. Mirrors
// NodeEndpoints.MapNodeEndpoints's PatchNodeRequest body / anonymous response projection
// (apps/Korat.Cloud/Web/Endpoints.cs).

export interface PatchNodeRequest {
  /** null clears the note. */
  note: string | null;
}

export interface PatchedNodeDto {
  id: IdValue;
  displayName: string;
  note: string | null;
  updatedAt: string;
}

// ── /api/oauth/consents ─────────────────────────────────────────────────────────
// Space-MCP inc-2a, Task 8: owner console — OAuth consents (OpenIddict permanent
// authorizations carrying the korat:space property) for the signed-in owner. The endpoint
// hand-projects an anonymous object (see OAuthConsentEndpoints.cs) — plain strings, no
// IdValue-wrapped id structs.

export interface OAuthConsentDto {
  id: string;
  clientId: string | null;
  /** Resolved OpenIddict application display name. Falls back to clientId/shortId in the UI. */
  clientDisplayName: string | null;
  spaceId: string;
  /** Resolved Space display name. Falls back to spaceId/shortId in the UI. */
  spaceName: string;
  createdAt: string;
}

// ── /api/version ──────────────────────────────────────────────────────────────
// Build/runtime metadata for the console version footer (authenticated-only).
export interface VersionDto {
  commit: string;
  environment: string;
  region: string | null;
  machineId: string | null;
  imageRef: string | null;
  serverTimeUtc: string;
}
