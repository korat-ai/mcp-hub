import { useState } from 'react';
import { createFileRoute } from '@tanstack/react-router';
import { Ban, Shield } from 'lucide-react';
import {
  Table, TableBody, TableCell, TableHead, TableHeader, TableRow,
} from '@/components/ui/table';
import { Button } from '@/components/ui/button';
import { EmptyState } from '@/components/domain/EmptyState';
import { TableSkeleton } from '@/components/domain/TableSkeleton';
import { ErrorState } from '@/components/domain/ErrorState';
import { GrantStatusBadge } from '@/components/domain/StatusBadges';
import { ConfirmDialog } from '@/components/domain/ConfirmDialog';
import { EntityLink } from '@/components/domain/EntityLink';
import { ActiveFilterChip } from '@/components/domain/ActiveFilterChip';
import { AccessSection } from '@/components/domain/AccessNav';
import { Badge } from '@/components/ui/badge';
import { useGrants } from '@/hooks/useGrants';
import { useRevokeGrant } from '@/hooks/useRevokeGrant';
import { formatTimestamp, relativeFromNow } from '@/lib/time';
import { getIdValue } from '@/lib/api';
import { shortId } from '@/lib/format';
import type { GrantDto, GrantStatus } from '@/types/api';

function canRevoke(status: GrantStatus): boolean {
  switch (status) {
    case 'Active': return true;
    case 'Revoked': return false;
  }
}

type GrantsSearch = { server?: string; agent?: string };

export const Route = createFileRoute('/grants')({
  validateSearch: (s: Record<string, unknown>): GrantsSearch => {
    const out: GrantsSearch = {};
    if (typeof s.server === 'string') out.server = s.server;
    if (typeof s.agent === 'string') out.agent = s.agent;
    return out;
  },
  component: GrantsPage,
});

/** Exported for test harness — allows mounting GrantsPage in a minimal router. */
export { GrantsPage };

function GrantsPage() {
  const grants = useGrants();
  const revoke = useRevokeGrant();
  const [target, setTarget] = useState<GrantDto | null>(null);
  const { server, agent } = Route.useSearch();
  const navigate = Route.useNavigate();

  if (grants.isPending) {
    return (
      <AccessSection>
        <TableSkeleton headers={['Consumer', 'Server', 'Status', 'Granted', '']} />
      </AccessSection>
    );
  }

  if (grants.isError) {
    return (
      <AccessSection>
        <ErrorState
          message="Could not load permissions"
          detail={`GET /api/grants — ${grants.error.message}`}
          onRetry={() => grants.refetch()}
        />
      </AccessSection>
    );
  }

  if (grants.data.length === 0) {
    return (
      <AccessSection>
        <EmptyState
          icon={Shield}
          title="No permissions yet"
          hint="Permissions appear after an owner approves an access request."
        />
      </AccessSection>
    );
  }

  // Client-side filtering over already-fetched list.
  const rows = grants.data.filter(
    (g) =>
      (!server || getIdValue(g.mcpServerId) === server) &&
      (!agent || getIdValue(g.consumerId) === agent),
  );

  // Resolve chip label from first match in full list, fallback to shortId.
  const chipLabel = server
    ? (grants.data.find((g) => getIdValue(g.mcpServerId) === server)?.serverName ?? shortId(server))
    : agent
      ? (grants.data.find((g) => getIdValue(g.consumerId) === agent)?.agentName ?? shortId(agent))
      : '';

  const targetAgentId = target ? getIdValue(target.consumerId) : '';
  const targetServerId = target ? getIdValue(target.mcpServerId) : '';
  const targetAgentName = target?.agentName ?? (targetAgentId ? shortId(targetAgentId) : '');
  const targetServerName = target?.serverName ?? (targetServerId ? shortId(targetServerId) : '');

  return (
    <AccessSection>
      <div className="mb-3 flex items-center justify-between gap-2">
        {(server || agent) ? (
          <ActiveFilterChip
            label={chipLabel}
            dimension={server ? 'Server' : 'Consumer'}
            onClear={() =>
              navigate({ search: () => ({ server: undefined, agent: undefined }) })
            }
          />
        ) : (
          <span />
        )}
        <Badge variant="outline" className="font-mono text-muted-foreground">
          {rows.length}
        </Badge>
      </div>

      {rows.length === 0 ? (
        <EmptyState
          icon={Shield}
          title="No permissions match this filter"
          hint={
            server
              ? `No consumer has access to ${chipLabel} yet.`
              : agent
                ? `No permissions held by ${chipLabel} yet.`
                : 'Clear the filter to see all permissions.'
          }
        />
      ) : (
        <Table>
          <TableHeader>
            <TableRow>
              <TableHead className="eyebrow">Consumer</TableHead>
              <TableHead className="eyebrow">Server</TableHead>
              <TableHead className="eyebrow">Status</TableHead>
              <TableHead className="eyebrow text-right">Granted</TableHead>
              <TableHead className="eyebrow text-right"> </TableHead>
            </TableRow>
          </TableHeader>
          <TableBody>
            {rows.map((g) => {
              const grantId = getIdValue(g.id);
              const agentId = getIdValue(g.consumerId);
              const serverId = getIdValue(g.mcpServerId);
              return (
                <TableRow key={grantId}>
                  <TableCell>
                    <EntityLink
                      name={g.agentName}
                      rawId={agentId}
                      to={g.agentName ? '/grants' : undefined}
                      search={g.agentName ? { agent: agentId } : undefined}
                    />
                  </TableCell>
                  <TableCell>
                    <EntityLink
                      name={g.serverName}
                      rawId={serverId}
                      to={g.serverName ? '/servers/$serverId' : undefined}
                      params={g.serverName ? { serverId } : undefined}
                    />
                  </TableCell>
                  <TableCell><GrantStatusBadge status={g.status} /></TableCell>
                  <TableCell
                    className="text-right font-mono text-muted-foreground"
                    title={formatTimestamp(g.approvedAt)}
                  >
                    {relativeFromNow(g.approvedAt)}
                  </TableCell>
                  <TableCell className="text-right">
                    {canRevoke(g.status) ? (
                      <Button
                        variant="destructive"
                        size="xs"
                        disabled={revoke.isPending && revoke.variables?.grantId === grantId}
                        onClick={() => setTarget(g)}
                      >
                        <Ban />
                        {revoke.isPending && revoke.variables?.grantId === grantId ? '…' : 'Revoke'}
                      </Button>
                    ) : (
                      <span className="text-muted-foreground">—</span>
                    )}
                  </TableCell>
                </TableRow>
              );
            })}
          </TableBody>
        </Table>
      )}

      <ConfirmDialog
        open={target !== null}
        onOpenChange={(open) => { if (!open) setTarget(null); }}
        title={target ? `Revoke ${targetAgentName}'s access to ${targetServerName}?` : ''}
        description="This blocks new sessions and immediately terminates every active session using this permission."
        confirmLabel="Revoke"
        destructive
        isPending={revoke.isPending}
        onConfirm={() => {
          if (!target) return;
          const grantId = getIdValue(target.id);
          revoke.mutate(
            { grantId, agentName: targetAgentName, serverName: targetServerName },
            { onSuccess: () => setTarget(null) },
          );
        }}
      />
    </AccessSection>
  );
}
