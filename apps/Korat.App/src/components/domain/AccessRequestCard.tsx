import { ChevronRight } from 'lucide-react';
import { Link } from '@tanstack/react-router';
import { Button } from '@/components/ui/button';
import { Badge } from '@/components/ui/badge';
import { relativeFromNow } from '@/lib/time';
import { getIdValue } from '@/lib/api';
import type { AccessRequestSummaryDto } from '@/types/api';
import { EntityLink } from '@/components/domain/EntityLink';
import { DefinitionChangeNotice } from '@/components/domain/DefinitionChangeNotice';

interface Props {
  request: AccessRequestSummaryDto;
  onApprove: () => void;
  onDeny: () => void;
  /** Approve mutation in flight for THIS request — drives the "Approving…" label. */
  approvePending: boolean;
  /** Deny mutation in flight for THIS request. */
  denyPending: boolean;
  /** Live clock from useNow — pass to keep the relative timestamp ticking without per-card timers. */
  nowMs?: number;
}

export function AccessRequestCard({ request, onApprove, onDeny, approvePending, denyPending, nowMs }: Props) {
  const requestId = getIdValue(request.id);
  const agentId = getIdValue(request.consumerId);
  const serverId = getIdValue(request.mcpServerId);
  // Both buttons stay disabled while EITHER action is in flight (prevents a
  // double-submit racing the other mutation), but each button's LABEL keys
  // off its own pending flag only — see the bug this fixes: a single shared
  // `isPending` used to make Approve mislabel "Approving…" while a Deny was
  // still running.
  const anyPending = approvePending || denyPending;

  return (
    <div className="border border-border/40 rounded-lg bg-card">
      {/* Top row: text info + open-detail icon (all breakpoints) + (desktop) action buttons */}
      <div className="flex items-center gap-3 px-4 py-3">
        <div className="flex-1 min-w-0">
          <div className="text-sm font-semibold truncate">
            {/* #117: agent/server labels link to their grant lists */}
            <EntityLink
              name={request.consumerDisplayName}
              rawId={agentId}
              to="/grants"
              search={{ agent: agentId }}
            />
            {' → '}
            <EntityLink
              name={request.mcpServerDisplayName}
              rawId={serverId}
              to="/grants"
              search={{ server: serverId }}
            />
          </div>
          <div className="text-xs text-muted-foreground">
            {/* GAP O2 / Increment 1 (HTTP MCP direct-to-Space, carried LOW from Task 6's
                review): publisher node name, when the server sends one. A null/absent
                publisherNodeName specifically means the request is against an http_cloud
                server — it has no publisher node at all (same null-means-http_cloud signal
                already used for AccessRequestDto.publisherNodeName in
                routes/approve.$requestId.tsx and McpServerDto.publisherNodeName in
                routes/servers.tsx) — so it must be DISCLOSED, always (spec §11 decision 3),
                not silently dropped via the old `&&` short-circuit (which left this row with
                no publisher segment whatsoever, cloud-terminated or not). */}
            {request.publisherNodeName ? (
              <>
                {request.publisherNodeName}
                <span className="opacity-50"> · </span>
              </>
            ) : (
              <>
                <Badge
                  variant="outline"
                  title="Cloud-terminated: this server has no e2e encryption — the cloud connects to it directly."
                >
                  Cloud-terminated
                </Badge>
                <span className="opacity-50"> · </span>
              </>
            )}
            {'requested '}
            {relativeFromNow(request.requestedAt, nowMs)}
            <span className="font-mono ml-2 opacity-60" title={requestId}>
              · {requestId.slice(0, 8)}
            </span>
          </div>
        </div>
        {/* GAP O1: open-detail affordance — visible at every breakpoint (previously
            hidden below 720px), so the full /approve/$requestId screen stays reachable
            from a phone, alongside the mobile Deny/Approve row below. */}
        <Button variant="ghost" size="icon-sm" asChild className="shrink-0">
          <Link
            to="/approve/$requestId"
            params={{ requestId }}
            aria-label="Open request details"
          >
            <ChevronRight className="size-4" aria-hidden="true" />
          </Link>
        </Button>
        {/* Action buttons — on desktop inline (size="sm"), on mobile full-width below */}
        <Button
          variant="outline"
          size="sm"
          disabled={anyPending}
          onClick={onDeny}
          className="min-[720px]:inline-flex hidden"
        >
          Deny
        </Button>
        {/* aria-label keeps the accessible name stable as "Approve" while the busy
            label swaps the visible text to "Approving…" — see AccessRequestCard.test.tsx
            which queries buttons by accessible name /approve/i. */}
        <Button
          size="sm"
          aria-label="Approve"
          disabled={anyPending}
          onClick={onApprove}
          className="min-[720px]:inline-flex hidden"
        >
          {approvePending ? 'Approving…' : 'Approve'}
        </Button>
      </div>
      {/* Р27: the diff sits ABOVE the buttons and spans the card, not tucked into the metadata
          line — an owner scanning a list of requests must not be able to approve this one without
          the change having crossed their field of view. */}
      {request.definitionChange ? (
        <div className="px-4 pb-3">
          <DefinitionChangeNotice change={request.definitionChange} />
        </div>
      ) : null}
      {/* Mobile action buttons — stacked full width, comfortable tap targets (h-11 = 44px) */}
      <div className="min-[720px]:hidden flex gap-2 px-4 pb-3">
        <Button
          variant="outline"
          className="flex-1 h-11"
          disabled={anyPending}
          onClick={onDeny}
        >
          Deny
        </Button>
        <Button
          className="flex-1 h-11"
          aria-label="Approve"
          disabled={anyPending}
          onClick={onApprove}
        >
          {approvePending ? 'Approving…' : 'Approve'}
        </Button>
      </div>
    </div>
  );
}
