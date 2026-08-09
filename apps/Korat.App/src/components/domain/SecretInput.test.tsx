/**
 * Unit tests for SecretInput (T15, PR #145 review).
 *
 * Covers:
 *  - type="password" by default (value masked).
 *  - the reveal toggle flips the input to type="text" and back to type="password".
 *  - autoComplete="new-password" is present (blocks password-manager autofill suggestions).
 *  - onChange fires with the typed value; the value prop is what's rendered (controlled input).
 */
import { describe, expect, it, vi } from 'vitest';
import { render, screen, fireEvent } from '@testing-library/react';
import { SecretInput } from './SecretInput';

describe('SecretInput', () => {
  it('renders as type="password" by default', () => {
    render(<SecretInput value="sk-abc123" onChange={vi.fn()} />);
    expect(screen.getByDisplayValue('sk-abc123')).toHaveAttribute('type', 'password');
  });

  it('has autoComplete="new-password"', () => {
    render(<SecretInput value="" onChange={vi.fn()} />);
    expect(screen.getByDisplayValue('')).toHaveAttribute('autocomplete', 'new-password');
  });

  it('the reveal toggle flips the input to type="text"', () => {
    render(<SecretInput value="sk-abc123" onChange={vi.fn()} />);
    fireEvent.click(screen.getByRole('button', { name: 'Reveal value' }));
    expect(screen.getByDisplayValue('sk-abc123')).toHaveAttribute('type', 'text');
  });

  it('the reveal toggle flips back to type="password" on a second click', () => {
    render(<SecretInput value="sk-abc123" onChange={vi.fn()} />);
    const toggle = screen.getByRole('button', { name: 'Reveal value' });
    fireEvent.click(toggle);
    fireEvent.click(screen.getByRole('button', { name: 'Hide value' }));
    expect(screen.getByDisplayValue('sk-abc123')).toHaveAttribute('type', 'password');
  });

  it('calls onChange with the typed value', () => {
    const onChange = vi.fn();
    render(<SecretInput value="" onChange={onChange} />);
    fireEvent.change(screen.getByDisplayValue(''), { target: { value: 'sk-new-value' } });
    expect(onChange).toHaveBeenCalledExactlyOnceWith('sk-new-value');
  });
});
