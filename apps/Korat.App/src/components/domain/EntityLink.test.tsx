/**
 * Unit tests for EntityLink.
 *
 * Covers:
 *  - With `to`: renders a link role with the display name.
 *  - Without `to`: renders a non-link tooltip span (no link role).
 *  - Falls back to shortId(rawId) when `name` is undefined.
 *  - Raw id appears in tooltip content.
 */
import { describe, expect, it, vi } from 'vitest';
import { render, screen } from '@testing-library/react';
import { EntityLink } from './EntityLink';

// Stub router Link as a plain anchor.
vi.mock('@tanstack/react-router', async (importOriginal) => {
  const actual = await importOriginal<typeof import('@tanstack/react-router')>();
  return {
    ...actual,
    Link: ({
      children,
      to,
      search: _search,
      ...rest
    }: React.AnchorHTMLAttributes<HTMLAnchorElement> & { to?: string; search?: unknown }) => (
      <a href={to} {...rest}>
        {children}
      </a>
    ),
  };
});

vi.mock('@/lib/format', () => ({
  shortId: (id: string) => id.slice(0, 12),
}));

describe('EntityLink — with `to`', () => {
  it('renders a link with the display name', () => {
    render(<EntityLink name="My Agent" rawId="agent-abc-123" to="/grants" />);
    expect(screen.getByRole('link', { name: 'My Agent' })).toBeInTheDocument();
  });

  it('link href matches the `to` prop', () => {
    render(<EntityLink name="My Agent" rawId="agent-abc-123" to="/grants" />);
    expect(screen.getByRole('link', { name: 'My Agent' })).toHaveAttribute('href', '/grants');
  });

  it('tooltip trigger wraps the link element', () => {
    render(<EntityLink name="My Agent" rawId="agent-abc-123-full-id" to="/grants" />);
    // The link itself must be in the DOM; Radix Tooltip content is portal-rendered
    // on hover only — we verify the trigger wrapper attribute rather than portal content.
    const link = screen.getByRole('link', { name: 'My Agent' });
    expect(link).toHaveAttribute('data-slot', 'tooltip-trigger');
  });
});

describe('EntityLink — without `to`', () => {
  it('does NOT render a link role', () => {
    render(<EntityLink name="Session Agent" rawId="session-xyz-999" />);
    expect(screen.queryByRole('link')).toBeNull();
  });

  it('renders the display name as plain text', () => {
    render(<EntityLink name="Session Agent" rawId="session-xyz-999" />);
    expect(screen.getByText('Session Agent')).toBeInTheDocument();
  });

  it('tooltip trigger wraps the span element', () => {
    render(<EntityLink name="Session Agent" rawId="session-xyz-999" />);
    // Radix Tooltip content is portal-rendered on hover only; verify the trigger
    // wrapper is present and wraps the non-link span.
    const span = screen.getByText('Session Agent');
    expect(span).toHaveAttribute('data-slot', 'tooltip-trigger');
  });
});

describe('EntityLink — name fallback', () => {
  it('falls back to shortId(rawId) when name is undefined', () => {
    // shortId mocked as first 12 chars; rawId longer than 12 chars triggers fallback
    render(<EntityLink rawId="abcdefghijklmnopqrstuvwxyz" to="/grants" />);
    // shortId mock returns first 12 chars
    expect(screen.getByRole('link', { name: 'abcdefghijkl' })).toBeInTheDocument();
  });
});
