/**
 * /servers/new — AddHttpMcpServerModal. Renders as a MODAL OVERLAY (like /servers/how_to_add),
 * not inline page content: this route is a CHILD of /servers, so it renders into servers.tsx's
 * <Outlet/> which sits BELOW the server table — a plain page body there stacks under the table
 * instead of overlaying it. Wrap the form in the shared Dialog so it presents as a modal, matching
 * the sibling how_to_add route. (The static `new` segment still out-ranks the dynamic `$serverId`
 * in TanStack's route matching, so a random serverId can never collide with it — a rendering
 * concern, independent of the modal-vs-page decision.)
 *
 * Increment 1 (HTTP MCP direct-to-Space, Task 7): registers a cloud-hosted HTTP MCP server via
 * POST /api/mcp-servers — distinct from /servers/how_to_add (the modal showing `korat mcp add`
 * instructions for a local-node-published stdio server). See McpServerCreateForm.tsx for the form.
 */
import { createFileRoute, useNavigate } from '@tanstack/react-router';
import {
  Dialog,
  DialogContent,
  DialogHeader,
  DialogTitle,
} from '@/components/ui/dialog';
import { McpServerCreateForm } from '@/components/domain/McpServerCreateForm';

export const Route = createFileRoute('/servers/new')({
  component: AddHttpMcpServerModal,
});

/** Exported for test harness — allows mounting AddHttpMcpServerModal in a minimal router. */
export { AddHttpMcpServerModal };

function AddHttpMcpServerModal() {
  const navigate = useNavigate();

  function handleClose(open: boolean) {
    if (!open) void navigate({ to: '/servers' });
  }

  return (
    <Dialog open onOpenChange={handleClose}>
      <DialogContent className="sm:max-w-lg">
        <DialogHeader>
          <DialogTitle>Add HTTP MCP server</DialogTitle>
        </DialogHeader>
        <div className="mt-1">
          <McpServerCreateForm
            onCreated={(serverId) => void navigate({ to: '/servers/$serverId', params: { serverId } })}
          />
        </div>
      </DialogContent>
    </Dialog>
  );
}
