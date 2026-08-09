import { Terminal } from 'lucide-react';
import { Card } from '@/components/ui/card';
import { NodeSetupSteps } from '@/components/domain/NodeSetupSteps';

/**
 * Shown on the Overview when the Space is brand-new (no publisher runtimes, no MCP servers, no
 * pending requests). Replaces the premature "No pending access requests" empty state
 * with the concrete next step: install the CLI, sign in, bring a runtime online.
 *
 * `cloudOrigin` is pinned into `korat login --cloud <origin>` so the command targets
 * THIS console's cloud (e.g. my.korat.dev vs my.korat.ai) rather than the CLI's default
 * host — a copy-paste that always lands in the right Space.
 */
export function OnboardingEmptyState({ cloudOrigin }: { cloudOrigin: string }) {
  return (
    <Card className="p-6 md:p-8">
      <div className="flex items-center gap-2 text-primary">
        <Terminal className="size-5" />
        <h2 className="text-base font-semibold">Connect your first runtime</h2>
      </div>
      <p className="mt-1.5 max-w-prose text-sm text-muted-foreground">
        Your Space is empty. Bring a machine online as a Korat publisher runtime — the MCP servers it
        serves and any access requests will then appear here.
      </p>

      <div className="mt-5">
        <NodeSetupSteps cloudOrigin={cloudOrigin} />
      </div>
    </Card>
  );
}
