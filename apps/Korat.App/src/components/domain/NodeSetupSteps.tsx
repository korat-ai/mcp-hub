import { useCallback, useState, type ReactNode } from 'react';
import { Copy } from 'lucide-react';
import { Button } from '@/components/ui/button';
import { toastReceipt } from '@/lib/toast';
import { cn } from '@/lib/utils';

function CommandLine({ command }: { command: string }) {
  const copy = useCallback(() => {
    void navigator.clipboard?.writeText(command);
    toastReceipt('good', 'Copied', 'command');
  }, [command]);

  return (
    <div className="flex items-center gap-2 rounded-md border border-border bg-muted/40 px-3 py-2 font-mono text-xs">
      <span aria-hidden className="select-none text-muted-foreground">$</span>
      <code className="flex-1 overflow-x-auto whitespace-pre">{command}</code>
      <Button type="button" variant="ghost" size="icon-xs" aria-label="Copy command" onClick={copy}>
        <Copy />
      </Button>
    </div>
  );
}

function Step({ n, title, children }: { n: number; title: string; children: ReactNode }) {
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

type OsTab = 'macos' | 'linux' | 'windows';

const OS_TABS: { id: OsTab; label: string }[] = [
  { id: 'macos', label: 'macOS' },
  { id: 'linux', label: 'Linux' },
  { id: 'windows', label: 'Windows' },
];

function OsTabs({ value, onChange }: { value: OsTab; onChange: (v: OsTab) => void }) {
  return (
    <div className="flex gap-1 rounded-md border border-border bg-muted/30 p-0.5 w-fit">
      {OS_TABS.map(({ id, label }) => (
        <button
          key={id}
          type="button"
          onClick={() => onChange(id)}
          className={cn(
            'rounded px-2.5 py-1 text-xs font-medium transition-colors',
            value === id
              ? 'bg-background text-foreground shadow-sm'
              : 'text-muted-foreground hover:text-foreground',
          )}
          aria-pressed={value === id}
        >
          {label}
        </button>
      ))}
    </div>
  );
}

// The CLI's built-in default for `korat login --cloud` (apps/Korat.Cli/Commands/LoginCommand.cs).
// When this console IS that cloud, the flag is redundant — render the bare `korat login`.
const DEFAULT_CLOUD = 'https://my.korat.ai';

/**
 * Presentational list of runtime-setup steps (install → login → service install → mcp add).
 * Used by OnboardingEmptyState and the "Add Runtime" modal.
 */
export function NodeSetupSteps({ cloudOrigin }: { cloudOrigin: string }) {
  const origin = cloudOrigin.replace(/\/+$/, '');
  const [os, setOs] = useState<OsTab>('macos');

  const installMac = 'curl -fsSL https://get.korat.ai/install.sh | sh';
  const installLinux = 'curl -fsSL https://get.korat.ai/install.sh | sh';
  const installWindows = 'irm https://get.korat.ai/install.ps1 | iex';
  const brewInstall = 'brew install korat-ai/tap/korat';

  const login = origin === DEFAULT_CLOUD ? 'korat login' : `korat login --cloud ${origin}`;
  const serviceInstall = 'korat service install';
  const mcpAdd = 'korat mcp add <name> --command "..."';

  return (
    <ol className="flex flex-col gap-4">
      <Step n={1} title="Install the CLI">
        <OsTabs value={os} onChange={setOs} />

        {os === 'macos' && (
          <>
            <CommandLine command={installMac} />
            <p className="text-xs text-muted-foreground">
              Or with Homebrew:{' '}
              <code className="font-mono">{brewInstall}</code>
            </p>
          </>
        )}

        {os === 'linux' && (
          <CommandLine command={installLinux} />
        )}

        {os === 'windows' && (
          <>
            <CommandLine command={installWindows} />
            <p className="text-xs text-primary/90">
              <span className="font-semibold">Windows CLI is alpha.</span> The native background
              service (Scheduled Task) and MCP spawning are built but still under active testing —
              expect rough edges and please report issues.
            </p>
          </>
        )}
      </Step>

      <Step n={2} title="Sign in (device-flow OAuth)">
        <CommandLine command={login} />
        <p className="text-xs text-muted-foreground">
          Opens your browser to approve this device; the token is saved to{' '}
          <code className="font-mono">~/.korat/credentials</code>.
        </p>
      </Step>

      <Step n={3} title="Start the background service">
        <CommandLine command={serviceInstall} />
        <p className="text-xs text-muted-foreground">
          Installs and starts an always-on publisher runtime (launchd on macOS, systemd --user on
          Linux, a per-user Scheduled Task on Windows) that auto-starts at login. Check status with{' '}
          <code className="font-mono">korat service status</code>.
        </p>
      </Step>

      <Step n={4} title="Publish an MCP server">
        <CommandLine command={mcpAdd} />
        <p className="text-xs text-muted-foreground">
          Registers the server locally; the running service serves it automatically. Add
          as many servers as you like — they all stay online without manual restarts.
        </p>
      </Step>
    </ol>
  );
}
