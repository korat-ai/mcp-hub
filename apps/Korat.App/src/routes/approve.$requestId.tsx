import { createFileRoute, Link } from '@tanstack/react-router';
import { ArrowLeft, CheckCircle2, Search, XCircle } from 'lucide-react';
import { Card } from '@/components/ui/card';
import { Button } from '@/components/ui/button';
import { Badge } from '@/components/ui/badge';
import { Skeleton } from '@/components/ui/skeleton';
import { EmptyState } from '@/components/domain/EmptyState';
import { ErrorState } from '@/components/domain/ErrorState';
import { AccessRequestStatusBadge } from '@/components/domain/StatusBadges';
import { EntityLink } from '@/components/domain/EntityLink';
import { useAccessRequest } from '@/hooks/useAccessRequest';
import { useApproveRequest } from '@/hooks/useApproveRequest';
import { useDenyRequest } from '@/hooks/useDenyRequest';
import { ApiError } from '@/lib/api';
import { cn } from '@/lib/utils';
import { shortId } from '@/lib/format';
import { formatTimestamp, relativeFromNow } from '@/lib/time';
import { useNow } from '@/hooks/useNow';

export const Route = createFileRoute('/approve/$requestId')({
  validateSearch: (): Record<string, never> => ({}),
  component: ApprovePage,
});

function ApprovePage() {
  const { requestId } = Route.useParams();

  const req = useAccessRequest(requestId);
  const approve = useApproveRequest();
  const deny = useDenyRequest();
  const nowMs = useNow(8_000);

  // "← Overview" affordance shown above the request card in every state
  // (loading/error/not-found/resolved/pending), mirroring the -3 prototype's back button.
  // Kept as a local JSX value (not a module-level component) so it doesn't add another
  // react-refresh/only-export-components false-positive to this route file.
  const backLink = (
    <Link
      to="/"
      className="inline-flex items-center gap-1.5 text-xs font-medium text-muted-foreground hover:text-foreground w-fit"
    >
      <ArrowLeft className="size-3.5" aria-hidden="true" />
      Overview
    </Link>
  );

  if (req.isPending) {
    return (
      <div className="max-w-2xl mx-auto space-y-4">
        {backLink}
        <Card className="p-8 space-y-4">
          <Skeleton className="h-8 w-2/3" />
          <Skeleton className="h-4 w-1/2" />
          <Skeleton className="h-32" />
        </Card>
      </div>
    );
  }

  if (req.isError) {
    // 404 (request purged/never existed) gets a distinct "not found" empty state
    // rather than the generic 5xx ErrorState — retrying a 404 can never succeed.
    if (req.error instanceof ApiError && req.error.status === 404) {
      return (
        <div className="max-w-2xl mx-auto space-y-4">
          {backLink}
          <Card className="p-8">
            <EmptyState
              icon={Search}
              title="Request not found"
              hint="This access request id doesn't exist or has been purged."
            />
          </Card>
        </div>
      );
    }
    return (
      <div className="max-w-2xl mx-auto space-y-4">
        {backLink}
        <ErrorState
          message="Could not load request"
          detail={`GET /api/access-requests/${requestId} — ${req.error.message}`}
          onRetry={() => req.refetch()}
        />
      </div>
    );
  }

  const r = req.data;

  if (r.status !== 'Pending') {
    const isApproved = r.status === 'Approved';
    return (
      <div className="max-w-2xl mx-auto space-y-4">
        {backLink}
        <Card className="p-8 flex flex-col items-center gap-3 text-center">
          <span
            className={cn(
              'inline-flex size-11 items-center justify-center rounded-full',
              isApproved ? 'bg-primary/15 text-primary' : 'bg-destructive/15 text-destructive',
            )}
          >
            {isApproved
              ? <CheckCircle2 className="size-6" aria-hidden="true" />
              : <XCircle className="size-6" aria-hidden="true" />}
          </span>
          <h3 className="text-base font-semibold">Request was {r.status.toLowerCase()}</h3>
          <p className="font-mono text-xs text-muted-foreground">
            {r.agentNodeName} → {r.mcpServerName} · {r.id}
          </p>
          <Button variant="outline" asChild className="mt-1.5">
            <Link to="/">Back to overview</Link>
          </Button>
        </Card>
      </div>
    );
  }

  // Note: deliberately excludes req.isFetching — the 5s background poll on a
  // Pending request would otherwise briefly disable Deny/Allow on every tick.
  const busy = approve.isPending || deny.isPending;

  const relTime = relativeFromNow(r.requestedAt, nowMs);
  // "just now" → "requester is waiting now"; otherwise "requester has been waiting N ago"
  const waitingHint = relTime === 'just now'
    ? 'Requester is waiting now.'
    : `Requester has been waiting — ${relTime}.`;

  return (
    <div className="max-w-2xl mx-auto space-y-4">
      {backLink}
      <Card className="p-8 space-y-6">
        <header className="space-y-2">
          {/* Primary heading uses friendly consumer/server names; raw client id is secondary. */}
          <h2 className="text-lg font-semibold">
            <span>{r.agentNodeName}</span>
            {' → '}
            <span>{r.mcpServerName}</span>
          </h2>
          <div className="flex items-center gap-3">
            <AccessRequestStatusBadge status={r.status} />
            {/* #113: live "N ago" hint so the approver sees urgency */}
            <span className="text-xs text-muted-foreground" data-testid="waiting-hint">{waitingHint}</span>
          </div>
        </header>

        <dl className="grid grid-cols-[10rem_1fr] gap-y-2 text-sm">
          <dt className="text-muted-foreground">Consumer</dt>
          <dd>{r.agentNodeName}</dd>
          <dt className="text-muted-foreground">Consumer id</dt>
          {/* #117: agent id links to grants filtered by this agent */}
          <dd className="font-mono text-xs text-muted-foreground">
            <EntityLink
              name={r.consumerId}
              rawId={r.consumerId}
              to="/grants"
              search={{ agent: r.consumerId }}
              className="font-mono text-xs text-muted-foreground"
            />
          </dd>
          <dt className="text-muted-foreground">Server</dt>
          {/* #117: server name links to grants filtered by this server */}
          <dd>
            <EntityLink
              name={r.mcpServerName}
              rawId={r.mcpServerId}
              to="/grants"
              search={{ server: r.mcpServerId }}
            />
          </dd>
          <dt className="text-muted-foreground">Publisher</dt>
          {/* #117: publisher node links to servers published by this node. Increment 1 (HTTP MCP
              direct-to-Space): publisherNodeId/-Name are null for an http_cloud server's request
              — no publisher node exists at all (Finding 16, M5 / spec §11 decision 3: disclosed,
              always). Guard before rendering (Task-6-gate HIGH fix). */}
          <dd>
            {r.publisherNodeId === null ? (
              <Badge
                variant="outline"
                title="Cloud-terminated: this server has no e2e encryption — the cloud connects to it directly."
              >
                Cloud-terminated
              </Badge>
            ) : (
              <EntityLink
                name={r.publisherNodeName ?? undefined}
                rawId={r.publisherNodeId}
                to="/servers"
                search={{ node: r.publisherNodeId }}
              />
            )}
          </dd>
          <dt className="text-muted-foreground">Requested</dt>
          <dd className="font-mono">{formatTimestamp(r.requestedAt)}</dd>
          <dt className="text-muted-foreground">Request id</dt>
          <dd className="font-mono text-xs text-muted-foreground">{r.id}</dd>
        </dl>

        <div className="bg-muted/40 rounded-md p-4 text-sm text-muted-foreground">
          Allowing this request creates a permission. The consumer can call this MCP server
          until you revoke it from{' '}
          <Link to="/grants" className="font-mono underline hover:text-foreground">
            /app/grants
          </Link>.
        </div>

        <footer className="flex justify-end gap-3">
          <Button
            variant="outline"
            disabled={busy}
            onClick={() => deny.mutate({
              requestId: r.id,
              agentLabel: shortId(r.consumerId),
              serverLabel: r.mcpServerName,
            })}
          >
            Deny
          </Button>
          <Button
            aria-label="Allow access"
            disabled={busy}
            onClick={() => approve.mutate({
              requestId: r.id,
              agentLabel: shortId(r.consumerId),
              serverLabel: r.mcpServerName,
            })}
          >
            {approve.isPending ? 'Approving…' : 'Allow access'}
          </Button>
        </footer>
      </Card>
    </div>
  );
}
