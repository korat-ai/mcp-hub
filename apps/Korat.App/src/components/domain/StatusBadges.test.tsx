import { describe, it, expect, vi } from 'vitest';
import { render, screen, within } from '@testing-library/react';
import {
  StatusBadge,
  ServerAvailabilityBadge,
  AccessRequestStatusBadge,
  NodeStatusBadge,
  McpServerStatusBadge,
  GrantStatusBadge,
  SessionStatusBadge,
} from './StatusBadges';

// ---------------------------------------------------------------------------
// Stub Radix Tooltip primitives so tests can assert tooltip content without
// needing pointer events or portal rendering in jsdom.
// ---------------------------------------------------------------------------
vi.mock('@/components/ui/tooltip', () => ({
  TooltipProvider: ({ children }: { children: React.ReactNode }) => <>{children}</>,
  Tooltip: ({ children }: { children: React.ReactNode }) => <>{children}</>,
  TooltipTrigger: ({
    children,
  }: {
    children: React.ReactNode;
    asChild?: boolean;
  }) => <span data-testid="tooltip-trigger">{children}</span>,
  TooltipContent: ({ children }: { children: React.ReactNode }) => (
    <span data-testid="tooltip-content">{children}</span>
  ),
}));

// ---------------------------------------------------------------------------
// StatusBadge (base)
// ---------------------------------------------------------------------------

describe('StatusBadge', () => {
  it('renders the label', () => {
    render(<StatusBadge tone="good" label="available" />);
    expect(screen.getByText('available')).toBeInTheDocument();
  });

  it('does not render a tooltip when no tooltip prop is given', () => {
    render(<StatusBadge tone="idle" label="disabled" />);
    expect(screen.queryByTestId('tooltip-content')).toBeNull();
  });

  it('renders tooltip content when tooltip prop is provided', () => {
    render(<StatusBadge tone="warn" label="unavailable" tooltip="Run korat service start" />);
    expect(screen.getByTestId('tooltip-content')).toHaveTextContent(
      'Run korat service start',
    );
  });

  it('wraps badge in TooltipTrigger when tooltip prop is provided', () => {
    render(<StatusBadge tone="good" label="ok" tooltip="All good" />);
    expect(screen.getByTestId('tooltip-trigger')).toBeInTheDocument();
  });
});

// ---------------------------------------------------------------------------
// ServerAvailabilityBadge — explicitly requested in #107
// ---------------------------------------------------------------------------

describe('ServerAvailabilityBadge', () => {
  it.each([
    ['Available',   /✅ Available/],
    ['Unavailable', /⏸ Unavailable/],
    ['Disabled',    /⛔ Disabled/],
  ] as const)('%s renders label and tooltip', (availability, tipPattern) => {
    render(<ServerAvailabilityBadge availability={availability} />);
    // CSS `lowercase` is visual-only; DOM text node retains original casing.
    expect(screen.getByText(availability)).toBeInTheDocument();
    expect(screen.getByTestId('tooltip-content')).toHaveTextContent(tipPattern);
  });

  it('Available tooltip mentions publisher runtime being online', () => {
    render(<ServerAvailabilityBadge availability="Available" />);
    expect(screen.getByTestId('tooltip-content')).toHaveTextContent(/publisher runtime is online/i);
  });

  it('Unavailable tooltip mentions korat service start', () => {
    render(<ServerAvailabilityBadge availability="Unavailable" />);
    expect(screen.getByTestId('tooltip-content')).toHaveTextContent(/korat service start/i);
  });

  it('Disabled tooltip mentions administratively disabled', () => {
    render(<ServerAvailabilityBadge availability="Disabled" />);
    expect(screen.getByTestId('tooltip-content')).toHaveTextContent(/administratively disabled/i);
  });

  it('renders the NeedsReauth badge', () => {
    render(<ServerAvailabilityBadge availability="NeedsReauth" />);
    // Scoped to the trigger (badge label), not getByText globally — the tooltip-content span
    // (rendered as a sibling, not nested) ALSO contains "reauthorization", so an unscoped
    // getByText(/needs reauth|reauthorization/i) ambiguously matches both nodes.
    expect(within(screen.getByTestId('tooltip-trigger')).getByText(/needs reauth|reauthorization/i)).toBeInTheDocument();
  });
});

// ---------------------------------------------------------------------------
// AccessRequestStatusBadge — explicitly requested in #107
// ---------------------------------------------------------------------------

describe('AccessRequestStatusBadge', () => {
  it.each([
    ['Pending',  /waiting for a space owner/i],
    ['Approved', /active permission exists/i],
    ['Denied',   /space owner rejected/i],
    ['Expired',  /not acted on in time/i],
    ['Canceled', /withdrew this access request/i],
  ] as const)('%s renders label and tooltip', (status, tipPattern) => {
    render(<AccessRequestStatusBadge status={status} />);
    // CSS `lowercase` is visual-only; DOM text node retains original casing.
    expect(screen.getByText(status)).toBeInTheDocument();
    expect(screen.getByTestId('tooltip-content')).toHaveTextContent(tipPattern);
  });
});

// ---------------------------------------------------------------------------
// NodeStatusBadge
// ---------------------------------------------------------------------------

describe('NodeStatusBadge', () => {
  it('Online tooltip mentions heartbeating', () => {
    render(<NodeStatusBadge status="Online" />);
    expect(screen.getByTestId('tooltip-content')).toHaveTextContent(/heartbeating/i);
  });

  it('Offline tooltip mentions korat service start', () => {
    render(<NodeStatusBadge status="Offline" />);
    expect(screen.getByTestId('tooltip-content')).toHaveTextContent(/korat service start/i);
  });
});

// ---------------------------------------------------------------------------
// McpServerStatusBadge
// ---------------------------------------------------------------------------

describe('McpServerStatusBadge', () => {
  it('Published tooltip mentions publisher runtime availability', () => {
    render(<McpServerStatusBadge status="Published" />);
    expect(screen.getByTestId('tooltip-content')).toHaveTextContent(/publisher runtime is online/i);
  });

  it('Disabled tooltip mentions administratively disabled', () => {
    render(<McpServerStatusBadge status="Disabled" />);
    expect(screen.getByTestId('tooltip-content')).toHaveTextContent(/administratively disabled/i);
  });

  it('Unavailable tooltip mentions korat service start', () => {
    render(<McpServerStatusBadge status="Unavailable" />);
    expect(screen.getByTestId('tooltip-content')).toHaveTextContent(/korat service start/i);
  });

  it('NeedsReauth tooltip explains that OAuth must be authorized again', () => {
    render(<McpServerStatusBadge status="NeedsReauth" />);
    expect(screen.getByTestId('tooltip-content')).toHaveTextContent(/OAuth reauthorization/i);
  });
});

// ---------------------------------------------------------------------------
// GrantStatusBadge
// ---------------------------------------------------------------------------

describe('GrantStatusBadge', () => {
  it('Active tooltip mentions permission to connect', () => {
    render(<GrantStatusBadge status="Active" />);
    expect(screen.getByTestId('tooltip-content')).toHaveTextContent(/consumer may connect/i);
  });

  it('Revoked tooltip mentions new access request', () => {
    render(<GrantStatusBadge status="Revoked" />);
    expect(screen.getByTestId('tooltip-content')).toHaveTextContent(/Approve a new access request/i);
  });
});

// ---------------------------------------------------------------------------
// SessionStatusBadge
// ---------------------------------------------------------------------------

describe('SessionStatusBadge', () => {
  it.each([
    ['Opening', /being established/i],
    ['Active',  /exchanging messages/i],
    ['Closing', /shutting down/i],
    ['Closed',  /ended normally/i],
    ['Failed',  /terminated unexpectedly/i],
    ['Denied',  /did not have an active permission/i],
    ['Stale',   /cleaned up automatically/i],
  ] as const)('%s has the correct tooltip', (status, tipPattern) => {
    render(<SessionStatusBadge status={status} />);
    expect(screen.getByTestId('tooltip-content')).toHaveTextContent(tipPattern);
  });
});

// ---------------------------------------------------------------------------
// InferenceStatusBadge
// ---------------------------------------------------------------------------

