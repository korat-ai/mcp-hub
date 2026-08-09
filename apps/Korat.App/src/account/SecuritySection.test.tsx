/**
 * SecuritySection tests (Task 6).
 *
 * Covers:
 *  - EmailChangeForm: submit shows "check your inbox" pending state and does NOT
 *    mutate me; 429 → "too many requests"; 400 same-as-current → "already your primary
 *    email"; client-side short-circuit for same address. Note: 409 is no longer returned
 *    by the backend (anti-enumeration posture: both success and email-in-use return 202).
 *  - SessionsList: renders list, badges current ("This device"), revoke flows
 *    through ConfirmRevokeDialog and invalidates auth.sessions; current session has
 *    no Revoke button; failed revoke surfaces error in dialog without closing it.
 *  - ConfirmRevokeDialog: generic — renders title/body; confirm disabled while
 *    pending; cancel closes without calling onConfirm; distinct "Revoke session" button.
 *
 * Network is mocked at the api module boundary (vi.mock) so no MSW/fetch.
 */
import { describe, expect, it, vi, beforeEach } from 'vitest';
import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import type { ReactNode } from 'react';
import type { MeDto } from '@/types/api';

// ---------------------------------------------------------------------------
// Session type — mirrors the exact wire shape emitted by GET /api/auth/sessions
// ---------------------------------------------------------------------------

type AuthSessionDto = {
  id: string;
  userAgent: string | null;
  createdFromIp: string | null;
  createdAt: string;
  lastUsedAt: string;
  expiresAt: string;
  current: boolean;
};

// ---------------------------------------------------------------------------
// Mock api module — tests never hit the network
// ---------------------------------------------------------------------------
vi.mock('@/lib/api', () => ({
  api: {
    auth: {
      me: vi.fn(),
      updateMe: vi.fn(),
      sessions: { list: vi.fn(), revoke: vi.fn() },
      email: { change: vi.fn(), confirm: vi.fn() },
      signout: vi.fn(),
    },
    cli: { tokens: { list: vi.fn(), revoke: vi.fn() } },
  },
  ApiError: class ApiError extends Error {
    constructor(public status: number, public body: string) {
      super(`HTTP ${status}${body ? `: ${body}` : ''}`);
      this.name = 'ApiError';
    }
  },
}));

// Mock TanStack Router — hooks.ts imports useNavigate at module level
const mockNavigate = vi.fn();
vi.mock('@tanstack/react-router', async (importOriginal) => {
  const original = await importOriginal<typeof import('@tanstack/react-router')>();
  return { ...original, useNavigate: () => mockNavigate };
});

import { api, ApiError } from '@/lib/api';
import { EmailChangeForm } from '@/account/EmailChangeForm';
import { SessionsList } from '@/account/SessionsList';
import { ConfirmRevokeDialog } from '@/account/ConfirmRevokeDialog';
import { SecuritySection } from '@/account/SecuritySection';

// ---------------------------------------------------------------------------
// Test harness
// ---------------------------------------------------------------------------

function makeWrapper() {
  const qc = new QueryClient({
    defaultOptions: {
      queries: { retry: false, gcTime: 0 },
      mutations: { retry: false },
    },
  });
  const wrapper = ({ children }: { children: ReactNode }) => (
    <QueryClientProvider client={qc}>{children}</QueryClientProvider>
  );
  return { qc, wrapper };
}

const meData: MeDto = {
  userId: 'u1',
  displayName: 'Ada Lovelace',
  primaryEmail: 'ada@example.com',
  providers: [],
  pendingEmailChange: null,
};

/** Two sessions matching the exact backend wire shape (current marker from server). */
const sessions: AuthSessionDto[] = [
  {
    id: 's1',
    userAgent: 'Mozilla/5.0 (Macintosh)',
    createdFromIp: '192.0.2.1',
    lastUsedAt: '2026-05-30T10:00:00Z',
    createdAt: '2026-05-01T08:00:00Z',
    expiresAt: '2026-06-30T10:00:00Z',
    current: true,
  },
  {
    id: 's2',
    userAgent: 'Mozilla/5.0 (iPhone)',
    createdFromIp: '192.0.2.2',
    lastUsedAt: '2026-05-29T18:00:00Z',
    createdAt: '2026-05-15T12:00:00Z',
    expiresAt: '2026-06-29T18:00:00Z',
    current: false,
  },
];

beforeEach(() => {
  vi.clearAllMocks();
  mockNavigate.mockReset();
});

// ---------------------------------------------------------------------------
// ConfirmRevokeDialog
// ---------------------------------------------------------------------------

describe('ConfirmRevokeDialog', () => {
  it('renders title and body text', () => {
    const { wrapper } = makeWrapper();
    render(
      <ConfirmRevokeDialog
        open
        onOpenChange={vi.fn()}
        title="Revoke session"
        body="This will end the session immediately."
        onConfirm={vi.fn()}
        pending={false}
      />,
      { wrapper },
    );

    expect(screen.getByRole('heading', { name: 'Revoke session' })).toBeInTheDocument();
    expect(screen.getByText('This will end the session immediately.')).toBeInTheDocument();
  });

  it('disables confirm button while pending', () => {
    const { wrapper } = makeWrapper();
    render(
      <ConfirmRevokeDialog
        open
        onOpenChange={vi.fn()}
        title="Revoke"
        body="Are you sure?"
        onConfirm={vi.fn()}
        pending
      />,
      { wrapper },
    );

    const confirmBtn = screen.getByRole('button', { name: /revoke session/i });
    expect(confirmBtn).toBeDisabled();
  });

  it('calls onConfirm when confirm is clicked', async () => {
    const user = userEvent.setup();
    const onConfirm = vi.fn();
    const { wrapper } = makeWrapper();
    render(
      <ConfirmRevokeDialog
        open
        onOpenChange={vi.fn()}
        title="Revoke session"
        body="Are you sure?"
        onConfirm={onConfirm}
        pending={false}
      />,
      { wrapper },
    );

    await user.click(screen.getByRole('button', { name: /revoke session/i }));
    expect(onConfirm).toHaveBeenCalledOnce();
  });

  it('calls onOpenChange(false) when cancel is clicked and does NOT call onConfirm', async () => {
    const user = userEvent.setup();
    const onConfirm = vi.fn();
    const onOpenChange = vi.fn();
    const { wrapper } = makeWrapper();
    render(
      <ConfirmRevokeDialog
        open
        onOpenChange={onOpenChange}
        title="Revoke"
        body="Are you sure?"
        onConfirm={onConfirm}
        pending={false}
      />,
      { wrapper },
    );

    await user.click(screen.getByRole('button', { name: /cancel/i }));
    expect(onOpenChange).toHaveBeenCalledWith(false);
    expect(onConfirm).not.toHaveBeenCalled();
  });

  it('renders inline error when error prop is set', () => {
    const { wrapper } = makeWrapper();
    render(
      <ConfirmRevokeDialog
        open
        onOpenChange={vi.fn()}
        title="Revoke"
        body="Are you sure?"
        onConfirm={vi.fn()}
        pending={false}
        error="Network error — please try again."
      />,
      { wrapper },
    );

    expect(screen.getByRole('alert')).toBeInTheDocument();
    expect(screen.getByText(/network error/i)).toBeInTheDocument();
  });
});

// ---------------------------------------------------------------------------
// EmailChangeForm
// ---------------------------------------------------------------------------

describe('EmailChangeForm', () => {
  it('renders the current email address as placeholder/label', () => {
    const { wrapper } = makeWrapper();
    render(<EmailChangeForm me={meData} />, { wrapper });

    // The form should reference the current email in some visible way
    expect(screen.getByText(/ada@example\.com/i)).toBeInTheDocument();
  });

  it('shows "check your inbox" state after successful submit and does NOT mutate me', async () => {
    (api.auth.email.change as ReturnType<typeof vi.fn>).mockResolvedValue(undefined);

    const user = userEvent.setup();
    const { qc, wrapper } = makeWrapper();
    const invalidateSpy = vi.spyOn(qc, 'invalidateQueries');

    render(<EmailChangeForm me={meData} />, { wrapper });

    const input = screen.getByRole('textbox', { name: /new email/i });
    await user.type(input, 'new@example.com');
    await user.click(screen.getByRole('button', { name: /send verification/i }));

    await waitFor(() =>
      expect(screen.getByText(/check your inbox/i)).toBeInTheDocument(),
    );

    // Must NOT invalidate auth.me — email is not changed until confirmed
    const meCalls = invalidateSpy.mock.calls.filter(
      (args) => JSON.stringify(args[0]) === JSON.stringify({ queryKey: ['auth', 'me'] }),
    );
    expect(meCalls).toHaveLength(0);
  });

  it('shows "check your inbox" for an email that is already in use (anti-enumeration: server returns 202)', async () => {
    // Anti-enumeration posture: the backend returns 202 regardless of whether the address
    // is taken. The frontend shows "check your inbox" — it cannot and should not distinguish
    // success from email-in-use. The old 409 branch is intentionally removed.
    (api.auth.email.change as ReturnType<typeof vi.fn>).mockResolvedValue(undefined);

    const user = userEvent.setup();
    const { wrapper } = makeWrapper();
    render(<EmailChangeForm me={meData} />, { wrapper });

    const input = screen.getByRole('textbox', { name: /new email/i });
    await user.type(input, 'taken@example.com');
    await user.click(screen.getByRole('button', { name: /send verification/i }));

    expect(await screen.findByText(/check your inbox/i)).toBeInTheDocument();
  });

  it('shows inline error for 429 (too many requests)', async () => {
    (api.auth.email.change as ReturnType<typeof vi.fn>).mockRejectedValue(
      new ApiError(429, 'Too many requests'),
    );

    const user = userEvent.setup();
    const { wrapper } = makeWrapper();
    render(<EmailChangeForm me={meData} />, { wrapper });

    const input = screen.getByRole('textbox', { name: /new email/i });
    await user.type(input, 'other@example.com');
    await user.click(screen.getByRole('button', { name: /send verification/i }));

    expect(await screen.findByRole('alert')).toBeInTheDocument();
    expect(await screen.findByText(/too many requests/i)).toBeInTheDocument();
  });

  it('shows inline error for 400 same-as-current without surfacing raw ApiError string', async () => {
    (api.auth.email.change as ReturnType<typeof vi.fn>).mockRejectedValue(
      new ApiError(400, JSON.stringify({ error: 'same-as-current' })),
    );

    const user = userEvent.setup();
    const { wrapper } = makeWrapper();
    render(<EmailChangeForm me={meData} />, { wrapper });

    const input = screen.getByRole('textbox', { name: /new email/i });
    // Use a different address to bypass the client-side guard
    await user.type(input, 'ADA@EXAMPLE.COM');
    await user.click(screen.getByRole('button', { name: /send verification/i }));

    expect(await screen.findByRole('alert')).toBeInTheDocument();
    expect(await screen.findByText(/already your primary email/i)).toBeInTheDocument();
    // Must not show raw HTTP error string
    expect(screen.queryByText(/HTTP 400/i)).not.toBeInTheDocument();
  });

  it('short-circuits with inline error when entered email matches current email (client-side)', async () => {
    const user = userEvent.setup();
    const { wrapper } = makeWrapper();
    render(<EmailChangeForm me={meData} />, { wrapper });

    const input = screen.getByRole('textbox', { name: /new email/i });
    await user.type(input, 'ada@example.com');
    await user.click(screen.getByRole('button', { name: /send verification/i }));

    expect(screen.getByRole('alert')).toBeInTheDocument();
    expect(screen.getByText(/already your primary email/i)).toBeInTheDocument();
    expect(api.auth.email.change).not.toHaveBeenCalled();
  });

  it('disables submit button while mutation is pending', async () => {
    let release: () => void = () => undefined;
    const inflight = new Promise<void>((resolve) => {
      release = () => resolve();
    });
    (api.auth.email.change as ReturnType<typeof vi.fn>).mockReturnValue(inflight);

    const user = userEvent.setup();
    const { wrapper } = makeWrapper();
    render(<EmailChangeForm me={meData} />, { wrapper });

    const input = screen.getByRole('textbox', { name: /new email/i });
    await user.type(input, 'pending@example.com');
    const submitBtn = screen.getByRole('button', { name: /send verification/i });
    await user.click(submitBtn);

    await waitFor(() => expect(submitBtn).toBeDisabled());
    release();
  });
});

// ---------------------------------------------------------------------------
// SessionsList
// ---------------------------------------------------------------------------

describe('SessionsList', () => {
  it('renders all sessions with the current one badged "This device"', async () => {
    (api.auth.sessions.list as ReturnType<typeof vi.fn>).mockResolvedValue(sessions);

    const { wrapper } = makeWrapper();
    render(<SessionsList />, { wrapper });

    expect(await screen.findByText(/this device/i)).toBeInTheDocument();
    // Both user agents should appear
    expect(screen.getByText(/macintosh/i)).toBeInTheDocument();
    expect(screen.getByText(/iphone/i)).toBeInTheDocument();
  });

  it('does NOT show a revoke button for the current session (shows "This device" badge instead)', async () => {
    (api.auth.sessions.list as ReturnType<typeof vi.fn>).mockResolvedValue(sessions);

    const { wrapper } = makeWrapper();
    render(<SessionsList />, { wrapper });

    await screen.findByText(/this device/i);

    // There should be exactly one Revoke button — for the non-current session.
    // Query for the list-row revoke button specifically (not dialog confirm).
    const revokeButtons = screen.getAllByRole('button', { name: /revoke session for/i });
    expect(revokeButtons).toHaveLength(1);
  });

  it('opens confirm dialog on revoke click and calls revoke API after confirm, then invalidates auth.sessions', async () => {
    (api.auth.sessions.list as ReturnType<typeof vi.fn>).mockResolvedValue(sessions);
    (api.auth.sessions.revoke as ReturnType<typeof vi.fn>).mockResolvedValue(undefined);

    const user = userEvent.setup();
    const { qc, wrapper } = makeWrapper();
    const invalidateSpy = vi.spyOn(qc, 'invalidateQueries');

    render(<SessionsList />, { wrapper });

    // Click the revoke button for the non-current session (aria-label contains "revoke session for")
    const revokeBtn = await screen.findByRole('button', { name: /revoke session for/i });
    await user.click(revokeBtn);

    // Dialog should appear
    const dialog = await screen.findByRole('dialog');
    expect(dialog).toBeInTheDocument();

    // Confirm using the dialog's distinct "Revoke session" button
    await user.click(within(dialog).getByRole('button', { name: /revoke session/i }));

    await waitFor(() =>
      expect(api.auth.sessions.revoke).toHaveBeenCalledWith('s2'),
    );
    expect(invalidateSpy).toHaveBeenCalledWith(
      expect.objectContaining({ queryKey: ['auth', 'sessions'] }),
    );
  });

  it('keeps dialog open and shows error message on revoke failure', async () => {
    (api.auth.sessions.list as ReturnType<typeof vi.fn>).mockResolvedValue(sessions);
    (api.auth.sessions.revoke as ReturnType<typeof vi.fn>).mockRejectedValue(
      new Error('Network error'),
    );

    const user = userEvent.setup();
    const { wrapper } = makeWrapper();
    render(<SessionsList />, { wrapper });

    const revokeBtn = await screen.findByRole('button', { name: /revoke session for/i });
    await user.click(revokeBtn);

    const dialog = await screen.findByRole('dialog');
    await user.click(within(dialog).getByRole('button', { name: /revoke session/i }));

    // Dialog stays open; error is surfaced
    await waitFor(() =>
      expect(screen.getByRole('alert')).toBeInTheDocument(),
    );
    expect(screen.getByRole('dialog')).toBeInTheDocument();
  });

  it('shows loading state while sessions are fetching', () => {
    (api.auth.sessions.list as ReturnType<typeof vi.fn>).mockReturnValue(new Promise(() => undefined));

    const { wrapper } = makeWrapper();
    render(<SessionsList />, { wrapper });

    // Should render some loading indicator (skeleton or spinner)
    // We just assert no session items are rendered yet
    expect(screen.queryByText(/this device/i)).not.toBeInTheDocument();
  });

  it('renders "Unknown device" without crashing when userAgent is null (OAuth sessions)', async () => {
    // Regression: freshly-created OAuth sessions arrive with userAgent = null.
    // Accessing .length on null was the original production crash.
    const nullUaSessions: AuthSessionDto[] = [
      {
        id: 's-oauth',
        userAgent: null,
        createdFromIp: '10.0.0.1',
        createdAt: '2026-05-01T08:00:00Z',
        lastUsedAt: '2026-05-30T10:00:00Z',
        expiresAt: '2026-06-30T10:00:00Z',
        current: true,
      },
    ];
    (api.auth.sessions.list as ReturnType<typeof vi.fn>).mockResolvedValue(nullUaSessions);

    const { wrapper } = makeWrapper();
    render(<SessionsList />, { wrapper });

    // Should render without throwing and fall back to "Unknown device"
    expect(await screen.findByText(/unknown device/i)).toBeInTheDocument();
    expect(screen.getByText(/this device/i)).toBeInTheDocument();
  });

  it('surfaces createdFromIp in the session row', async () => {
    (api.auth.sessions.list as ReturnType<typeof vi.fn>).mockResolvedValue(sessions);

    const { wrapper } = makeWrapper();
    render(<SessionsList />, { wrapper });

    await screen.findByText(/this device/i);
    // Both sessions have IPs
    expect(screen.getAllByText(/192\.0\.2\./i).length).toBeGreaterThanOrEqual(1);
  });
});

// ---------------------------------------------------------------------------
// SecuritySection (composition)
// ---------------------------------------------------------------------------

describe('SecuritySection', () => {
  it('renders EmailChangeForm and SessionsList together', async () => {
    (api.auth.sessions.list as ReturnType<typeof vi.fn>).mockResolvedValue(sessions);

    const { wrapper } = makeWrapper();
    render(<SecuritySection me={meData} />, { wrapper });

    // EmailChangeForm marker
    expect(screen.getByText(/ada@example\.com/i)).toBeInTheDocument();
    // SessionsList loads
    expect(await screen.findByText(/this device/i)).toBeInTheDocument();
  });
});
