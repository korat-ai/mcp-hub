import { createFileRoute, useNavigate } from '@tanstack/react-router';
import {
  Dialog,
  DialogContent,
  DialogHeader,
  DialogTitle,
} from '@/components/ui/dialog';
import { NodeSetupSteps } from '@/components/domain/NodeSetupSteps';

export const Route = createFileRoute('/nodes/how_to_add')({
  component: AddNodeModal,
});

/** Exported for test harness. */
export { AddNodeModal };

function AddNodeModal() {
  const navigate = useNavigate();

  function handleClose(open: boolean) {
    if (!open) {
      void navigate({ to: '/nodes' });
    }
  }

  return (
    <Dialog open onOpenChange={handleClose}>
      <DialogContent className="sm:max-w-lg">
        <DialogHeader>
          <DialogTitle>Add a publisher runtime</DialogTitle>
        </DialogHeader>
        <div className="mt-1">
          <NodeSetupSteps cloudOrigin={window.location.origin} />
        </div>
      </DialogContent>
    </Dialog>
  );
}
