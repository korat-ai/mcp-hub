import { createFileRoute, Outlet, Link, useNavigate } from '@tanstack/react-router';
import { useMemo } from 'react';
import { Server, Plus } from 'lucide-react';
import {
  Table, TableBody, TableCell, TableHead, TableHeader, TableRow,
} from '@/components/ui/table';
import { Button } from '@/components/ui/button';
import { EmptyState } from '@/components/domain/EmptyState';
import { ErrorState } from '@/components/domain/ErrorState';
import { AgentConnectCard } from '@/components/domain/AgentConnectCard';
import { ServerAvailabilityBadge } from '@/components/domain/StatusBadges';
import { ServerActions } from '@/components/domain/ServerActions';
import { TableSkeleton } from '@/components/domain/TableSkeleton';
import { EntityLink } from '@/components/domain/EntityLink';
import { ActiveFilterChip } from '@/components/domain/ActiveFilterChip';
import { Badge } from '@/components/ui/badge';
import { useSpace } from '@/hooks/useSpace';
import { useNow } from '@/hooks/useNow';
import { useDisableServer } from '@/hooks/useDisableServer';
import { useEnableServer } from '@/hooks/useEnableServer';
import { useDeleteServer } from '@/hooks/useDeleteServer';
import { useReconnectServer } from '@/hooks/useReconnectServer';
import { useAtChildRoute } from '@/hooks/useAtChildRoute';
import { getIdValue } from '@/lib/api';
import { shortId } from '@/lib/format';
import { rowClickProps } from '@/lib/a11y';
import { computeSkew, deriveServerAvailability } from '@/lib/presence';

type ServersSearch = { node?: string; name?: string };

export const Route = createFileRoute('/servers')({
  // `name` filters by the server's real id (ServersScreen parity — the -3
  // prototype's `filter.name` is an independent, combinable dimension from
  // `node`; not wired to any in-app nav today, kept as a structural entry
  // point future cross-nav can use — same rationale as nodes.tsx's `?name=`).
  validateSearch: (s: Record<string, unknown>): ServersSearch => {
    const out: ServersSearch = {};
    if (typeof s.node === 'string') out.node = s.node;
    if (typeof s.name === 'string') out.name = s.name;
    return out;
  },
  component: ServersPage,
});

const HEADERS = ['Name', 'Publisher runtime', 'Status', ''];

function ServersPage() {
  const atDetail = useAtChildRoute('/servers/$serverId');

  const space = useSpace();
  const disable = useDisableServer();
  const enable = useEnableServer();
  const deleteSrv = useDeleteServer();
  const reconnect = useReconnectServer();

  const { node: nodeFilter, name: nameFilter } = Route.useSearch();
  const navigate = useNavigate({ from: '/servers' });

  // Single shared interval — all rows re-evaluate on the same tick (mirrors nodes.tsx).
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
        message="Could not load servers"
        detail={`GET /api/space — ${space.error.message}`}
        onRetry={() => space.refetch()}
      />
    );
  }

  if (space.data.mcpServers.length === 0) {
    return (
      <>
        <EmptyState
          icon={Server}
          title="No MCP servers"
          hint="Publish one with `korat mcp add <name> --command &quot;...&quot;`."
        />
        {/* Outlet renders the how_to_add child route as a modal overlay when navigated to */}
        <Outlet />
      </>
    );
  }

  const { presenceStaleSeconds } = space.data;

  // Client-side filter by publisher node id and/or server id — both dimensions
  // combine (AND), mirroring ServersScreen's independent `fNode`/`fName` rows.
  let filteredServers = space.data.mcpServers;
  // Increment 1 (HTTP MCP direct-to-Space): an http_cloud row's publisherNodeId is null (no
  // publisher node exists at all) — it can never match a real nodeFilter, so guard rather than
  // let getIdValue(null) throw while scanning every row.
  if (nodeFilter) {
    filteredServers = filteredServers.filter(
      (m) => m.publisherNodeId != null && getIdValue(m.publisherNodeId) === nodeFilter,
    );
  }
  if (nameFilter) filteredServers = filteredServers.filter((m) => getIdValue(m.id) === nameFilter);

  // Resolve chip labels from the full (unfiltered) list so each chip reflects
  // its own dimension even when the other filter has already narrowed rows.
  const nodeChipLabel = nodeFilter
    ? (space.data.mcpServers.find((m) => m.publisherNodeId != null && getIdValue(m.publisherNodeId) === nodeFilter)
      ?.publisherNodeName ?? shortId(nodeFilter))
    : undefined;
  const nameChipLabel = nameFilter
    ? (space.data.mcpServers.find((m) => getIdValue(m.id) === nameFilter)?.displayName ?? shortId(nameFilter))
    : undefined;

  return (
    <div className="flex flex-col gap-4">
      <AgentConnectCard />

      <div className="flex items-center justify-between gap-2">
        <Badge variant="outline" className="font-mono">
          {filteredServers.length} {filteredServers.length === 1 ? 'server' : 'servers'}
        </Badge>
        <div className="flex gap-2">
          {/* Increment 1 (HTTP MCP direct-to-Space, Task 7): a second, node-free entry point
              alongside the existing `korat mcp add` instructions modal — registers a
              cloud-hosted HTTP MCP server directly via the console form (POST /api/mcp-servers,
              no local node involved). */}
          <Button asChild variant="outline">
            <Link to="/servers/new">
              <Plus />
              Add HTTP MCP server
            </Link>
          </Button>
          <Button asChild>
            <Link to="/servers/how_to_add">
              <Plus />
              Add MCP server
            </Link>
          </Button>
        </div>
      </div>

      {nodeFilter && nodeChipLabel !== undefined && (
        <ActiveFilterChip
          label={nodeChipLabel}
          dimension="Runtime"
          onClear={() => navigate({ search: (p) => ({ ...p, node: undefined }) })}
        />
      )}
      {nameFilter && nameChipLabel !== undefined && (
        <ActiveFilterChip
          label={nameChipLabel}
          dimension="Server"
          onClear={() => navigate({ search: (p) => ({ ...p, name: undefined }) })}
        />
      )}

      {filteredServers.length === 0 && (nodeFilter || nameFilter) ? (
        <EmptyState
          icon={Server}
          title="No servers match this filter"
          hint={
            nodeFilter
              ? `No servers published by ${nodeChipLabel} right now.`
              : `No server named ${nameChipLabel}.`
          }
        />
      ) : (
        <Table>
          <TableHeader>
            <TableRow>
              <TableHead className="eyebrow">Name</TableHead>
              <TableHead className="eyebrow">Publisher runtime</TableHead>
              <TableHead className="eyebrow">Status</TableHead>
              <TableHead className="eyebrow text-right" />
            </TableRow>
          </TableHeader>
          <TableBody>
            {filteredServers.map((m) => {
              const serverId = getIdValue(m.id);
              // Increment 1: null for http_cloud (no publisher node exists at all) — guard
              // before unwrapping (Task-6-gate HIGH fix; getIdValue(null) throws).
              const publisherNodeId = m.publisherNodeId ? getIdValue(m.publisherNodeId) : null;
              const availability = deriveServerAvailability(
                m.status,
                // isAsserted defaults true on older server responses (pre-021) so
                // availability collapses to Published && ownerOnline — matches spec.
                m.isAsserted ?? true,
                m.publisherNodeStatus,
                m.publisherNodeLastSeenAt,
                presenceStaleSeconds,
                skewMs,
                nowMs,
                m.transport,
              );
              return (
                <TableRow
                  key={serverId}
                  className="cursor-pointer"
                  // Row → detail (parity w/ ServersScreen), mouse-only. Interactive cells
                  // below stop propagation so their own link/button targets win instead.
                  // Fable review (#186 MEDIUM-1): the <tr> itself no longer carries
                  // role="button"/tabIndex/onKeyDown — the Name cell below already contains
                  // a real <Link> (EntityLink → /grants), which is what gives keyboard and
                  // screen-reader users a genuine way into this row.
                  {...rowClickProps(() => navigate({ to: '/servers/$serverId', params: { serverId } }))}
                >
                  <TableCell onClick={(e) => e.stopPropagation()} onKeyDown={(e) => e.stopPropagation()}>
                    <EntityLink
                      name={m.displayName}
                      rawId={serverId}
                      to="/grants"
                      search={{ server: serverId }}
                      className="font-semibold"
                    />
                  </TableCell>
                  <TableCell
                    className="text-muted-foreground"
                    onClick={(e) => e.stopPropagation()}
                    onKeyDown={(e) => e.stopPropagation()}
                  >
                    {m.transport === 'http_cloud' || publisherNodeId === null ? (
                      // Finding 16, M5 / spec §11 decision 3: http_cloud servers are disclosed as
                      // cloud-terminated, always — no publisher node to link to (there is none).
                      <Badge
                        variant="outline"
                        title="Cloud-terminated: this server has no e2e encryption — the cloud connects to it directly."
                      >
                        Cloud-terminated
                      </Badge>
                    ) : (
                      /* publisherNodeName falls back to 8-char id server-side */
                      <EntityLink
                        name={m.publisherNodeName ?? undefined}
                        rawId={publisherNodeId}
                        to="/nodes/$name"
                        params={{ name: publisherNodeId }}
                      />
                    )}
                  </TableCell>
                  <TableCell>
                    <ServerAvailabilityBadge availability={availability} />
                  </TableCell>
                  <TableCell
                    className="text-right"
                    onClick={(e) => e.stopPropagation()}
                    onKeyDown={(e) => e.stopPropagation()}
                  >
                    <div className="flex justify-end gap-2">
                      <ServerActions
                        serverId={serverId}
                        displayName={m.displayName}
                        availability={availability}
                        disable={disable}
                        enable={enable}
                        deleteSrv={deleteSrv}
                        reconnect={reconnect}
                      />
                    </div>
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
