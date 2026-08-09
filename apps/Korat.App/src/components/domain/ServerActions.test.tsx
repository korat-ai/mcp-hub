/**
 * Unit tests for ServerActions (spec 021 Layer 3; Increment 2 adds the NeedsReauth branch,
 * Task 6).
 *
 * ServerActions takes already-constructed mutation objects as props (the caller owns the
 * useDisableServer/useEnableServer/useDeleteServer/useReconnectServer hook instances) — there is
 * nothing to mock at the `@/lib/api` or `@tanstack/react-query` boundary here. The mock*Hook()
 * factories below stand in for a `useMutation` return value, exposing only the shape this
 * component actually reads: `mutate`, `isPending`, `variables`.
 *
 * Covers:
 *  - NeedsReauth availability shows Reconnect (not Delete).
 *  - Clicking Reconnect calls reconnect.mutate with { serverId, displayName }.
 */
import { describe, expect, it, vi } from 'vitest';
import { render, screen, fireEvent } from '@testing-library/react';
import { ServerActions } from './ServerActions';
import type { useDisableServer } from '@/hooks/useDisableServer';
import type { useEnableServer } from '@/hooks/useEnableServer';
import type { useDeleteServer } from '@/hooks/useDeleteServer';
import type { useReconnectServer } from '@/hooks/useReconnectServer';

function mockDisableHook() {
  return { mutate: vi.fn(), isPending: false, variables: undefined } as unknown as ReturnType<typeof useDisableServer>;
}
function mockEnableHook() {
  return { mutate: vi.fn(), isPending: false, variables: undefined } as unknown as ReturnType<typeof useEnableServer>;
}
function mockDeleteHook() {
  return { mutate: vi.fn(), isPending: false, variables: undefined } as unknown as ReturnType<typeof useDeleteServer>;
}
function mockReconnectHook() {
  return { mutate: vi.fn(), isPending: false, variables: undefined } as unknown as ReturnType<typeof useReconnectServer>;
}

describe('ServerActions — NeedsReauth', () => {
  it('shows a Reconnect button (not Delete) for NeedsReauth availability', () => {
    render(
      <ServerActions
        serverId="srv-1" displayName="my-server" availability="NeedsReauth"
        disable={mockDisableHook()} enable={mockEnableHook()} deleteSrv={mockDeleteHook()}
        reconnect={mockReconnectHook()}
      />,
    );
    expect(screen.getByRole('button', { name: /reconnect/i })).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: /delete/i })).not.toBeInTheDocument();
  });

  it('clicking Reconnect calls reconnect.mutate with the server id', () => {
    const reconnect = mockReconnectHook();
    render(
      <ServerActions
        serverId="srv-1" displayName="my-server" availability="NeedsReauth"
        disable={mockDisableHook()} enable={mockEnableHook()} deleteSrv={mockDeleteHook()}
        reconnect={reconnect}
      />,
    );
    fireEvent.click(screen.getByRole('button', { name: /reconnect/i }));
    expect(reconnect.mutate).toHaveBeenCalledWith({ serverId: 'srv-1', displayName: 'my-server' });
  });
});

describe('ServerActions — other availability states (regression guard)', () => {
  it('shows Disable for Available', () => {
    render(
      <ServerActions
        serverId="srv-1" displayName="my-server" availability="Available"
        disable={mockDisableHook()} enable={mockEnableHook()} deleteSrv={mockDeleteHook()}
        reconnect={mockReconnectHook()}
      />,
    );
    expect(screen.getByRole('button', { name: /disable/i })).toBeInTheDocument();
  });

  it('shows Enable for Disabled', () => {
    render(
      <ServerActions
        serverId="srv-1" displayName="my-server" availability="Disabled"
        disable={mockDisableHook()} enable={mockEnableHook()} deleteSrv={mockDeleteHook()}
        reconnect={mockReconnectHook()}
      />,
    );
    expect(screen.getByRole('button', { name: /enable/i })).toBeInTheDocument();
  });

  it('shows Delete for Unavailable', () => {
    render(
      <ServerActions
        serverId="srv-1" displayName="my-server" availability="Unavailable"
        disable={mockDisableHook()} enable={mockEnableHook()} deleteSrv={mockDeleteHook()}
        reconnect={mockReconnectHook()}
      />,
    );
    expect(screen.getByRole('button', { name: /delete/i })).toBeInTheDocument();
  });
});
