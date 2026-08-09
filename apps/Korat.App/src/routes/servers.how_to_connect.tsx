import { createFileRoute, useNavigate } from '@tanstack/react-router';
import {
  Dialog,
  DialogContent,
  DialogHeader,
  DialogTitle,
} from '@/components/ui/dialog';
import { AgentConnectSteps } from '@/components/domain/AgentConnectSteps';

export const Route = createFileRoute('/servers/how_to_connect')({
  component: ConnectAgentModal,
});

/** Exported for test harness. */
export { ConnectAgentModal };

function ConnectAgentModal() {
  const navigate = useNavigate();

  function handleClose(open: boolean) {
    if (!open) {
      void navigate({ to: '/servers' });
    }
  }

  return (
    <Dialog open onOpenChange={handleClose}>
      <DialogContent className="sm:max-w-lg">
        <DialogHeader>
          <DialogTitle>Connect an MCP client to your Space</DialogTitle>
        </DialogHeader>
        <div className="mt-1">
          <AgentConnectSteps />
        </div>
      </DialogContent>
    </Dialog>
  );
}
