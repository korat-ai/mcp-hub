import { useState } from 'react';
import { createFileRoute } from '@tanstack/react-router';
import { Plug } from 'lucide-react';
import {
  Table, TableBody, TableCell, TableHead, TableHeader, TableRow,
} from '@/components/ui/table';
import { Button } from '@/components/ui/button';
import { EmptyState } from '@/components/domain/EmptyState';
import { TableSkeleton } from '@/components/domain/TableSkeleton';
import { ErrorState } from '@/components/domain/ErrorState';
import { ConfirmDialog } from '@/components/domain/ConfirmDialog';
import { AccessSection } from '@/components/domain/AccessNav';
import { useOAuthConsents } from '@/hooks/useOAuthConsents';
import { useRevokeOAuthConsent } from '@/hooks/useRevokeOAuthConsent';
import { formatTimestamp } from '@/lib/time';
import { shortId } from '@/lib/format';
import type { OAuthConsentDto } from '@/types/api';

export const Route = createFileRoute('/connected-apps')({
  component: ConnectedAppsPage,
});

/** Exported for test harness — allows mounting in a minimal router. */
export { ConnectedAppsPage };

function ConnectedAppsPage() {
  const consents = useOAuthConsents();
  const revoke = useRevokeOAuthConsent();
  const [target, setTarget] = useState<OAuthConsentDto | null>(null);

  if (consents.isPending) {
    return (
      <AccessSection>
        <TableSkeleton headers={['Client', 'Space', 'Connected', '']} />
      </AccessSection>
    );
  }

  if (consents.isError) {
    return (
      <AccessSection>
        <ErrorState
          message="Could not load connected apps"
          detail={`GET /api/oauth/consents — ${consents.error.message}`}
          onRetry={() => consents.refetch()}
        />
      </AccessSection>
    );
  }

  if (consents.data.length === 0) {
    return (
      <AccessSection>
        <EmptyState
          icon={Plug}
          title="No connected apps"
          hint="MCP clients you authorize via OAuth appear here; revoking one immediately cuts its access and closes its sessions."
        />
      </AccessSection>
    );
  }

  const targetLabel = target?.clientDisplayName ?? target?.clientId ?? '';

  return (
    <AccessSection>
      <Table>
        <TableHeader>
          <TableRow>
            <TableHead className="eyebrow">Client</TableHead>
            <TableHead className="eyebrow">Space</TableHead>
            <TableHead className="eyebrow text-right">Connected</TableHead>
            <TableHead className="eyebrow text-right"> </TableHead>
          </TableRow>
        </TableHeader>
        <TableBody>
          {consents.data.map((c) => (
            <TableRow key={c.id}>
              <TableCell>{c.clientDisplayName ?? c.clientId ?? shortId(c.id)}</TableCell>
              <TableCell>{c.spaceName || shortId(c.spaceId)}</TableCell>
              <TableCell
                className="text-right font-mono text-muted-foreground"
                title={formatTimestamp(c.createdAt)}
              >
                {formatTimestamp(c.createdAt)}
              </TableCell>
              <TableCell className="text-right">
                <Button
                  variant="destructive"
                  size="sm"
                  disabled={revoke.isPending && revoke.variables?.consentId === c.id}
                  onClick={() => setTarget(c)}
                >
                  {revoke.isPending && revoke.variables?.consentId === c.id ? '…' : 'Revoke'}
                </Button>
              </TableCell>
            </TableRow>
          ))}
        </TableBody>
      </Table>

      <ConfirmDialog
        open={target !== null}
        onOpenChange={(open) => { if (!open) setTarget(null); }}
        title="Revoke access?"
        description={`${targetLabel} will immediately lose access to this Space's MCP tools, and its open sessions will be closed.`}
        confirmLabel="Revoke access"
        destructive
        isPending={revoke.isPending}
        onConfirm={() => {
          if (!target) return;
          revoke.mutate(
            { consentId: target.id, clientName: targetLabel || target.id },
            { onSuccess: () => setTarget(null) },
          );
        }}
      />
    </AccessSection>
  );
}
