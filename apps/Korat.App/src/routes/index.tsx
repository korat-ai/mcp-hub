import { createFileRoute, Link } from '@tanstack/react-router';
import { useMemo } from 'react';
import { Inbox, ChevronRight, ChevronDown } from 'lucide-react';
import { Card } from '@/components/ui/card';
import { Skeleton } from '@/components/ui/skeleton';
import { EmptyState } from '@/components/domain/EmptyState';
import { ErrorState } from '@/components/domain/ErrorState';
import { AccessRequestCard } from '@/components/domain/AccessRequestCard';
import { OnboardingEmptyState } from '@/components/domain/OnboardingEmptyState';
import { useSpace } from '@/hooks/useSpace';
import { useNow } from '@/hooks/useNow';
import { useGrants } from '@/hooks/useGrants';
import { useSessions } from '@/hooks/useSessions';
import { useApproveRequest } from '@/hooks/useApproveRequest';
import { useDenyRequest } from '@/hooks/useDenyRequest';
import { getIdValue } from '@/lib/api';
import { isOpenSession, shortId } from '@/lib/format';
import { computeSkew, deriveServerAvailability, isNodeOnline } from '@/lib/presence';
import { relativeFromNow } from '@/lib/time';

export const Route = createFileRoute('/')({
  component: Overview,
});

function StatCard({
  label, value, sub, accent, to, onClick, scrollIcon,
}: {
  label: string;
  value: string | number;
  /** Contextual sub-label under the value, e.g. "of 3", "across consumers", "awaiting you". */
  sub?: string | number;
  accent?: boolean;
  /** TanStack route to navigate to on click. */
  to?: '/nodes' | '/servers' | '/grants' | '/sessions';
  /** Same-page action (e.g. scroll to the pending section) when there's no dedicated page. */
  onClick?: () => void;
  /** When true, shows a chevron-down instead of chevron-right (scroll affordance vs navigate). */
  scrollIcon?: boolean;
}) {
  const interactive = to !== undefined || onClick !== undefined;
  const cls =
    'p-4 relative ' +
    (accent ? 'border-primary/50 ' : '') +
    (interactive ? 'transition-colors hover:border-primary/50 hover:bg-muted/40' : '');
  const body = (
    <Card className={cls}>
      {/* group-hover:underline only fires under an interactive `group` ancestor (Link/button),
          so the LABEL underlines on hover while the value never does. */}
      <div className="text-xs font-mono uppercase tracking-wide text-muted-foreground group-hover:underline">
        {label}
      </div>
      <div className="flex items-baseline gap-1.5 mt-1">
        <span className={'text-2xl font-semibold tabular-nums ' + (accent ? 'text-primary' : '')}>
          {value}
        </span>
        {sub !== undefined && (
          <span className="font-mono text-[11px] text-muted-foreground">{sub}</span>
        )}
      </div>
      {/* #112: persistent navigate/scroll affordance icon in the top-right corner */}
      {interactive && (
        <span className="absolute top-2 right-2 text-muted-foreground/50 group-hover:text-muted-foreground transition-colors" aria-hidden="true">
          {scrollIcon
            ? <ChevronDown className="size-3.5" />
            : <ChevronRight className="size-3.5" />}
        </span>
      )}
    </Card>
  );

  // `group` lets the label react to card hover; `no-underline` kills the global a:hover underline
  // on the whole card so only the label (group-hover:underline) is underlined, not the value.
  const ring = 'group block rounded-xl no-underline focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring';
  if (to) return <Link to={to} className={ring}>{body}</Link>;
  if (onClick) return <button type="button" onClick={onClick} className={ring + ' w-full text-left'}>{body}</button>;
  return body;
}

function statValue<TData>(
  query: { isPending: boolean; isError: boolean; data: TData | undefined },
  render: (data: TData) => string | number,
): string | number {
  if (query.isPending) return '—';
  if (query.isError) return '!';
  return query.data === undefined ? '—' : render(query.data);
}

function Overview() {
  const space = useSpace();
  const nowMs = useNow(8_000);
  const grants = useGrants();
  const sessions = useSessions();
  const approve = useApproveRequest();
  const deny = useDenyRequest();

  // Skew is stable between fetches — derive once per serverTime change.
  const skewMs = useMemo(
    () => computeSkew(space.data?.serverTime),
     
    [space.data?.serverTime],
  );

  if (space.isPending) {
    return (
      <div className="grid grid-cols-2 min-[720px]:grid-cols-5 gap-4">
        {Array.from({ length: 5 }).map((_, i) => (
          <Card key={i} className={`p-4${i === 4 ? ' col-span-2 min-[720px]:col-span-1' : ''}`}>
            <Skeleton className="h-8" />
          </Card>
        ))}
      </div>
    );
  }

  if (space.isError) {
    return (
      <ErrorState
        message="Could not load space"
        detail={`GET /api/space — ${space.error.message}`}
        onRetry={() => space.refetch()}
      />
    );
  }

  const s = space.data;
  const runtimes = s.nodes.filter((n) => n.kind !== 'agent');

  // Brand-new Space: no publisher runtimes, no servers, nothing pending. Synthetic consumer
  // identities do not suppress onboarding.
  // step (install → login → up) instead of a premature "No pending requests" empty state.
  if (runtimes.length === 0 && s.mcpServers.length === 0 && s.pendingAccessRequests.length === 0) {
    return <OnboardingEmptyState cloudOrigin={window.location.origin} />;
  }

  const onlineRuntimes = runtimes.filter((n) =>
    isNodeOnline(n.status, n.lastSeenAt, s.presenceStaleSeconds, skewMs, nowMs),
  ).length;
  const availableServers = s.mcpServers.filter((server) =>
    deriveServerAvailability(
      server.status,
      server.isAsserted,
      server.publisherNodeStatus,
      server.publisherNodeLastSeenAt,
      s.presenceStaleSeconds,
      skewMs,
      nowMs,
      server.transport,
    ) === 'Available',
  ).length;
  const pending = s.pendingAccessRequests;

  // #113: find the oldest pending request to show a "waiting" hint on the pending card.
  const oldestPendingMs = pending.length > 0
    ? Math.min(...pending.map((r) => new Date(r.requestedAt).getTime()))
    : null;

  const scrollToPending = () =>
    document.getElementById('pending-requests')?.scrollIntoView({ behavior: 'smooth', block: 'start' });

  const statsGrid = (
    <div className="grid grid-cols-2 min-[720px]:grid-cols-5 gap-4">
      <StatCard label="Runtimes online" value={onlineRuntimes} sub={`of ${runtimes.length}`} to="/nodes" />
      <StatCard label="MCP servers" value={availableServers} sub={`${availableServers} available`} to="/servers" />
      <StatCard
        label="Active permissions"
        value={statValue(grants, (data) => data.filter((g) => g.status === 'Active').length)}
        sub="across consumers"
        to="/grants"
      />
      <StatCard
        label="Open sessions"
        value={statValue(sessions, (data) => data.filter((s) => isOpenSession(s.effectiveStatus)).length)}
        sub={statValue(sessions, (data) => `${data.length} total`)}
        to="/sessions"
      />
      {/* Pending card is full-width on mobile (col-span-2) to match mockup amber-accented row */}
      <div className="col-span-2 min-[720px]:col-span-1">
        <StatCard
          label="Pending requests"
          value={pending.length}
          sub="awaiting you"
          accent={pending.length > 0}
          scrollIcon
          onClick={scrollToPending}
        />
      </div>
    </div>
  );

  const pendingSection = (
    /* #112: target: ring highlights the section when the URL fragment points here */
    <section id="pending-requests" className="scroll-mt-6 target:outline target:outline-2 target:outline-primary/30 target:outline-offset-4 target:rounded-lg transition-all">
      <h2 className="text-sm font-mono uppercase tracking-wide text-muted-foreground mb-3 flex items-center gap-2">
        Pending access requests
        <span className="inline-flex items-center justify-center rounded-full bg-muted px-1.5 py-0.5 text-[10px] normal-case tracking-normal text-foreground">
          {pending.length}
        </span>
      </h2>
      {/* #113: show live "oldest waiting N ago" hint when there are pending requests */}
      {pending.length > 0 && oldestPendingMs !== null && (
        <p className="text-xs text-muted-foreground mb-3" data-testid="pending-waiting-hint">
          {(() => {
            const oldestIso = new Date(oldestPendingMs).toISOString();
            const rel = relativeFromNow(oldestIso, nowMs);
            return rel === 'just now'
              ? 'Requester is waiting now — please review.'
              : `Waiting since ${rel} — please review.`;
          })()}
        </p>
      )}
      {pending.length === 0 ? (
        <EmptyState
          icon={Inbox}
          title="No pending access requests"
          hint="New requests appear here when a consumer tries a server it does not have permission to use."
        />
      ) : (
        <div className="flex flex-col gap-2">
          {pending.map((req) => {
            const requestId = getIdValue(req.id);
            const agentId = getIdValue(req.consumerId);
            const agentLabel = req.consumerDisplayName ?? shortId(agentId);
            const serverLabel = req.mcpServerDisplayName || getIdValue(req.mcpServerId);
            return (
              <AccessRequestCard
                key={requestId}
                request={req}
                nowMs={nowMs}
                onApprove={() =>
                  approve.mutate({ requestId, agentLabel, serverLabel })
                }
                onDeny={() =>
                  deny.mutate({ requestId, agentLabel, serverLabel })
                }
                approvePending={approve.isPending && approve.variables?.requestId === requestId}
                denyPending={deny.isPending && deny.variables?.requestId === requestId}
              />
            );
          })}
        </div>
      )}
    </section>
  );

  return (
    <div className="flex flex-col gap-8">
      {/*
        Desktop: 5-column single row.
        Mobile: 2-column grid; Pending spans full width.
        Tailwind v4 arbitrary breakpoint: min-[720px]:grid-cols-5.

        #108: when there are pending requests, surface the pending section ABOVE the stats
        so the approver sees actionable items first without scrolling.
      */}
      {pending.length > 0 ? (
        <>
          {pendingSection}
          {statsGrid}
        </>
      ) : (
        <>
          {statsGrid}
          {pendingSection}
        </>
      )}
    </div>
  );
}
