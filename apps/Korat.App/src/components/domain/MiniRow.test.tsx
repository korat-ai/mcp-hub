import { describe, expect, it, vi } from 'vitest';
import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MiniRow } from './MiniRow';

describe('MiniRow — informational (no onClick)', () => {
  it('renders children without a button role', () => {
    render(<MiniRow>plain content</MiniRow>);
    expect(screen.getByText('plain content')).toBeInTheDocument();
    expect(screen.queryByRole('button')).toBeNull();
  });
});

describe('MiniRow — interactive (with onClick)', () => {
  it('exposes a button role and calls onClick when clicked', async () => {
    const onClick = vi.fn();
    render(<MiniRow onClick={onClick}>filesystem</MiniRow>);
    const row = screen.getByRole('button');
    await userEvent.click(row);
    expect(onClick).toHaveBeenCalledTimes(1);
  });

  it('calls onClick on Enter key', async () => {
    const onClick = vi.fn();
    render(<MiniRow onClick={onClick}>filesystem</MiniRow>);
    const row = screen.getByRole('button');
    row.focus();
    await userEvent.keyboard('{Enter}');
    expect(onClick).toHaveBeenCalledTimes(1);
  });
});
