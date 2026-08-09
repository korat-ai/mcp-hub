import { useCallback } from 'react';
import { Copy } from 'lucide-react';
import { Button } from '@/components/ui/button';
import { Step } from '@/components/domain/McpServerSetupSteps';
import { toastReceipt } from '@/lib/toast';

const MCP_JSON = `{
  "mcpServers": {
    "korat": {
      "command": "korat",
      "args": ["connect", "--space", "--bridge", "--agent", "my-client"]
    }
  }
}`;

function JsonBlock({ content }: { content: string }) {
  const copy = useCallback(() => {
    void navigator.clipboard?.writeText(content);
    toastReceipt('good', 'Copied', 'config');
  }, [content]);

  return (
    <div className="relative rounded-md border border-border bg-muted/40 px-3 py-2">
      <pre className="overflow-x-auto font-mono text-xs whitespace-pre">{content}</pre>
      <Button
        type="button"
        variant="ghost"
        size="icon-xs"
        aria-label="Copy config"
        onClick={copy}
        className="absolute right-1 top-1"
      >
        <Copy />
      </Button>
    </div>
  );
}

/**
 * Presentational list of steps for connecting an MCP client to the whole Space.
 * Used by the "Connect an MCP client to your Space" modal on the Servers page.
 */
export function AgentConnectSteps() {
  return (
    <ol className="flex flex-col gap-4">
      <Step n={1} title="Add one entry to your MCP client">
        <JsonBlock content={MCP_JSON} />
        <p className="text-xs text-muted-foreground">
          Replace <code className="font-mono">"my-client"</code> with a stable name for this MCP
          client. Its permissions persist across runs; use a different name for each client.
        </p>
      </Step>

      <Step n={2} title="Permitted servers appear automatically">
        <p className="text-xs text-muted-foreground">
          Each permitted server shows up as{' '}
          <code className="font-mono">&lt;server&gt;__&lt;tool&gt;</code>. Newly approved servers
          appear without a restart — no reconfig needed.
        </p>
      </Step>

      <Step n={3} title="Request access to more">
        <p className="text-xs text-muted-foreground">
          For a server this consumer is not yet permitted to use, call the{' '}
          <code className="font-mono">request-access__&lt;server&gt;</code> tool. The Space owner
          approves it here in the console, and its tools appear automatically.
        </p>
      </Step>
    </ol>
  );
}
