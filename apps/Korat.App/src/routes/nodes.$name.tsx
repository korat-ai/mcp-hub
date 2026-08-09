/**
 * /nodes/$name — NodeDetailScreen (spec parity with the -3 prototype).
 *
 * Despite the `$name` segment (kept for parity with the manifest's file
 * naming), the param value is the node's real id (getIdValue(node.id)) — not
 * its display name — per the "use the real identifier" hard rule.
 *
 * All three related-entity lists (servers/sessions/inference points) are
 * derived client-side from data already fetched by /api/space, /api/sessions
 * and /api/inference-points:
 *  - servers on node:    mcpServers where publisherNodeId === this node's id.
 *  - inference on node:  inference points where nodeId === this node's id
 *                        (InferencePointDto.nodeId is a plain string).
 *  - sessions on node:   SessionDto.publisherNodeId === this node's id
 *                        (matches sessions.tsx's own `?node=` filter join).
 */
import { createFileRoute, Link, useNavigate } from '@tanstack/react-router';
import { useMemo, useState } from 'react';
import { Cpu, Server, Activity, StickyNote } from 'lucide-react';
import { Card } from '@/components/ui/card';
import { Button } from '@/components/ui/button';
import { Skeleton } from '@/components/ui/skeleton';
import { EmptyState } from '@/components/domain/EmptyState';
import { ErrorState } from '@/components/domain/ErrorState';
import { DetailBack } from '@/components/domain/DetailBack';
import { DetailRow } from '@/components/domain/DetailRow';
import { MiniSection } from '@/components/domain/MiniSection';
import { MiniRow } from '@/components/domain/MiniRow';
import { EntityLink } from '@/components/domain/EntityLink';
import {
  StatusBadge,
  ServerAvailabilityBadge,
  SessionStatusBadge,
} from '@/components/domain/StatusBadges';
import { useSpace } from '@/hooks/useSpace';
import { useSessions } from '@/hooks/useSessions';
import { useNow } from '@/hooks/useNow';
import { useUpdateNodeNote } from '@/hooks/useUpdateNodeNote';
import { computeSkew, isNodeOnline, deriveServerAvailability } from '@/lib/presence';
import { getIdValue, ApiError } from '@/lib/api';
import { shortId } from '@/lib/format';
import { relativeFromNow } from '@/lib/time';
import type { NodeDto } from '@/types/api';

export const Route = createFileRoute('/nodes/$name')({
  validateSearch: (): Record<string, never> => ({}),
  component: NodeDetailPage,
});

/** Exported for test harness. */
export { NodeDetailPage };

// ── Owner-editable note (node-visibility-doctor, 2026-07-02) ──────────────────
//
// Inline edit + save via PATCH /api/nodes/{id} (useUpdateNodeNote → TanStack Query mutation +
// invalidate), mirroring InferencePointEditForm's partial-update / inline-error idiom but scaled
// down to this single field — no dialog needed.
//
// "Adjust state during render" reset keyed on nodeId (same pattern as inference.$pointId.tsx's
// L5 prevPointId guard): NodeDetailPage does not remount when navigating directly between two
// nodes' detail pages (e.g. via an EntityLink from elsewhere), so local draft/editing state must
// be reset explicitly rather than relying on unmount.

interface NodeNoteCardProps {
  node: NodeDto;
}

function NodeNoteCard({ node }: NodeNoteCardProps) {
  const nodeId = getIdValue(node.id);
  const update = useUpdateNodeNote();

  const [prevNodeId, setPrevNodeId] = useState(nodeId);
  const [editing, setEditing] = useState(false);
  const [value, setValue] = useState(node.note ?? '');
  const [submitError, setSubmitError] = useState<string | null>(null);
  if (nodeId !== prevNodeId) {
    setPrevNodeId(nodeId);
    setEditing(false);
    setValue(node.note ?? '');
    setSubmitError(null);
  }

  function startEditing() {
    setValue(node.note ?? '');
    setSubmitError(null);
    setEditing(true);
  }

  function cancelEditing() {
    setEditing(false);
    setValue(node.note ?? '');
    setSubmitError(null);
  }

  function handleSave() {
    setSubmitError(null);
    const trimmed = value.trim();
    update.mutate(
      { nodeId, displayName: node.displayName, note: trimmed === '' ? null : trimmed },
      {
        onSuccess: () => setEditing(false),
        onError: (err) =>
          setSubmitError(err instanceof ApiError ? err.message : 'Could not save the note.'),
      },
    );
  }

  return (
    <Card className="p-4 flex flex-col gap-2">
      <div className="flex items-center justify-between gap-2">
        <div className="flex items-center gap-1.5 eyebrow">
          <StickyNote className="size-3.5" aria-hidden="true" />
          <span>Note</span>
        </div>
        {!editing && (
          <Button variant="ghost" size="sm" onClick={startEditing}>
            {node.note ? 'Edit' : 'Add note'}
          </Button>
        )}
      </div>

      {editing ? (
        <div className="flex flex-col gap-2">
          <textarea
            value={value}
            onChange={(e) => setValue(e.target.value)}
            maxLength={500}
            rows={3}
            placeholder="Add a note for this runtime…"
            aria-label="Runtime note"
            className="w-full rounded-md border border-input bg-background px-3 py-2 text-sm focus:outline-none focus:ring-2 focus:ring-ring"
          />
          {submitError && (
            <p role="alert" className="text-xs text-destructive">
              {submitError}
            </p>
          )}
          <div className="flex items-center gap-2">
            <Button size="sm" disabled={update.isPending} onClick={handleSave}>
              {update.isPending ? 'Saving…' : 'Save'}
            </Button>
            <Button size="sm" variant="outline" disabled={update.isPending} onClick={cancelEditing}>
              Cancel
            </Button>
          </div>
        </div>
      ) : (
        <p className="text-sm text-muted-foreground whitespace-pre-wrap">
          {node.note ?? 'No note yet.'}
        </p>
      )}
    </Card>
  );
}

function NodeDetailPage() {
  const { name: nodeId } = Route.useParams();
  const space = useSpace();
  const sessions = useSessions();
  const nowMs = useNow(8_000);
  const navigate = useNavigate();

  const skewMs = useMemo(
    () => computeSkew(space.data?.serverTime),
     
    [space.data?.serverTime],
  );

  if (space.isPending) {
    return (
      <div className="flex flex-col gap-4">
        <Skeleton className="h-4 w-20" />
        <Skeleton className="h-8 w-48" />
        <Skeleton className="h-36 w-full" />
      </div>
    );
  }

  if (space.isError) {
    return (
      <ErrorState
        message="Could not load runtime"
        detail={`GET /api/space — ${space.error.message}`}
        onRetry={() => space.refetch()}
      />
    );
  }

  const node = space.data.nodes.find((n) => getIdValue(n.id) === nodeId);

  if (!node) {
    return (
      <div className="flex flex-col gap-4">
        <DetailBack to="/nodes" label="Runtimes" />
        <EmptyState icon={Cpu} title="Runtime not found" hint="It may have disconnected." />
      </div>
    );
  }

  const { presenceStaleSeconds } = space.data;
  const serverNowMs = nowMs - skewMs;
  const online = isNodeOnline(node.status, node.lastSeenAt, presenceStaleSeconds, skewMs, nowMs);

  // Servers published from this node. Increment 1 (HTTP MCP direct-to-Space): an http_cloud
  // server's publisherNodeId is null (no publisher node exists at all) — it can never belong to
  // this (or any) node, so guard rather than let getIdValue(null) throw while scanning every row.
  const servers = space.data.mcpServers.filter(
    (m) => m.publisherNodeId != null && getIdValue(m.publisherNodeId) === nodeId,
  );

  // Sessions "on this node" — SessionDto.publisherNodeId is the real id of the
  // node that published the server the session went through. Same http_cloud null-guard as above.
  const nodeSessions = sessions.data?.filter(
    (s) => s.publisherNodeId != null && getIdValue(s.publisherNodeId) === nodeId,
  ) ?? [];

  return (
    <div className="flex flex-col gap-5">
      <DetailBack to="/nodes" label="Runtimes" />

      <div className="flex flex-wrap items-center gap-3">
        <h1 className="text-2xl font-semibold tracking-tight">{node.displayName}</h1>
        <StatusBadge tone={online ? 'good' : 'idle'} label={online ? 'Online' : 'Offline'} />
      </div>

      <Card className="px-4 py-1">
        <DetailRow label="Host" value={node.hostname ?? shortId(nodeId)} mono />
        <DetailRow label="OS" value={node.os ?? '—'} mono />
        <DetailRow label="Arch" value={node.arch ?? '—'} mono />
        <DetailRow label="CLI version" value={node.cliVersion ?? '—'} mono />
        <DetailRow label="Status" value={node.status} mono />
        <DetailRow label="Last seen" value={relativeFromNow(node.lastSeenAt, serverNowMs)} mono />
      </Card>

      <NodeNoteCard node={node} />

      <MiniSection icon={Server} title="MCP servers published" count={servers.length}>
        <Card className="overflow-hidden p-0">
          {servers.length === 0 ? (
            <div className="px-4 py-4 text-center text-xs text-muted-foreground">
              No servers published from this runtime.
            </div>
          ) : (
            servers.map((m) => {
              const availability = deriveServerAvailability(
                m.status,
                m.isAsserted ?? true,
                node.status,
                node.lastSeenAt,
                presenceStaleSeconds,
                skewMs,
                nowMs,
              );
              const serverId = getIdValue(m.id);
              return (
                <MiniRow
                  key={serverId}
                  onClick={() => navigate({ to: '/servers/$serverId', params: { serverId } })}
                >
                  <span className="flex-1 font-medium">{m.displayName}</span>
                  <ServerAvailabilityBadge availability={availability} />
                </MiniRow>
              );
            })
          )}
        </Card>
      </MiniSection>


      <MiniSection icon={Activity} title="Sessions" count={nodeSessions.length}>
        <Card className="overflow-hidden p-0">
          {sessions.isPending ? (
            <div className="px-4 py-4 text-center text-xs text-muted-foreground">Loading sessions…</div>
          ) : sessions.isError ? (
            <div className="px-4 py-4 text-center text-xs text-destructive">Could not load sessions.</div>
          ) : nodeSessions.length === 0 ? (
            <div className="px-4 py-4 text-center text-xs text-muted-foreground">
              No sessions on this runtime.
            </div>
          ) : (
            nodeSessions.map((s) => {
              const sessionId = getIdValue(s.id);
              const agentId = getIdValue(s.consumerId);
              const serverId = getIdValue(s.mcpServerId);
              return (
                <MiniRow key={sessionId}>
                  <code className="font-mono text-xs text-foreground">{shortId(sessionId)}</code>
                  <EntityLink
                    name={s.agentName}
                    rawId={agentId}
                    to={s.agentName ? '/grants' : undefined}
                    search={s.agentName ? { agent: agentId } : undefined}
                  />
                  <span className="font-mono text-xs text-muted-foreground">→</span>
                  <EntityLink
                    name={s.serverName}
                    rawId={serverId}
                    to={s.serverName ? '/grants' : undefined}
                    search={s.serverName ? { server: serverId } : undefined}
                  />
                  <div className="flex-1" />
                  <SessionStatusBadge status={s.effectiveStatus} />
                </MiniRow>
              );
            })
          )}
        </Card>
      </MiniSection>

      {/* Escape hatches to the full filtered list views for this node — both
          servers.tsx (?node=) and sessions.tsx (?node=) already support this. */}
      <div className="flex flex-wrap gap-4">
        {servers.length > 0 && (
          <Link
            to="/servers"
            search={{ node: nodeId }}
            className="w-fit text-xs text-muted-foreground underline-offset-4 hover:text-foreground hover:underline"
          >
            View in MCP servers list →
          </Link>
        )}
        {nodeSessions.length > 0 && (
          <Link
            to="/sessions"
            search={{ node: nodeId }}
            className="w-fit text-xs text-muted-foreground underline-offset-4 hover:text-foreground hover:underline"
          >
            View in sessions list →
          </Link>
        )}
      </div>
    </div>
  );
}
