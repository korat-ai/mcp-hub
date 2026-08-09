import { type ReactNode } from 'react';
import { CommandLine } from '@/components/domain/CommandLine';

export { CommandLine };

export function Step({ n, title, children }: { n: number; title: string; children: ReactNode }) {
  return (
    <li className="flex gap-3">
      <span className="flex size-6 shrink-0 items-center justify-center rounded-full bg-primary/10 text-xs font-semibold tabular-nums text-primary">
        {n}
      </span>
      <div className="flex-1 space-y-1.5">
        <div className="text-sm font-medium">{title}</div>
        {children}
      </div>
    </li>
  );
}

/**
 * Presentational list of MCP-server publishing steps.
 * Used by the "Add MCP server" modal on the Servers page.
 */
export function McpServerSetupSteps() {
  const serviceInstall = 'korat service install';
  const mcpAdd = 'korat mcp add <name> --command "..."';
  const mcpList = 'korat mcp list';

  return (
    <ol className="flex flex-col gap-4">
      <Step n={1} title="Ensure the publisher runtime is running">
        <CommandLine command={serviceInstall} />
        <p className="text-xs text-muted-foreground">
          Installs and starts an always-on publisher runtime (launchd on macOS, systemd --user on
          Linux). If it is already running, this command is a no-op. Check status with{' '}
          <code className="font-mono">korat service status</code>.
        </p>
      </Step>

      <Step n={2} title="Publish an MCP server">
        <CommandLine command={mcpAdd} />
        <p className="text-xs text-muted-foreground">
          Replace <code className="font-mono">&lt;name&gt;</code> with a short identifier and{' '}
          <code className="font-mono">--command</code> with the command that starts your MCP
          server (e.g.{' '}
          <code className="font-mono">npx -y @modelcontextprotocol/server-filesystem ~/docs</code>
          ). The running service serves it automatically — no restart needed.
        </p>
      </Step>

      <Step n={3} title="Verify it's live">
        <CommandLine command={mcpList} />
        <p className="text-xs text-muted-foreground">
          Shows each server with a local (<span aria-hidden>💻</span>) and cloud (
          <span aria-hidden>☁️</span>) status. You want{' '}
          <code className="font-mono">💻:✅ ☁️:✅</code> — served by this machine's daemon and
          available through the hub. <code className="font-mono">💻:⏸</code> means the daemon
          isn't running; <code className="font-mono">☁️:💤</code> means the cloud sees it but the
          publisher is offline.
        </p>
      </Step>
    </ol>
  );
}
