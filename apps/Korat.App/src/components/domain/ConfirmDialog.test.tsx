import { describe, expect, it, vi } from 'vitest';
import { render, screen } from '@testing-library/react';
import { ConfirmDialog } from './ConfirmDialog';

describe('ConfirmDialog', () => {
  it('renders the title, description, and confirm/cancel buttons', () => {
    render(
      <ConfirmDialog
        open
        onOpenChange={vi.fn()}
        title="Delete thing?"
        description="This cannot be undone."
        confirmLabel="Delete"
        onConfirm={vi.fn()}
      />,
    );
    expect(screen.getByText('Delete thing?')).toBeInTheDocument();
    expect(screen.getByText('This cannot be undone.')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Delete' })).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Cancel' })).toBeInTheDocument();
  });

  it('renders no extra content when children is omitted (regression: existing callers unaffected)', () => {
    render(
      <ConfirmDialog
        open
        onOpenChange={vi.fn()}
        title="Delete thing?"
        confirmLabel="Delete"
        onConfirm={vi.fn()}
      />,
    );
    expect(screen.queryByTestId('confirm-dialog-children')).toBeNull();
  });

  it('renders a children/body slot under the description', () => {
    render(
      <ConfirmDialog
        open
        onOpenChange={vi.fn()}
        title="Delete agent?"
        description="This permanently deletes the agent."
        confirmLabel="Delete"
        onConfirm={vi.fn()}
      >
        <p data-testid="confirm-dialog-children">Warning: 2 bound channels.</p>
      </ConfirmDialog>,
    );
    expect(screen.getByText('Delete agent?')).toBeInTheDocument();
    expect(screen.getByText('This permanently deletes the agent.')).toBeInTheDocument();
    expect(screen.getByTestId('confirm-dialog-children')).toHaveTextContent(
      'Warning: 2 bound channels.',
    );
  });

  it('renders the children slot even without a description', () => {
    render(
      <ConfirmDialog
        open
        onOpenChange={vi.fn()}
        title="Delete point?"
        confirmLabel="Delete"
        onConfirm={vi.fn()}
      >
        <p data-testid="confirm-dialog-children">Warning body.</p>
      </ConfirmDialog>,
    );
    expect(screen.getByTestId('confirm-dialog-children')).toBeInTheDocument();
  });
});
