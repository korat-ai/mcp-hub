import { describe, expect, it } from 'vitest';
import { render, screen } from '@testing-library/react';
import { Server } from 'lucide-react';
import { MiniSection } from './MiniSection';

describe('MiniSection', () => {
  it('renders the title and count', () => {
    render(
      <MiniSection icon={Server} title="MCP servers published" count={3}>
        <div>rows</div>
      </MiniSection>,
    );
    expect(screen.getByText('MCP servers published')).toBeInTheDocument();
    expect(screen.getByText('· 3')).toBeInTheDocument();
  });

  it('renders its children', () => {
    render(
      <MiniSection icon={Server} title="Sessions" count={0}>
        <div>No sessions on this runtime.</div>
      </MiniSection>,
    );
    expect(screen.getByText('No sessions on this runtime.')).toBeInTheDocument();
  });
});
