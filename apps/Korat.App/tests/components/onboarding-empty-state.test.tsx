import { describe, it, expect, vi } from 'vitest';
import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { OnboardingEmptyState } from '@/components/domain/OnboardingEmptyState';

describe('OnboardingEmptyState', () => {
  it('pins a non-default cloud origin into the login command', () => {
    render(<OnboardingEmptyState cloudOrigin="https://my.korat.dev" />);

    // macOS tab is active by default — shows the curl installer.
    expect(screen.getByText(/get\.korat\.ai\/install\.sh/)).toBeInTheDocument();
    // Non-default cloud (dev) → the flag is required so login targets THIS console.
    expect(screen.getByText('korat login --cloud https://my.korat.dev')).toBeInTheDocument();
    expect(screen.getByText('korat service install')).toBeInTheDocument();
  });

  it('shows Windows installer and alpha notice when Windows tab is selected', async () => {
    render(<OnboardingEmptyState cloudOrigin="https://my.korat.dev" />);

    await userEvent.click(screen.getByRole('button', { name: /windows/i }));

    // Windows one-liner is now visible.
    expect(screen.getByText(/get\.korat\.ai\/install\.ps1/)).toBeInTheDocument();
    // Windows alpha caveat is scoped to the Windows tab.
    expect(screen.getByText(/Windows CLI is alpha/i)).toBeInTheDocument();
    // install.sh is no longer rendered.
    expect(screen.queryByText(/install\.sh/)).not.toBeInTheDocument();
  });

  it('omits the --cloud flag when the console IS the CLI default cloud', () => {
    render(<OnboardingEmptyState cloudOrigin="https://my.korat.ai" />);

    expect(screen.getByText('korat login')).toBeInTheDocument();
    expect(screen.queryByText(/--cloud/)).not.toBeInTheDocument();
  });

  it('copies the selected command to the clipboard', async () => {
    const writeText = vi.fn().mockResolvedValue(undefined);
    Object.assign(navigator, { clipboard: { writeText } });

    render(<OnboardingEmptyState cloudOrigin="https://my.korat.dev" />);
    const copyButtons = screen.getAllByRole('button', { name: /copy command/i });
    // Default macOS tab: 4 commands — install.sh, login, service install, mcp add
    expect(copyButtons).toHaveLength(4);

    await userEvent.click(copyButtons[1]); // the login command (after the installer)
    expect(writeText).toHaveBeenCalledWith('korat login --cloud https://my.korat.dev');
  });
});
