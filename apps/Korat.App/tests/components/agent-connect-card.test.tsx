import { describe, it, expect, vi } from 'vitest';
import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import {
  createMemoryHistory,
  createRootRoute,
  createRoute,
  createRouter,
  RouterProvider,
} from '@tanstack/react-router';
import { AgentConnectCard } from '@/components/domain/AgentConnectCard';

// ---------------------------------------------------------------------------
// Minimal router harness — AgentConnectCard uses <Link to="/servers/how_to_connect">
// ---------------------------------------------------------------------------

function makeRouter() {
  const rootRoute = createRootRoute();
  const cardRoute = createRoute({
    getParentRoute: () => rootRoute,
    path: '/',
    component: AgentConnectCard,
  });
  const connectRoute = createRoute({
    getParentRoute: () => rootRoute,
    path: '/servers/how_to_connect',
    component: () => <div>connect modal</div>,
  });
  return createRouter({
    history: createMemoryHistory({ initialEntries: ['/'] }),
    routeTree: rootRoute.addChildren([cardRoute, connectRoute]),
  });
}

describe('AgentConnectCard', () => {
  it('renders the title', async () => {
    render(<RouterProvider router={makeRouter()} />);
    expect(await screen.findByText('Connect an MCP client to this Space')).toBeInTheDocument();
  });

  it('renders the korat connect command', async () => {
    render(<RouterProvider router={makeRouter()} />);
    expect(await screen.findByText('korat connect --space --bridge')).toBeInTheDocument();
  });

  it('renders the Details link pointing to /servers/how_to_connect', async () => {
    render(<RouterProvider router={makeRouter()} />);
    const link = await screen.findByRole('link', { name: /details/i });
    expect(link).toBeInTheDocument();
    expect(link.getAttribute('href')).toBe('/servers/how_to_connect');
  });

  it('clicking the copy button calls navigator.clipboard.writeText with the command', async () => {
    const writeText = vi.fn().mockResolvedValue(undefined);
    Object.assign(navigator, { clipboard: { writeText } });

    render(<RouterProvider router={makeRouter()} />);
    const copyButton = await screen.findByRole('button', { name: /copy command/i });
    await userEvent.click(copyButton);

    expect(writeText).toHaveBeenCalledWith('korat connect --space --bridge');
  });
});
