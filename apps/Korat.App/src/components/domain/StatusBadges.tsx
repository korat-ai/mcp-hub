// Labels are wire-format strings (server enum names). When i18n lands, swap
// each wrapper to read from a translation map keyed by the union literal.
import { Badge } from '@/components/ui/badge';
import {
  Tooltip,
  TooltipContent,
  TooltipProvider,
  TooltipTrigger,
} from '@/components/ui/tooltip';
import { cn } from '@/lib/utils';
import type {
  NodeStatus,
  NodeKind,
  McpServerStatus,
  GrantStatus,
  SessionEffectiveStatus,
  AccessRequestStatus,
} from '@/types/api';
import type { ServerAvailability } from '@/lib/presence';

export type Tone = 'good' | 'warn' | 'bad' | 'idle';

const TONE_CLASSES: Record<Tone, string> = {
  good: 'bg-primary/15 text-primary border-transparent',
  warn: 'bg-transparent text-primary border-primary/45',
  bad:  'bg-destructive/15 text-destructive border-transparent',
  idle: 'bg-muted text-muted-foreground border-transparent',
};

const DOT_CLASSES: Record<Tone, string> = {
  good: 'bg-primary',
  warn: 'bg-primary',
  bad:  'bg-destructive',
  idle: 'bg-muted-foreground/60',
};

interface Props {
  tone: Tone;
  label: string;
  tooltip?: string;
  /** Adds a subtle pulsing ring around the status dot (e.g. inference 'live'). */
  pulse?: boolean;
}

export function StatusBadge({ tone, label, tooltip, pulse }: Props) {
  const badge = (
    <Badge variant="outline" className={cn('gap-1.5 font-mono lowercase', TONE_CLASSES[tone])}>
      <span className="relative inline-flex size-1.5" aria-hidden="true">
        {pulse && (
          <span
            className={cn(
              'absolute inline-flex size-full animate-ping rounded-full opacity-75',
              DOT_CLASSES[tone],
            )}
          />
        )}
        <span className={cn('relative inline-flex size-1.5 rounded-full', DOT_CLASSES[tone])} />
      </span>
      {label}
    </Badge>
  );

  if (!tooltip) return badge;

  return (
    <TooltipProvider>
      <Tooltip>
        <TooltipTrigger asChild>{badge}</TooltipTrigger>
        <TooltipContent side="top">{tooltip}</TooltipContent>
      </Tooltip>
    </TooltipProvider>
  );
}

// NodeStatus: 'Online' | 'Offline' — per api.ts reconciliation note, Stale is NOT present.
const NODE_TONE: Record<NodeStatus, Tone> = {
  Online: 'good',
  Offline: 'idle',
};

// McpServerStatus: 'Published' | 'Disabled' | 'Unavailable' | 'NeedsReauth'
const SERVER_TONE: Record<McpServerStatus, Tone> = {
  Published:   'good',
  Disabled:    'idle',
  Unavailable: 'warn',
  NeedsReauth: 'warn',
};

// GrantStatus: 'Active' | 'Revoked'
const GRANT_TONE: Record<GrantStatus, Tone> = {
  Active:  'good',
  Revoked: 'bad',
};

// SessionEffectiveStatus: SessionStatus + derived 'Stale' (Active/Opening but a participant offline)
const SESSION_TONE: Record<SessionEffectiveStatus, Tone> = {
  Opening: 'warn',
  Active:  'good',
  Closing: 'warn',
  Closed:  'idle',
  Failed:  'bad',
  Denied:  'bad',
  Stale:   'warn',
};

// AccessRequestStatus: 'Pending' | 'Approved' | 'Denied' | 'Expired' | 'Canceled'
const ACCESS_TONE: Record<AccessRequestStatus, Tone> = {
  Pending:  'warn',
  Approved: 'good',
  Denied:   'bad',
  Expired:  'bad',
  Canceled: 'idle',
};

const NODE_TOOLTIP: Record<NodeStatus, string> = {
  Online:  'Runtime is connected and heartbeating — ready to publish MCP servers.',
  Offline: 'Runtime is disconnected. Run `korat service start` on its host to bring it back online.',
};

export function NodeStatusBadge({ status }: { status: NodeStatus }) {
  return <StatusBadge tone={NODE_TONE[status]} label={status} tooltip={NODE_TOOLTIP[status]} />;
}

// NodeKind: 'publisher' | 'agent' (lowercase wire format from server)
const KIND_LABEL: Record<NodeKind, string> = {
  publisher: 'Publisher',
  agent:     'Agent',
};

const KIND_TONE: Record<NodeKind, Tone> = {
  publisher: 'idle',
  agent:     'warn',
};

export function NodeKindBadge({ kind }: { kind?: NodeKind }) {
  const resolved = kind ?? 'publisher';
  return <StatusBadge tone={KIND_TONE[resolved]} label={KIND_LABEL[resolved]} />;
}

const SERVER_STATUS_TOOLTIP: Record<McpServerStatus, string> = {
  Published:   'Server is registered (✅ available while its publisher runtime is online).',
  Disabled:    'Server is administratively disabled (⛔ disabled). Re-enable it from the MCP servers list.',
  Unavailable: 'Server is registered but its publisher runtime is offline (💤 offline). Run `korat service start` on the host to restore it.',
  NeedsReauth: 'Server needs OAuth reauthorization before consumers can use it.',
};

export function McpServerStatusBadge({ status }: { status: McpServerStatus }) {
  return <StatusBadge tone={SERVER_TONE[status]} label={status} tooltip={SERVER_STATUS_TOOLTIP[status]} />;
}

// ServerAvailability tri-state (spec 021): Available / Unavailable / Disabled.
// Disabled = owner intent (idle), Unavailable = presence-derived (warn), Available = good.
const AVAILABILITY_TONE: Record<ServerAvailability, Tone> = {
  Available:   'good',
  Unavailable: 'warn',
  Disabled:    'idle',
  NeedsReauth: 'warn',
};

const AVAILABILITY_LABEL: Record<ServerAvailability, string> = {
  Available:   'Available',
  Unavailable: 'Unavailable',
  Disabled:    'Disabled',
  NeedsReauth: 'Needs reauthorization',
};

// CLI glyph mapping: ✅ available / ⏸ declared, service stopped / 💤 offline / ⛔ disabled / — not published
const AVAILABILITY_TOOLTIP: Record<ServerAvailability, string> = {
  Available:   '✅ Available — the publisher runtime is online and actively declaring this server.',
  Unavailable: '⏸ Unavailable — the publisher runtime is offline or has stopped declaring this server. Run `korat service start` on its host to restore it.',
  Disabled:    '⛔ Disabled — this server has been administratively disabled. Enable it from this page to make it available again.',
  NeedsReauth: '🔒 Needs reauthorization — this OAuth server\'s consent was never finished, or its token can no longer be refreshed. Use Reconnect to authorize it again.',
};

export function ServerAvailabilityBadge({ availability }: { availability: ServerAvailability }) {
  return (
    <StatusBadge
      tone={AVAILABILITY_TONE[availability]}
      label={AVAILABILITY_LABEL[availability]}
      tooltip={AVAILABILITY_TOOLTIP[availability]}
    />
  );
}

const GRANT_TOOLTIP: Record<GrantStatus, string> = {
  Active:  'Permission is active — this consumer may connect to the MCP server.',
  Revoked: 'Permission is revoked — this consumer can no longer connect. Approve a new access request to restore it.',
};

export function GrantStatusBadge({ status }: { status: GrantStatus }) {
  return <StatusBadge tone={GRANT_TONE[status]} label={status} tooltip={GRANT_TOOLTIP[status]} />;
}

const SESSION_TOOLTIP: Record<SessionEffectiveStatus, string> = {
  Opening: 'Session is being established between the consumer and the MCP server.',
  Active:  'Session is active — the consumer and MCP server are exchanging messages.',
  Closing: 'Session is shutting down gracefully.',
  Closed:  'Session has ended normally.',
  Failed:  'Session failed to open or terminated unexpectedly. Check the runtime and server status.',
  Denied:  'Session was denied — the consumer did not have an active permission for this server.',
  Stale:   'Session appears stale — one of the participants went offline. It will be cleaned up automatically.',
};

export function SessionStatusBadge({ status }: { status: SessionEffectiveStatus }) {
  return <StatusBadge tone={SESSION_TONE[status]} label={status} tooltip={SESSION_TOOLTIP[status]} />;
}

const ACCESS_TOOLTIP: Record<AccessRequestStatus, string> = {
  Pending:  'Pending — waiting for a space owner to approve or deny this request.',
  Approved: 'Approved — an active permission exists and the consumer can connect.',
  Denied:   'Denied — the Space owner rejected this request. The consumer must request access again.',
  Expired:  'Expired — the request was not acted on in time. The consumer must request access again.',
  Canceled: 'Canceled — the requesting consumer withdrew this access request.',
};

export function AccessRequestStatusBadge({ status }: { status: AccessRequestStatus }) {
  return <StatusBadge tone={ACCESS_TONE[status]} label={status} tooltip={ACCESS_TOOLTIP[status]} />;
}

