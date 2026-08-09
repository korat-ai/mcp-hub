import { describe, expect, it, vi } from 'vitest';
import { useState } from 'react';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { http, HttpResponse } from 'msw';
import { server } from '../setup';
import { withQueryClient } from '../test-utils';
import { ConfirmDialog } from '@/components/domain/ConfirmDialog';
import { useRevokeGrant } from '@/hooks/useRevokeGrant';

// Stub next-themes for Toaster's useTheme dependency.
vi.mock('next-themes', () => ({ useTheme: () => ({ theme: 'light' }) }));

const { Toaster } = await import('@/components/ui/sonner');

function Harness() {
  const [open, setOpen] = useState(true);
  const revoke = useRevokeGrant();
  return (
    <>
      <ConfirmDialog
        open={open}
        onOpenChange={setOpen}
        title="Revoke @anya's access to slack?"
        description="This stops new sessions immediately."
        confirmLabel="Revoke"
        destructive
        isPending={revoke.isPending}
        onConfirm={() =>
          revoke.mutate({ grantId: 'g1', agentName: '@anya', serverName: 'slack' })
        }
      />
      <Toaster />
    </>
  );
}

describe('ConfirmDialog + useRevokeGrant', () => {
  it('confirm click fires receipt toast', async () => {
    server.use(
      http.post('/api/grants/g1/revoke', () => new HttpResponse(null, { status: 204 })),
    );
    const user = userEvent.setup();
    render(withQueryClient(<Harness />));
    const revokeBtn = await screen.findByRole('button', { name: /^revoke$/i });
    await user.click(revokeBtn);
    await waitFor(() =>
      expect(screen.getByText(/permission revoked/i)).toBeInTheDocument(),
    );
    expect(screen.getByText(/@anya → slack/)).toBeInTheDocument();
  });

  it('Cancel button stays enabled while mutation is pending', async () => {
    // Keep the POST hanging so isPending stays true.
    let releasePost: () => void = () => undefined;
    const inflight = new Promise<void>((resolve) => {
      releasePost = resolve;
    });
    server.use(
      http.post('/api/grants/g1/revoke', async () => {
        await inflight;
        return new HttpResponse(null, { status: 204 });
      }),
    );

    const user = userEvent.setup();
    render(withQueryClient(<Harness />));

    // Click Revoke to start the mutation.
    const revokeBtn = await screen.findByRole('button', { name: /^revoke$/i });
    await user.click(revokeBtn);

    // While pending, the Revoke button disables but Cancel stays enabled.
    await waitFor(() => expect(revokeBtn).toBeDisabled());
    const cancelBtn = screen.getByRole('button', { name: /cancel/i });
    expect(cancelBtn).toBeEnabled();

    // Release the stuck mutation so the test cleans up cleanly.
    releasePost();
  });
});
