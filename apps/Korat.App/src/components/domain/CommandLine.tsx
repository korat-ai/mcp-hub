import { useCallback } from 'react';
import { Copy } from 'lucide-react';
import { Button } from '@/components/ui/button';
import { toastReceipt } from '@/lib/toast';

/**
 * Mono command display with a copy button.
 * Renders a `$` prefix, the command in monospace, and a clipboard copy action.
 */
export function CommandLine({ command }: { command: string }) {
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
