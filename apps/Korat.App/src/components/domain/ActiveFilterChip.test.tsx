/**
 * Unit tests for ActiveFilterChip.
 *
 * Covers:
 *  - Renders "Filtered by <label>" text.
 *  - Clear button has aria-label="Clear filter".
 *  - Clicking the clear button fires onClear.
 *  - data-testid="active-filter-chip" is present for test targeting.
 */
import { describe, expect, it, vi } from 'vitest';
import { render, screen, fireEvent } from '@testing-library/react';
import { ActiveFilterChip } from './ActiveFilterChip';

describe('ActiveFilterChip', () => {
  it('renders "Filtered by" label text', () => {
    render(<ActiveFilterChip label="my-server" onClear={vi.fn()} />);
    expect(screen.getByText('Filtered by')).toBeInTheDocument();
  });

  it('renders the filter label value', () => {
    render(<ActiveFilterChip label="my-server" onClear={vi.fn()} />);
    expect(screen.getByText('my-server')).toBeInTheDocument();
  });

  it('clear button has aria-label="Clear filter"', () => {
    render(<ActiveFilterChip label="my-server" onClear={vi.fn()} />);
    expect(screen.getByRole('button', { name: 'Clear filter' })).toBeInTheDocument();
  });

  it('clicking clear button fires onClear', () => {
    const onClear = vi.fn();
    render(<ActiveFilterChip label="my-server" onClear={onClear} />);
    fireEvent.click(screen.getByRole('button', { name: 'Clear filter' }));
    expect(onClear).toHaveBeenCalledTimes(1);
  });

  it('root element has data-testid="active-filter-chip"', () => {
    render(<ActiveFilterChip label="my-server" onClear={vi.fn()} />);
    expect(screen.getByTestId('active-filter-chip')).toBeInTheDocument();
  });
});
