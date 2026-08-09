import { createFileRoute, useNavigate } from '@tanstack/react-router';
import {
  Dialog,
  DialogContent,
  DialogHeader,
  DialogTitle,
} from '@/components/ui/dialog';
import { McpServerSetupSteps } from '@/components/domain/McpServerSetupSteps';

export const Route = createFileRoute('/servers/how_to_add')({
  component: AddMcpServerModal,
});

/** Exported for test harness. */
export { AddMcpServerModal };

function AddMcpServerModal() {
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
          <DialogTitle>Add an MCP server</DialogTitle>
        </DialogHeader>
        <div className="mt-1">
          <McpServerSetupSteps />
        </div>
      </DialogContent>
    </Dialog>
  );
}
