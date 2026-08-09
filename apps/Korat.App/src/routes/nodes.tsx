import { createFileRoute, Outlet, Link } from '@tanstack/react-router';
import { useMemo } from 'react';
import { Cpu, Plus, ChevronRight } from 'lucide-react';
import {
  Table, TableBody, TableCell, TableHead, TableHeader, TableRow,
} from '@/components/ui/table';
import { Button } from '@/components/ui/button';
import { EmptyState } from '@/components/domain/EmptyState';
import { ErrorState } from '@/components/domain/ErrorState';
import { ActiveFilterChip } from '@/components/domain/ActiveFilterChip';
import { StatusBadge } from '@/components/domain/StatusBadges';
import { TableSkeleton } from '@/components/domain/TableSkeleton';
import { EntityLink } from '@/components/domain/EntityLink';
import { useSpace } from '@/hooks/useSpace';
import { useSessions } from '@/hooks/useSessions';
import { useNow } from '@/hooks/useNow';
import { useAtChildRoute } from '@/hooks/useAtChildRoute';
import { relativeFromNow } from '@/lib/time';
import { getIdValue } from '@/lib/api';
import { shortId } from '@/lib/format';
import { rowClickProps } from '@/lib/a11y';
import { computeSkew, isNodeOnline } from '@/lib/presence';

type NodesSearch = { name?: string };

export const Route = createFileRoute('/nodes')({
  // `name` filters by the node's real id (NodesScreen parity — the -3
  // prototype's `filter.name` is never actually set by any in-app nav today,
  // kept for structural parity + as an entry point future cross-nav can use).
  validateSearch: (s: Record<string, unknown>): NodesSearch => {
    const out: NodesSearch = {};
    if (typeof s.name === 'string') out.name = s.name;
    return out;
  },
  component: NodesPage,
});

// Named export for tests that import the component directly.
export { NodesPage };

const HEADERS = ['Name', 'Host', 'Status', 'Servers', 'Sessions', 'Last seen', ''];

function NodesPage() {
  const atDetail = useAtChildRoute('/nodes/$name');

  const space = useSpace();
  const sessions = useSessions();
  const { name: nameFilter } = Route.useSearch();
  const navigate = Route.useNavigate();
  // Single shared interval — all rows re-evaluate on the same tick.
  const nowMs = useNow(8_000);

  // Compute clock skew once per space fetch, not on every tick.
  const skewMs = useMemo(
    () => computeSkew(space.data?.serverTime),
    // Recompute only when serverTime changes (i.e. after a refetch).
     
    [space.data?.serverTime],
  );

  if (atDetail) return <Outlet />;

  if (space.isPending) return <TableSkeleton headers={HEADERS} />;

  if (space.isError) {
    return (
      <ErrorState
        message="Could not load runtimes"
        detail={`GET /api/space — ${space.error.message}`}
        onRetry={() => space.refetch()}
      />
    );
  }

  // Agent-kind rows are synthetic consumer identities, not distinct machines. Keep them in the
  // transport model for routing/TOFU, but do not expose them as owner-facing runtimes.
  const runtimes = space.data.nodes.filter((node) => node.kind !== 'agent');

  if (runtimes.length === 0) {
    return (
      <>
        <EmptyState
          icon={Cpu}
          title="No publisher runtimes"
          hint="Run `korat login` then `korat service install` on a machine that hosts MCP servers."
        />
        {/* Outlet renders the how_to_add child route as a modal overlay when navigated to */}
        <Outlet />
      </>
    );
  }

  const { presenceStaleSeconds } = space.data;

  // Client-side filter by node id (see validateSearch comment above).
  const filteredNodes = nameFilter
    ? runtimes.filter((n) => getIdValue(n.id) === nameFilter)
    : runtimes;

  const chipLabel = nameFilter
    ? (filteredNodes[0]?.displayName ?? shortId(nameFilter))
    : undefined;

  // Per-node counts (NodesScreen parity — srvOf/sessOf), derived live from the
  // already-fetched /api/space and /api/sessions collections.
  // Increment 1 (HTTP MCP direct-to-Space): an http_cloud server/session has a null
  // publisherNodeId (no publisher node exists at all) — it can never belong to a real node's
  // count, so guard rather than let getIdValue(null) throw while scanning every row.
  const serverCountByNode = (nodeId: string) =>
    space.data.mcpServers.filter((m) => m.publisherNodeId != null && getIdValue(m.publisherNodeId) === nodeId).length;
  const sessionCountByNode = (nodeId: string) =>
    (sessions.data ?? []).filter((s) => s.publisherNodeId != null && getIdValue(s.publisherNodeId) === nodeId).length;

  return (
    <div className="flex flex-col gap-4">
      <div className="flex justify-end">
        <Button size="sm" asChild>
          <Link to="/nodes/how_to_add">
            <Plus />
            Add Runtime
          </Link>
        </Button>
      </div>

      {nameFilter && chipLabel !== undefined && (
        <ActiveFilterChip
          label={chipLabel}
          dimension="Runtime"
          onClear={() => navigate({ search: (p) => ({ ...p, name: undefined }) })}
        />
      )}

      {filteredNodes.length === 0 && nameFilter ? (
        <EmptyState
          icon={Cpu}
          title="No such runtime"
          hint="Clear the filter above to see all publisher runtimes."
        />
      ) : (
        <Table>
          <TableHeader>
            <TableRow>
              <TableHead className="eyebrow">Name</TableHead>
              <TableHead className="eyebrow">Host</TableHead>
              <TableHead className="eyebrow">Status</TableHead>
              <TableHead className="eyebrow text-right">Servers</TableHead>
              <TableHead className="eyebrow text-right">Sessions</TableHead>
              <TableHead className="eyebrow text-right">Last seen</TableHead>
              <TableHead className="eyebrow text-right" />
            </TableRow>
          </TableHeader>
          <TableBody>
            {filteredNodes.map((n) => {
              const nodeId = getIdValue(n.id);
              const online = isNodeOnline(n.status, n.lastSeenAt, presenceStaleSeconds, skewMs, nowMs);
              // "last seen" relative time uses server-skew-corrected now so the clock
              // matches how presence was derived.
              const serverNowMs = nowMs - skewMs;
              return (
                // Row → detail (NodesScreen parity): the whole row opens /nodes/$name.
                // "Servers on this node" — previously reached by clicking the node
                // name straight into a filtered /servers list — now lives inside the
                // detail page's "MCP servers published" section (with a link back
                // out to the filtered list from there).
                <TableRow
                  key={nodeId}
                  className="cursor-pointer"
                  {...rowClickProps(() => navigate({ to: '/nodes/$name', params: { name: nodeId } }))}
                >
                  <TableCell
                    className="font-semibold"
                    title={nodeId}
                    onClick={(e) => e.stopPropagation()}
                    onKeyDown={(e) => e.stopPropagation()}
                  >
                    {/* Fable review (#186 MEDIUM-1): a real focusable Link restores keyboard/
                        screen-reader navigation now that the <tr> no longer carries
                        role="button"/tabIndex/onKeyDown. */}
                    <div className="flex flex-col">
                      <EntityLink
                        name={n.displayName}
                        rawId={nodeId}
                        to="/nodes/$name"
                        params={{ name: nodeId }}
                      />
                      {n.note && (
                        <span
                          className="max-w-[220px] truncate text-xs font-normal text-muted-foreground"
                          title={n.note}
                        >
                          {n.note}
                        </span>
                      )}
                    </div>
                  </TableCell>
                  <TableCell className="font-mono text-muted-foreground">
                    <div className="flex flex-col">
                      <span>{n.hostname ?? shortId(nodeId)}</span>
                      {n.os && (
                        <span className="text-[10px] uppercase text-muted-foreground/60">{n.os}</span>
                      )}
                    </div>
                  </TableCell>
                  <TableCell>
                    <StatusBadge
                      tone={online ? 'good' : 'idle'}
                      label={online ? 'Online' : 'Offline'}
                    />
                  </TableCell>
                  <TableCell className="text-right font-mono tabular-nums text-muted-foreground">
                    {serverCountByNode(nodeId) || '—'}
                  </TableCell>
                  <TableCell className="text-right font-mono tabular-nums text-muted-foreground">
                    {sessionCountByNode(nodeId) || '—'}
                  </TableCell>
                  <TableCell className="text-right font-mono text-muted-foreground">
                    {relativeFromNow(n.lastSeenAt, serverNowMs)}
                  </TableCell>
                  <TableCell className="text-right">
                    <ChevronRight className="ml-auto size-4 text-muted-foreground" aria-hidden="true" />
                  </TableCell>
                </TableRow>
              );
            })}
          </TableBody>
        </Table>
      )}

      {/* Outlet renders the how_to_add child route as a modal overlay when navigated to */}
      <Outlet />
    </div>
  );
}
