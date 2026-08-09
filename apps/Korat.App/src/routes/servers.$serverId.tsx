import { createFileRoute } from '@tanstack/react-router';
import { useEffect, useMemo } from 'react';
import { Server, Shield, Activity } from 'lucide-react';
import { Card } from '@/components/ui/card';
import { Badge } from '@/components/ui/badge';
import { Skeleton } from '@/components/ui/skeleton';
import { EmptyState } from '@/components/domain/EmptyState';
import { ErrorState } from '@/components/domain/ErrorState';
import { DetailBack } from '@/components/domain/DetailBack';
import { DetailRow } from '@/components/domain/DetailRow';
import { MiniSection } from '@/components/domain/MiniSection';
import { MiniRow } from '@/components/domain/MiniRow';
import { EntityLink } from '@/components/domain/EntityLink';
import { ServerActions } from '@/components/domain/ServerActions';
import {
  ServerAvailabilityBadge, GrantStatusBadge, SessionStatusBadge,
} from '@/components/domain/StatusBadges';
import { useSpace } from '@/hooks/useSpace';
import { useGrants } from '@/hooks/useGrants';
import { useSessions } from '@/hooks/useSessions';
import { useNow } from '@/hooks/useNow';
import { useDisableServer } from '@/hooks/useDisableServer';
import { useEnableServer } from '@/hooks/useEnableServer';
import { useDeleteServer } from '@/hooks/useDeleteServer';
import { useReconnectServer } from '@/hooks/useReconnectServer';
import { getIdValue } from '@/lib/api';
import { formatTimestamp, relativeFromNow } from '@/lib/time';
import { shortId } from '@/lib/format';
import { computeSkew, deriveServerAvailability } from '@/lib/presence';
import { toastReceipt } from '@/lib/toast';

export const Route = createFileRoute('/servers/$serverId')({
  component: ServerDetailPage,
  // Increment 2 (HTTP MCP OAuth, Task 6): the oauth callback redirect
  // (McpOAuthCallbackEndpoints.cs) lands the owner back here with
  // ?connected=true|false&reason=... — see the mount-effect toast below.
  // Both keys are OPTIONAL (explicit `?:` return type + omit-when-absent): a normal visit to this
  // route carries no such params, so search must NOT be required on navigation — otherwise every
  // other route linking to /servers/$serverId (servers.tsx, servers.new.tsx, nodes.$name.tsx)
  // fails to type-check for a missing `search` prop.
  validateSearch: (search: Record<string, unknown>): { connected?: boolean; reason?: string } => {
    const connected = search.connected === 'true' ? true : search.connected === 'false' ? false : undefined;
    const reason = typeof search.reason === 'string' ? search.reason : undefined;
    return {
      ...(connected !== undefined ? { connected } : {}),
      ...(reason !== undefined ? { reason } : {}),
    };
  },
});

/** Exported for test harness — allows mounting ServerDetailPage in a minimal router. */
export { ServerDetailPage };

function ServerDetailPage() {
  const { serverId } = Route.useParams();
  const space = useSpace();
  const grants = useGrants();
  const sessions = useSessions();
  const disable = useDisableServer();
  const enable = useEnableServer();
  const deleteSrv = useDeleteServer();
  const reconnect = useReconnectServer();
  const navigate = Route.useNavigate();

  const { connected, reason } = Route.useSearch();
  useEffect(() => {
    if (connected === undefined) return; // normal visit — no oauth-callback params to consume
    if (connected === true) toastReceipt('good', 'server connected', 'OAuth authorization succeeded.');
    else toastReceipt('bad', 'connection failed', reason ?? 'unknown reason');
    // Consume the one-shot callback params: strip them (replace, no history entry) so a hard
    // refresh, a bookmarked/shared ?connected= link, or a StrictMode remount does NOT re-toast —
    // the URL no longer carries the trigger after this first run, making the effect idempotent
    // across mounts (the empty-deps array alone only suppresses re-render, not re-mount).
    navigate({ search: (prev) => ({ ...prev, connected: undefined, reason: undefined }), replace: true });
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  // Single shared interval — mirrors servers.tsx / nodes.tsx.
  const nowMs = useNow(8_000);

  // Compute clock skew once per space fetch, not on every tick.
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
        message="Could not load server"
        detail={`GET /api/space — ${space.error.message}`}
        onRetry={() => space.refetch()}
      />
    );
  }

  const server = space.data.mcpServers.find((m) => getIdValue(m.id) === serverId);

  if (!server) {
    return (
      <div className="flex flex-col gap-4">
        <DetailBack to="/servers" label="MCP servers" />
        <EmptyState
          icon={Server}
          title="Server not found"
          hint="It may have been deleted, or you may not have access to it."
        />
      </div>
    );
  }

  const { presenceStaleSeconds } = space.data;
  // Increment 1 (HTTP MCP direct-to-Space): null for http_cloud (no publisher node exists at
  // all) — guard before unwrapping (Task-6-gate HIGH fix; getIdValue(null) throws).
  const publisherNodeId = server.publisherNodeId ? getIdValue(server.publisherNodeId) : null;
  const availability = deriveServerAvailability(
    server.status,
    server.isAsserted ?? true,
    server.publisherNodeStatus,
    server.publisherNodeLastSeenAt,
    presenceStaleSeconds,
    skewMs,
    nowMs,
    server.transport,
  );
  const serverNowMs = nowMs - skewMs;

  const serverGrants = (grants.data ?? []).filter((g) => getIdValue(g.mcpServerId) === serverId);
  const serverSessions = (sessions.data ?? []).filter((s) => getIdValue(s.mcpServerId) === serverId);

  return (
    <div className="flex flex-col gap-5">
      <DetailBack to="/servers" label="MCP servers" />

      <div className="flex items-center gap-3 flex-wrap">
        <h1 className="text-2xl font-semibold tracking-tight">{server.displayName}</h1>
        <ServerAvailabilityBadge availability={availability} />
        <div className="flex-1" />
        <ServerActions
          serverId={serverId}
          displayName={server.displayName}
          availability={availability}
          disable={disable}
          enable={enable}
          deleteSrv={deleteSrv}
          reconnect={reconnect}
          onDeleted={() => void navigate({ to: '/servers' })}
        />
      </div>

      <Card className="px-4 py-1">
        <DetailRow
          label="Publisher runtime"
          value={(
            server.transport === 'http_cloud' || publisherNodeId === null ? (
              // Finding 16, M5 / spec §11 decision 3: disclosed, always — no publisher node to
              // link to (there is none).
              <Badge
                variant="outline"
                title="Cloud-terminated: this server has no e2e encryption — the cloud connects to it directly."
              >
                Cloud-terminated
              </Badge>
            ) : (
              <EntityLink
                name={server.publisherNodeName ?? undefined}
                rawId={publisherNodeId}
                to="/nodes/$name"
                params={{ name: publisherNodeId }}
              />
            )
          )}
        />
        <DetailRow label="Status" value={server.status} mono />
        <DetailRow label="Last seen" value={relativeFromNow(server.lastSeenAt, serverNowMs)} mono />
      </Card>

      <MiniSection icon={Shield} title="Permissions — who can call this" count={serverGrants.length}>
        <Card className="overflow-hidden p-0">
          {grants.isPending ? (
            <div className="px-4 py-4 text-center text-xs text-muted-foreground">Loading permissions…</div>
          ) : grants.isError ? (
            <div className="px-4 py-4 text-center text-xs text-destructive">Could not load permissions.</div>
          ) : serverGrants.length === 0 ? (
            <div className="px-4 py-4 text-center text-xs text-muted-foreground">
              No consumer has access yet.
            </div>
          ) : (
            serverGrants.map((g) => {
              const agentId = getIdValue(g.consumerId);
              return (
                <MiniRow key={getIdValue(g.id)}>
                  <EntityLink
                    name={g.agentName}
                    rawId={agentId}
                    to="/grants"
                    search={{ agent: agentId }}
                  />
                  <div className="flex-1" />
                  <GrantStatusBadge status={g.status} />
                  <span className="font-mono text-xs text-muted-foreground">
                    {formatTimestamp(g.approvedAt)}
                  </span>
                </MiniRow>
              );
            })
          )}
        </Card>
      </MiniSection>

      <MiniSection icon={Activity} title="Sessions" count={serverSessions.length}>
        <Card className="overflow-hidden p-0">
          {sessions.isPending ? (
            <div className="px-4 py-4 text-center text-xs text-muted-foreground">Loading sessions…</div>
          ) : sessions.isError ? (
            <div className="px-4 py-4 text-center text-xs text-destructive">Could not load sessions.</div>
          ) : serverSessions.length === 0 ? (
            <div className="px-4 py-4 text-center text-xs text-muted-foreground">
              No sessions through this server.
            </div>
          ) : (
            serverSessions.map((s) => {
              const sessionId = getIdValue(s.id);
              const agentId = getIdValue(s.consumerId);
              return (
                <MiniRow key={sessionId}>
                  <code className="font-mono text-xs text-foreground">{shortId(sessionId)}</code>
                  <EntityLink
                    name={s.agentName}
                    rawId={agentId}
                    to={s.agentName ? '/grants' : undefined}
                    search={s.agentName ? { agent: agentId } : undefined}
                  />
                  <div className="flex-1" />
                  <SessionStatusBadge status={s.effectiveStatus} />
                  <span className="font-mono text-xs text-muted-foreground">
                    {formatTimestamp(s.startedAt)}
                  </span>
                </MiniRow>
              );
            })
          )}
        </Card>
      </MiniSection>
    </div>
  );
}
