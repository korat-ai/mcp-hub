import { createFileRoute } from '@tanstack/react-router';
import { Activity } from 'lucide-react';
import {
  Table, TableBody, TableCell, TableHead, TableHeader, TableRow,
} from '@/components/ui/table';
import { EmptyState } from '@/components/domain/EmptyState';
import { ErrorState } from '@/components/domain/ErrorState';
import { SessionStatusBadge } from '@/components/domain/StatusBadges';
import { TableSkeleton } from '@/components/domain/TableSkeleton';
import { EntityLink } from '@/components/domain/EntityLink';
import { ActiveFilterChip } from '@/components/domain/ActiveFilterChip';
import { Badge } from '@/components/ui/badge';
import { useSessions } from '@/hooks/useSessions';
import { formatTimestamp } from '@/lib/time';
import { getIdValue } from '@/lib/api';
import { shortId, formatBytes } from '@/lib/format';

type SessionsSearch = { agentName?: string; serverName?: string; node?: string };

export const Route = createFileRoute('/sessions')({
  validateSearch: (s: Record<string, unknown>): SessionsSearch => {
    const out: SessionsSearch = {};
    if (typeof s.agentName === 'string') out.agentName = s.agentName;
    if (typeof s.serverName === 'string') out.serverName = s.serverName;
    if (typeof s.node === 'string') out.node = s.node;
    return out;
  },
  component: SessionsPage,
});

const HEADERS = ['Session', 'Status', 'Started', 'Ended', 'C→S', 'S→C', 'Close reason'];

export function SessionsPage() {
  const sessions = useSessions();
  const { agentName, serverName, node } = Route.useSearch();
  const navigate = Route.useNavigate();

  if (sessions.isPending) return <TableSkeleton headers={HEADERS} />;

  if (sessions.isError) {
    return (
      <ErrorState
        message="Could not load sessions"
        detail={`GET /api/sessions — ${sessions.error.message}`}
        onRetry={() => sessions.refetch()}
      />
    );
  }

  if (sessions.data.length === 0) {
    return (
      <EmptyState
        icon={Activity}
        title="No sessions yet"
        hint="Sessions appear when a consumer connects through an approved permission."
      />
    );
  }

  const hasFilter = Boolean(agentName || serverName || node);

  // Increment 1 (HTTP MCP direct-to-Space): a session against an http_cloud server has a null
  // publisherNodeId (no publisher node exists at all) — it can never match a real `node` filter,
  // so guard rather than let getIdValue(null) throw while scanning every row.
  const rows = sessions.data.filter(
    (s) =>
      (!agentName || s.agentName === agentName) &&
      (!serverName || s.serverName === serverName) &&
      (!node || (s.publisherNodeId != null && getIdValue(s.publisherNodeId) === node)),
  );

  // Resolve node chip label from first match in full list, fallback to shortId (mirrors grants.tsx).
  const filterLabel = agentName
    ? agentName
    : serverName
      ? serverName
      : node
        ? (sessions.data.find((s) => s.publisherNodeId != null && getIdValue(s.publisherNodeId) === node)
          ?.publisherNodeName ?? shortId(node))
        : '';

  const filterDimension = agentName ? 'Consumer' : serverName ? 'Server' : node ? 'Runtime' : undefined;

  return (
    <div className="flex flex-col gap-3">
      <div className="flex items-center justify-between gap-2">
        {hasFilter ? (
          <ActiveFilterChip
            label={filterLabel}
            dimension={filterDimension}
            onClear={() =>
              navigate({
                search: () => ({}),
              })
            }
          />
        ) : (
          <span />
        )}
        <Badge variant="outline" className="font-mono text-muted-foreground">
          {rows.length}
        </Badge>
      </div>

      {hasFilter && rows.length === 0 ? (
        <EmptyState
          icon={Activity}
          title="No sessions match this filter"
          hint="Try clearing the filter to see all sessions."
        />
      ) : (
        <Table>
          <TableHeader>
            <TableRow>
              <TableHead className="eyebrow">Session</TableHead>
              <TableHead className="eyebrow">Status</TableHead>
              <TableHead className="eyebrow">Started</TableHead>
              <TableHead className="eyebrow">Ended</TableHead>
              <TableHead className="eyebrow text-right">C→S</TableHead>
              <TableHead className="eyebrow text-right">S→C</TableHead>
              <TableHead className="eyebrow">Close reason</TableHead>
            </TableRow>
          </TableHeader>
          <TableBody>
            {rows.map((s) => {
              const sessionId = getIdValue(s.id);
              const agentId = getIdValue(s.consumerId);
              const serverId = getIdValue(s.mcpServerId);
              // Increment 1 (HTTP MCP direct-to-Space): null for a session against an http_cloud
              // server (no publisher node exists at all) — guard before unwrapping (Task-6-gate
              // HIGH fix; getIdValue(null) throws).
              const nodeId = s.publisherNodeId ? getIdValue(s.publisherNodeId) : null;
              return (
                <TableRow key={sessionId}>
                  {/* Stacked "Session" cell (-3 parity): id over an agent · server · node
                      breadcrumb, each an EntityLink cross-navigating to its own view. */}
                  <TableCell>
                    <div className="flex flex-col items-start gap-1">
                      <span className="font-mono text-xs font-medium text-foreground">
                        {shortId(sessionId)}
                      </span>
                      <span className="flex items-center gap-1.5 text-xs text-muted-foreground">
                        <EntityLink
                          name={s.agentName}
                          rawId={agentId}
                          to={s.agentName ? '/grants' : undefined}
                          search={s.agentName ? { agent: agentId } : undefined}
                        />
                        <span className="opacity-50">·</span>
                        <EntityLink
                          name={s.serverName}
                          rawId={serverId}
                          to={s.serverName ? '/servers/$serverId' : undefined}
                          params={s.serverName ? { serverId } : undefined}
                        />
                        <span className="opacity-50">·</span>
                        {nodeId === null ? (
                          // Finding 16, M5 / spec §11 decision 3: disclosed, always — no
                          // publisher node to link to (this session's server is http_cloud).
                          <span title="Cloud-terminated: this server has no publisher node.">
                            cloud-terminated
                          </span>
                        ) : (
                          <EntityLink
                            name={s.publisherNodeName ?? undefined}
                            rawId={nodeId}
                            to={s.publisherNodeName ? '/nodes/$name' : undefined}
                            params={s.publisherNodeName ? { name: nodeId } : undefined}
                          />
                        )}
                      </span>
                    </div>
                  </TableCell>
                  <TableCell><SessionStatusBadge status={s.effectiveStatus} /></TableCell>
                  <TableCell className="font-mono text-muted-foreground">{formatTimestamp(s.startedAt)}</TableCell>
                  <TableCell className="font-mono text-muted-foreground">{formatTimestamp(s.endedAt)}</TableCell>
                  <TableCell className="text-right font-mono tabular-nums">{formatBytes(s.bytesClientToServer)}</TableCell>
                  <TableCell className="text-right font-mono tabular-nums">{formatBytes(s.bytesServerToClient)}</TableCell>
                  <TableCell className="font-mono text-muted-foreground">{s.closeReason ?? '—'}</TableCell>
                </TableRow>
              );
            })}
          </TableBody>
        </Table>
      )}
    </div>
  );
}
