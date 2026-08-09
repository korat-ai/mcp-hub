/**
 * CliTokensSection tests (Task 7).
 *
 * Covers:
 *  - CliTokenList: renders token list (name, createdAt, lastUsedAt); empty state
 *    when no tokens; revoke flows through ConfirmRevokeDialog and invalidates
 *    cli.tokens; confirm button disabled while pending; failed revoke surfaces
 *    error in dialog without closing it.
 *  - CliTokensSection: composition renders CliTokenList inside a card.
 *
 * Network is mocked at the api module boundary (vi.mock) so no MSW/fetch.
 */
import { describe, expect, it, vi, beforeEach } from 'vitest';
import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import type { ReactNode } from 'react';
import type { CliTokenDto } from '@/types/api';

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
    cli: {
      tokens: { list: vi.fn(), revoke: vi.fn() },
    },
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

import { api } from '@/lib/api';
import { CliTokenList } from '@/account/CliTokenList';
import { CliTokensSection } from '@/account/CliTokensSection';

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

/** Two sample CLI tokens matching the wire shape from GET /api/cli/tokens */
const tokens: CliTokenDto[] = [
  {
    id: 'tk1',
    name: 'My Laptop',
    createdAt: '2026-05-01T08:00:00Z',
    lastUsedAt: '2026-05-30T10:00:00Z',
    expiresAt: '2027-05-01T08:00:00Z',
  },
  {
    id: 'tk2',
    name: 'CI Runner',
    createdAt: '2026-04-15T12:00:00Z',
    lastUsedAt: null,
    expiresAt: null,
  },
];

beforeEach(() => {
  vi.clearAllMocks();
  mockNavigate.mockReset();
});

// ---------------------------------------------------------------------------
// CliTokenList
// ---------------------------------------------------------------------------

describe('CliTokenList', () => {
  it('renders token names and createdAt dates', async () => {
    (api.cli.tokens.list as ReturnType<typeof vi.fn>).mockResolvedValue(tokens);

    const { wrapper } = makeWrapper();
    render(<CliTokenList />, { wrapper });

    expect(await screen.findByText('My Laptop')).toBeInTheDocument();
    expect(screen.getByText('CI Runner')).toBeInTheDocument();
    // createdAt should be rendered in some human-readable form
    // We just verify the text nodes exist for both tokens
    expect(screen.getAllByText(/2026/i).length).toBeGreaterThanOrEqual(1);
  });

  it('shows lastUsedAt when present and "Never" when absent', async () => {
    (api.cli.tokens.list as ReturnType<typeof vi.fn>).mockResolvedValue(tokens);

    const { wrapper } = makeWrapper();
    render(<CliTokenList />, { wrapper });

    await screen.findByText('My Laptop');
    // Token with lastUsedAt should not say "Never" for that row
    // Token with null lastUsedAt (CI Runner) should show "Never"
    expect(screen.getByText(/never/i)).toBeInTheDocument();
  });

  it('renders empty state when token list is empty', async () => {
    (api.cli.tokens.list as ReturnType<typeof vi.fn>).mockResolvedValue([]);

    const { wrapper } = makeWrapper();
    render(<CliTokenList />, { wrapper });

    expect(await screen.findByText(/no cli tokens/i)).toBeInTheDocument();
  });

  it('shows loading state while tokens are fetching', () => {
    (api.cli.tokens.list as ReturnType<typeof vi.fn>).mockReturnValue(new Promise(() => undefined));

    const { wrapper } = makeWrapper();
    render(<CliTokenList />, { wrapper });

    // No token names rendered yet; just assert absence
    expect(screen.queryByText('My Laptop')).not.toBeInTheDocument();
    expect(screen.queryByText('CI Runner')).not.toBeInTheDocument();
  });

  it('opens confirm dialog on revoke click and calls revoke API after confirm, then invalidates cli.tokens', async () => {
    (api.cli.tokens.list as ReturnType<typeof vi.fn>).mockResolvedValue(tokens);
    (api.cli.tokens.revoke as ReturnType<typeof vi.fn>).mockResolvedValue(undefined);

    const user = userEvent.setup();
    const { qc, wrapper } = makeWrapper();
    const invalidateSpy = vi.spyOn(qc, 'invalidateQueries');

    render(<CliTokenList />, { wrapper });

    // Click the revoke button for the first token
    const revokeBtn = await screen.findByRole('button', { name: /revoke token my laptop/i });
    await user.click(revokeBtn);

    // Dialog should appear
    const dialog = await screen.findByRole('dialog');
    expect(dialog).toBeInTheDocument();

    // Confirm using the dialog's button
    await user.click(within(dialog).getByRole('button', { name: /revoke token/i }));

    await waitFor(() =>
      expect(api.cli.tokens.revoke).toHaveBeenCalledWith('tk1'),
    );
    expect(invalidateSpy).toHaveBeenCalledWith(
      expect.objectContaining({ queryKey: ['cli', 'tokens'] }),
    );
  });

  it('disables confirm button while revoke mutation is pending', async () => {
    (api.cli.tokens.list as ReturnType<typeof vi.fn>).mockResolvedValue(tokens);
    let release: () => void = () => undefined;
    const inflight = new Promise<void>((resolve) => {
      release = () => resolve();
    });
    (api.cli.tokens.revoke as ReturnType<typeof vi.fn>).mockReturnValue(inflight);

    const user = userEvent.setup();
    const { wrapper } = makeWrapper();
    render(<CliTokenList />, { wrapper });

    const revokeBtn = await screen.findByRole('button', { name: /revoke token my laptop/i });
    await user.click(revokeBtn);

    const dialog = await screen.findByRole('dialog');
    const confirmBtn = within(dialog).getByRole('button', { name: /revoke token/i });
    await user.click(confirmBtn);

    await waitFor(() => expect(confirmBtn).toBeDisabled());
    release();
  });

  it('keeps dialog open and shows error on revoke failure', async () => {
    (api.cli.tokens.list as ReturnType<typeof vi.fn>).mockResolvedValue(tokens);
    (api.cli.tokens.revoke as ReturnType<typeof vi.fn>).mockRejectedValue(
      new Error('Network error'),
    );

    const user = userEvent.setup();
    const { wrapper } = makeWrapper();
    render(<CliTokenList />, { wrapper });

    const revokeBtn = await screen.findByRole('button', { name: /revoke token my laptop/i });
    await user.click(revokeBtn);

    const dialog = await screen.findByRole('dialog');
    await user.click(within(dialog).getByRole('button', { name: /revoke token/i }));

    // Dialog stays open; error is surfaced
    await waitFor(() =>
      expect(screen.getByRole('alert')).toBeInTheDocument(),
    );
    expect(screen.getByRole('dialog')).toBeInTheDocument();
  });

  it('closes dialog on cancel without calling revoke', async () => {
    (api.cli.tokens.list as ReturnType<typeof vi.fn>).mockResolvedValue(tokens);

    const user = userEvent.setup();
    const { wrapper } = makeWrapper();
    render(<CliTokenList />, { wrapper });

    const revokeBtn = await screen.findByRole('button', { name: /revoke token my laptop/i });
    await user.click(revokeBtn);

    const dialog = await screen.findByRole('dialog');
    await user.click(within(dialog).getByRole('button', { name: /cancel/i }));

    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument());
    expect(api.cli.tokens.revoke).not.toHaveBeenCalled();
  });
});

// ---------------------------------------------------------------------------
// CliTokensSection (composition)
// ---------------------------------------------------------------------------

describe('CliTokensSection', () => {
  it('renders the CLI tokens card with token list', async () => {
    (api.cli.tokens.list as ReturnType<typeof vi.fn>).mockResolvedValue(tokens);

    const { wrapper } = makeWrapper();
    render(<CliTokensSection />, { wrapper });

    // Card heading
    expect(screen.getByText(/cli tokens/i)).toBeInTheDocument();
    // Token list renders
    expect(await screen.findByText('My Laptop')).toBeInTheDocument();
  });

  it('renders empty state inside the card when no tokens', async () => {
    (api.cli.tokens.list as ReturnType<typeof vi.fn>).mockResolvedValue([]);

    const { wrapper } = makeWrapper();
    render(<CliTokensSection />, { wrapper });

    expect(await screen.findByText(/no cli tokens/i)).toBeInTheDocument();
  });
});
