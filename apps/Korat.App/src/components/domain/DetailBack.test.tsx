/**
 * Unit tests for DetailBack.
 * Router `Link` is stubbed as a plain anchor (same technique as EntityLink.test.tsx).
 */
import { describe, expect, it, vi } from 'vitest';
import { render, screen } from '@testing-library/react';
import { DetailBack } from './DetailBack';

vi.mock('@tanstack/react-router', async (importOriginal) => {
  const actual = await importOriginal<typeof import('@tanstack/react-router')>();
  return {
    ...actual,
    Link: ({ children, to, ...rest }: React.AnchorHTMLAttributes<HTMLAnchorElement> & { to?: string }) => (
      <a href={to} {...rest}>
        {children}
      </a>
    ),
  };
});

describe('DetailBack', () => {
  it('renders the label as a link', () => {
    render(<DetailBack to="/nodes" label="Runtimes" />);
    expect(screen.getByRole('link', { name: /Runtimes/ })).toBeInTheDocument();
  });

  it('links to the given `to` path', () => {
    render(<DetailBack to="/nodes" label="Runtimes" />);
    expect(screen.getByRole('link', { name: /Runtimes/ })).toHaveAttribute('href', '/nodes');
  });
});
