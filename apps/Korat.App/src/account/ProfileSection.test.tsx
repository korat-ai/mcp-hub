/**
 * ProfileSection tests (Task 5).
 *
 * Covers:
 *  - DisplayNameForm: renders current display name; blank submit shows inline
 *    error; successful submit calls useUpdateMe and invalidates auth.me.
 *  - ConnectedProviders: renders provider chips read-only — no unlink button.
 *
 * Network is mocked at the api module boundary (vi.mock) so these tests run
 * entirely in-process without MSW or fetch.
 */
import { describe, expect, it, vi, beforeEach } from 'vitest';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import type { ReactNode } from 'react';
import type { MeDto } from '@/types/api';

// ---------------------------------------------------------------------------
// Mock api module so tests never hit the network
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
}));

// Mock TanStack Router navigate (useUpdateMe doesn't navigate but hooks.ts
// imports useNavigate at module level — must be available).
vi.mock('@tanstack/react-router', async (importOriginal) => {
  const original = await importOriginal<typeof import('@tanstack/react-router')>();
  return { ...original, useNavigate: () => vi.fn() };
});

import { api } from '@/lib/api';
import { DisplayNameForm } from '@/account/DisplayNameForm';
import { ConnectedProviders } from '@/account/ConnectedProviders';
import { ProfileSection } from '@/account/ProfileSection';

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
  providers: [
    { provider: 'github', externalId: 'gh-123', linkedAt: '2024-01-01T00:00:00Z' },
    { provider: 'google', externalId: 'goog-456', linkedAt: '2024-01-02T00:00:00Z' },
  ],
  pendingEmailChange: null,
};

beforeEach(() => {
  vi.clearAllMocks();
});

// ---------------------------------------------------------------------------
// DisplayNameForm
// ---------------------------------------------------------------------------

describe('DisplayNameForm', () => {
  it('renders current display name in the input field', () => {
    const { wrapper } = makeWrapper();
    render(<DisplayNameForm me={meData} />, { wrapper });

    const input = screen.getByRole('textbox', { name: /display name/i });
    expect(input).toHaveValue('Ada Lovelace');
  });

  it('shows inline error when blank name is submitted', async () => {
    const user = userEvent.setup();
    const { wrapper } = makeWrapper();
    render(<DisplayNameForm me={meData} />, { wrapper });

    const input = screen.getByRole('textbox', { name: /display name/i });
    await user.clear(input);
    await user.click(screen.getByRole('button', { name: /save/i }));

    expect(await screen.findByRole('alert')).toBeInTheDocument();
    expect(api.auth.updateMe).not.toHaveBeenCalled();
  });

  it('calls updateMe and invalidates auth.me on successful submit', async () => {
    const updatedMe: MeDto = { ...meData, displayName: 'Grace Hopper' };
    (api.auth.updateMe as ReturnType<typeof vi.fn>).mockResolvedValue(updatedMe);
    (api.auth.me as ReturnType<typeof vi.fn>).mockResolvedValue(updatedMe);

    const user = userEvent.setup();
    const { qc, wrapper } = makeWrapper();
    const invalidateSpy = vi.spyOn(qc, 'invalidateQueries');

    render(<DisplayNameForm me={meData} />, { wrapper });

    const input = screen.getByRole('textbox', { name: /display name/i });
    await user.clear(input);
    await user.type(input, 'Grace Hopper');
    await user.click(screen.getByRole('button', { name: /save/i }));

    await waitFor(() =>
      expect(api.auth.updateMe).toHaveBeenCalledWith({ displayName: 'Grace Hopper' }),
    );
    expect(invalidateSpy).toHaveBeenCalledWith(
      expect.objectContaining({ queryKey: ['auth', 'me'] }),
    );
  });

  it('disables save button while mutation is pending', async () => {
    // Keep the PUT hanging so isPending stays true
    let release: () => void = () => undefined;
    const inflight = new Promise<MeDto>((resolve) => {
      release = () => resolve(meData);
    });
    (api.auth.updateMe as ReturnType<typeof vi.fn>).mockReturnValue(inflight);

    const user = userEvent.setup();
    const { wrapper } = makeWrapper();
    render(<DisplayNameForm me={meData} />, { wrapper });

    const input = screen.getByRole('textbox', { name: /display name/i });
    await user.clear(input);
    await user.type(input, 'Pending Name');
    const saveBtn = screen.getByRole('button', { name: /save/i });
    await user.click(saveBtn);

    await waitFor(() => expect(saveBtn).toBeDisabled());
    release();
  });
});

// ---------------------------------------------------------------------------
// ConnectedProviders
// ---------------------------------------------------------------------------

describe('ConnectedProviders', () => {
  it('renders connected provider chips without an unlink button', () => {
    const { wrapper } = makeWrapper();
    render(<ConnectedProviders providers={meData.providers} />, { wrapper });

    expect(screen.getByText(/github/i)).toBeInTheDocument();
    expect(screen.getByText(/google/i)).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: /unlink/i })).not.toBeInTheDocument();
  });

  it('renders empty state when no providers are connected', () => {
    const { wrapper } = makeWrapper();
    render(<ConnectedProviders providers={[]} />, { wrapper });

    expect(screen.getByText(/no connected providers/i)).toBeInTheDocument();
  });

  it('renders empty state without crashing when providers is undefined (API omits field)', () => {
    // Regression test: backend does not yet return providers; the field arrives as
    // undefined. Accessing .length on undefined was the production crash.
    const { wrapper } = makeWrapper();
    render(<ConnectedProviders providers={undefined} />, { wrapper });

    expect(screen.getByText(/no connected providers/i)).toBeInTheDocument();
  });

  it('offers a Connect button only for supported providers not yet linked (027)', () => {
    const { wrapper } = makeWrapper();
    render(
      <ConnectedProviders
        providers={[{ provider: 'github', externalId: 'gh-1', linkedAt: '2024-01-01T00:00:00Z' }]}
      />,
      { wrapper },
    );

    const connectGoogle = screen.getByRole('link', { name: /connect google/i });
    expect(connectGoogle.getAttribute('href')).toContain('/signin/google?link=1');
    // GitHub already linked → no Connect GitHub.
    expect(screen.queryByRole('link', { name: /connect github/i })).not.toBeInTheDocument();
  });

  it('offers Connect for every supported provider when none are linked (027)', () => {
    const { wrapper } = makeWrapper();
    render(<ConnectedProviders providers={[]} />, { wrapper });

    expect(screen.getByRole('link', { name: /connect github/i })).toBeInTheDocument();
    expect(screen.getByRole('link', { name: /connect google/i })).toBeInTheDocument();
  });
});

// ---------------------------------------------------------------------------
// ProfileSection
// ---------------------------------------------------------------------------

describe('ProfileSection', () => {
  it('composes DisplayNameForm and ConnectedProviders given me data', () => {
    (api.auth.me as ReturnType<typeof vi.fn>).mockResolvedValue(meData);
    const { wrapper } = makeWrapper();

    render(<ProfileSection me={meData} />, { wrapper });

    // DisplayNameForm rendered with current name
    const input = screen.getByRole('textbox', { name: /display name/i });
    expect(input).toHaveValue('Ada Lovelace');

    // ConnectedProviders rendered without unlink
    expect(screen.getByText(/github/i)).toBeInTheDocument();
    expect(screen.getByText(/google/i)).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: /unlink/i })).not.toBeInTheDocument();
  });
});
