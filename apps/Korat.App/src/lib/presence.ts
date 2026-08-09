/**
 * Presence derivation helpers (spec 019).
 *
 * The server returns raw facts: explicit `status` (Online/Offline from stream
 * lifecycle) and `lastSeenAt` (advanced by every heartbeat).  The frontend
 * derives the effective online indicator so it can tick live without polling.
 *
 * Clock-skew-safe formula
 * -----------------------
 * Capture skew once per fetch:
 *   skew = Date.now() - serverTimeMs          // +ve = client ahead
 *
 * Then at any tick:
 *   serverNowMs = Date.now() - skew
 *   ageMs       = serverNowMs - lastSeenMs
 *   online      = status === 'Online' && ageMs < presenceStaleMs
 *
 * This avoids treating a client whose clock is 5 min ahead as "stale" and
 * avoids keeping a node online when the client clock is behind the server.
 */

import type { McpServerStatus, NodeStatus } from '@/types/api';

const DEFAULT_STALE_SECONDS = 90;

/**
 * Compute clock skew (ms) from one fetch response.
 * Returns 0 when serverTime is absent (safe fallback = use local clock as-is).
 */
export function computeSkew(serverTime: string | undefined): number {
  if (!serverTime) return 0;
  const serverMs = new Date(serverTime).getTime();
  if (Number.isNaN(serverMs)) return 0;
  return Date.now() - serverMs;
}

/**
 * Derive whether a node is effectively online.
 *
 * @param status          - Raw stored status from the server ("Online" | "Offline").
 * @param lastSeenAt      - ISO-8601 string of the node's last heartbeat, or null.
 * @param presenceStaleSeconds - Stale threshold in seconds (default 90 when absent).
 * @param skewMs          - Clock skew computed via computeSkew() for this response.
 * @param nowMs           - Current wall-clock time (Date.now()); injectable for tests.
 */
export function isNodeOnline(
  status: NodeStatus | string,
  lastSeenAt: string | null | undefined,
  presenceStaleSeconds: number | undefined,
  skewMs: number,
  nowMs: number = Date.now(),
): boolean {
  // Explicit offline — clean disconnect. No need to check age.
  if (status !== 'Online') return false;

  if (!lastSeenAt) return false;
  const lastSeenMs = new Date(lastSeenAt).getTime();
  if (Number.isNaN(lastSeenMs)) return false;

  const staleMs = (presenceStaleSeconds ?? DEFAULT_STALE_SECONDS) * 1000;
  // Adjust nowMs to server-reference frame to cancel clock skew.
  const serverNowMs = nowMs - skewMs;
  const ageMs = serverNowMs - lastSeenMs;

  return ageMs < staleMs;
}

/**
 * Tri-state server availability (spec 021).
 *
 * Precedence (explicit per spec):
 *   1. Disabled  — stored owner intent; wins over everything.
 *   2. Available — Published + isAsserted + owner node online.
 *   3. Unavailable — everything else (not asserted OR owner offline).
 */
export type ServerAvailability = 'Available' | 'Unavailable' | 'Disabled' | 'NeedsReauth';

export function deriveServerAvailability(
  status: McpServerStatus | string,
  isAsserted: boolean,
  publisherNodeStatus: string | null | undefined,
  publisherNodeLastSeenAt: string | null | undefined,
  presenceStaleSeconds: number | undefined,
  skewMs: number,
  nowMs: number = Date.now(),
  transport?: string | null,
): ServerAvailability {
  // Disabled takes priority — it is stored owner intent.
  if (status === 'Disabled') return 'Disabled';

  // Finding 16, M5: http_cloud has no publisher node to check presence against — availability
  // collapses to Published alone. Spec §10: "http_cloud availability is Published && !NeedsReauth"
  // — NeedsReauth is reserved-but-unproduced this increment (Task 1, S1), so the check below is
  // already forward-compatible with no further code change needed once Increment 2 wires it in
  // (a future McpServerDto.status value of "NeedsReauth" would simply fail the === 'Published'
  // check here, exactly as intended, without touching this function again).
  if (transport === 'http_cloud') {
    // Increment 2 (HTTP MCP OAuth): NeedsReauth is checked BEFORE the Published check so it is
    // never shadowed by "anything not Published is Unavailable" — the owner needs a visibly
    // DIFFERENT signal (Reconnect action) from a merely-unavailable server.
    if (status === 'NeedsReauth') return 'NeedsReauth';
    return status === 'Published' ? 'Available' : 'Unavailable';
  }

  // Owner node must be online (reuse isNodeOnline verbatim). UNCHANGED for stdio_node.
  const ownerOnline = isNodeOnline(
    publisherNodeStatus ?? 'Offline',
    publisherNodeLastSeenAt,
    presenceStaleSeconds,
    skewMs,
    nowMs,
  );

  if (status === 'Published' && isAsserted && ownerOnline) return 'Available';
  return 'Unavailable';
}
