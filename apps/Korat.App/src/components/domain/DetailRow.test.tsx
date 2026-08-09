import { describe, expect, it } from 'vitest';
import { render, screen } from '@testing-library/react';
import { DetailRow } from './DetailRow';

describe('DetailRow', () => {
  it('renders the label and value', () => {
    render(<DetailRow label="Host" value="node-aaa" />);
    expect(screen.getByText('Host')).toBeInTheDocument();
    expect(screen.getByText('node-aaa')).toBeInTheDocument();
  });

  it('accepts a node as `value` (e.g. a badge)', () => {
    render(<DetailRow label="Status" value={<span data-testid="badge">Online</span>} />);
    expect(screen.getByTestId('badge')).toBeInTheDocument();
  });
});
