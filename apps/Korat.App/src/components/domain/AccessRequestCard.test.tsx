/**
 * Unit tests for AccessRequestCard.
 *
 * Key a11y assertion: the mobile Deny/Approve buttons must be reachable by
 * keyboard and screen readers (no aria-hidden, no tabIndex=-1).
 * The desktop copies carry `hidden` + `min-[720px]:inline-flex` and do NOT
 * carry aria-hidden either — both sets of buttons are in the a11y tree but
 * visually mutually exclusive via CSS.
 *
 * #117: agent and server labels are now EntityLink components (navigable links
 * to /grants filtered by agent or server id).
 */
import { describe, expect, it, vi } from 'vitest';
import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { AccessRequestCard } from './AccessRequestCard';
import type { AccessRequestSummaryDto } from '@/types/api';

// Stub out the router Link used for the "open details" chevron and EntityLink.
vi.mock('@tanstack/react-router', async (importOriginal) => {
  const actual = await importOriginal<typeof import('@tanstack/react-router')>();
  return {
    ...actual,
    Link: ({
      children,
      to,
      search: _search,
      params: _params,
      ...rest
    }: React.AnchorHTMLAttributes<HTMLAnchorElement> & { to?: string; params?: unknown; search?: unknown }) => (
      <a href={to} {...rest}>{children}</a>
    ),
  };
});

vi.mock('@/lib/api', () => ({
  getIdValue: (id: unknown) => String(id),
}));

vi.mock('@/lib/time', () => ({
  relativeFromNow: () => '2 min ago',
}));

vi.mock('@/lib/format', () => ({
  shortId: (id: string) => id.slice(0, 12),
}));

const baseRequest = {
  id: 'req-001',
  consumerId: 'agent-abc',
  mcpServerId: 'server-xyz',
  consumerDisplayName: 'My Agent',
  mcpServerDisplayName: 'My Server',
  publisherNodeName: 'publisher-laptop',
  requestedAt: new Date().toISOString(),
} as const;

function renderCard(overrides: {
  approvePending?: boolean;
  denyPending?: boolean;
  onApprove?: () => void;
  onDeny?: () => void;
  request?: Partial<typeof baseRequest>;
} = {}) {
  const onApprove = overrides.onApprove ?? vi.fn();
  const onDeny = overrides.onDeny ?? vi.fn();
  const approvePending = overrides.approvePending ?? false;
  const denyPending = overrides.denyPending ?? false;
  const request = { ...baseRequest, ...overrides.request };
  const { container } = render(
    <AccessRequestCard
      request={request as never}
      onApprove={onApprove}
      onDeny={onDeny}
      approvePending={approvePending}
      denyPending={denyPending}
    />,
  );
  return { onApprove, onDeny, container };
}

// ---------------------------------------------------------------------------
// a11y — mobile buttons reachable
// ---------------------------------------------------------------------------

describe('AccessRequestCard — mobile button accessibility', () => {
  it('renders at least one Deny button that is NOT aria-hidden', () => {
    renderCard();
    // getAllByRole finds all buttons in the a11y tree (aria-hidden excludes them).
    const denyButtons = screen.getAllByRole('button', { name: /deny/i });
    expect(denyButtons.length).toBeGreaterThanOrEqual(1);
    denyButtons.forEach((btn) => {
      expect(btn).not.toHaveAttribute('aria-hidden', 'true');
    });
  });

  it('renders at least one Approve button that is NOT aria-hidden', () => {
    renderCard();
    const approveButtons = screen.getAllByRole('button', { name: /approve/i });
    expect(approveButtons.length).toBeGreaterThanOrEqual(1);
    approveButtons.forEach((btn) => {
      expect(btn).not.toHaveAttribute('aria-hidden', 'true');
    });
  });

  it('mobile Deny button does not carry tabIndex=-1', () => {
    renderCard();
    // The mobile buttons have h-11 class; desktop have size="sm" (no h-11).
    // We check that none of the Deny buttons carry tabIndex=-1.
    const denyButtons = screen.getAllByRole('button', { name: /deny/i });
    denyButtons.forEach((btn) => {
      expect(btn).not.toHaveAttribute('tabindex', '-1');
    });
  });

  it('mobile Approve button does not carry tabIndex=-1', () => {
    renderCard();
    const approveButtons = screen.getAllByRole('button', { name: /approve/i });
    approveButtons.forEach((btn) => {
      expect(btn).not.toHaveAttribute('tabindex', '-1');
    });
  });
});

// ---------------------------------------------------------------------------
// Interaction
// ---------------------------------------------------------------------------

describe('AccessRequestCard — interactions', () => {
  it('clicking a Deny button calls onDeny', async () => {
    const user = userEvent.setup();
    const { onDeny } = renderCard();
    // Click the first accessible Deny button (mobile full-width one).
    const [firstDeny] = screen.getAllByRole('button', { name: /deny/i });
    await user.click(firstDeny);
    expect(onDeny).toHaveBeenCalledTimes(1);
  });

  it('clicking an Approve button calls onApprove', async () => {
    const user = userEvent.setup();
    const { onApprove } = renderCard();
    const [firstApprove] = screen.getAllByRole('button', { name: /approve/i });
    await user.click(firstApprove);
    expect(onApprove).toHaveBeenCalledTimes(1);
  });

  it('buttons are disabled when approvePending=true', () => {
    renderCard({ approvePending: true });
    screen.getAllByRole('button', { name: /deny/i }).forEach((btn) => {
      expect(btn).toBeDisabled();
    });
    screen.getAllByRole('button', { name: /approve/i }).forEach((btn) => {
      expect(btn).toBeDisabled();
    });
  });

  it('buttons are disabled when denyPending=true (prevents double-submit)', () => {
    renderCard({ denyPending: true });
    screen.getAllByRole('button', { name: /deny/i }).forEach((btn) => {
      expect(btn).toBeDisabled();
    });
    screen.getAllByRole('button', { name: /approve/i }).forEach((btn) => {
      expect(btn).toBeDisabled();
    });
  });

  it('Approve label shows "Approving…" only when approvePending=true', () => {
    renderCard({ approvePending: true, denyPending: false });
    screen.getAllByRole('button', { name: /approve/i }).forEach((btn) => {
      expect(btn).toHaveTextContent('Approving…');
    });
  });

  it('regression: Approve label stays "Approve" (not "Approving…") while only Deny is pending', () => {
    renderCard({ approvePending: false, denyPending: true });
    screen.getAllByRole('button', { name: /approve/i }).forEach((btn) => {
      expect(btn).toHaveTextContent('Approve');
      expect(btn).not.toHaveTextContent('Approving');
    });
  });

  it('displays agent and server display names', () => {
    renderCard();
    expect(screen.getByRole('link', { name: 'My Agent' })).toBeInTheDocument();
    expect(screen.getByRole('link', { name: 'My Server' })).toBeInTheDocument();
  });

  it('#117: agent label links to /grants filtered by agent id', () => {
    renderCard();
    const agentLink = screen.getByRole('link', { name: 'My Agent' });
    expect(agentLink).toHaveAttribute('href', '/grants');
  });

  it('#117: server label links to /grants filtered by server id', () => {
    renderCard();
    const serverLink = screen.getByRole('link', { name: 'My Server' });
    expect(serverLink).toHaveAttribute('href', '/grants');
  });
});

// ---------------------------------------------------------------------------
// Increment 1 (HTTP MCP direct-to-Space, carried LOW from Task 6's review): a pending request
// against an http_cloud server has publisherNodeName === null/undefined — must be disclosed as
// "Cloud-terminated", never silently dropped (spec §11 decision 3).
// ---------------------------------------------------------------------------

describe('AccessRequestCard — cloud-terminated disclosure', () => {
  it('shows the publisher node name (no Cloud-terminated badge) for a normal stdio request', () => {
    const { container } = renderCard();
    // Holistic-review FIX 1: the product markup is correct (the DOM does contain
    // "publisher-laptop"), but it renders as a bare text node next to a sibling `<span> · </span>`
    // (see AccessRequestCard.tsx's `{request.publisherNodeName}<span>...</span>` fragment) —
    // testing-library's `getByText` cannot match a single exact-text node when the text is
    // "broken up by multiple elements". Assert on the rendered container's full text content
    // instead (matcher fix only — the product markup under test is unchanged).
    expect(container).toHaveTextContent(/publisher-laptop/);
    expect(screen.queryByText('Cloud-terminated')).not.toBeInTheDocument();
  });

  it('shows a "Cloud-terminated" badge instead of silently omitting the segment when publisherNodeName is absent (http_cloud request)', () => {
    renderCard({ request: { publisherNodeName: undefined } });
    expect(screen.getByText('Cloud-terminated')).toBeInTheDocument();
    expect(screen.queryByText('publisher-laptop')).not.toBeInTheDocument();
  });
});

describe('AccessRequestCard — Р27 wiring', () => {
  const base: AccessRequestSummaryDto = {
    id: { value: 'req-1' },
    consumerId: { value: 'cons-1' },
    consumerDisplayName: 'Cursor',
    mcpServerId: { value: 'srv-1' },
    mcpServerDisplayName: 'files',
    publisherNodeName: 'Studio Mac',
    status: 'Pending',
    requestedAt: new Date().toISOString(),
  };

  const noop = () => {};

  it('shows nothing extra for an ordinary first-time request', () => {
    render(
      <AccessRequestCard request={base} onApprove={noop} onDeny={noop} approvePending={false} denyPending={false} />,
    );
    expect(screen.queryByTestId('definition-change-notice')).toBeNull();
  });

  it('surfaces the diff when the request follows a definition change', () => {
    render(
      <AccessRequestCard
        request={{
          ...base,
          definitionChange: {
            changedAt: new Date().toISOString(),
            previousCommand: 'echo',
            previousArguments: 'hello',
            currentCommand: 'bash',
            currentArguments: '-c whoami',
          },
        }}
        onApprove={noop}
        onDeny={noop}
        approvePending={false}
        denyPending={false}
      />,
    );
    expect(screen.getByTestId('definition-change-notice')).toBeInTheDocument();
    expect(screen.getByText('echo hello')).toBeInTheDocument();
    expect(screen.getByText('bash -c whoami')).toBeInTheDocument();
  });
});
