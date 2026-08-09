/**
 * ConfirmRevokeDialog — generic confirm gate for destructive revoke actions.
 *
 * Props:
 *  - open / onOpenChange: controlled open state (caller manages)
 *  - title: dialog heading
 *  - body: description / warning copy
 *  - onConfirm: called when the user confirms
 *  - pending: when true the Confirm button is disabled (no double-submit)
 *  - error: optional error message displayed inside the dialog; dialog stays open on error
 *  - confirmLabel: optional label for the confirm button (default: "Revoke session")
 *
 * Cancel always stays enabled so the user can back out of a stuck mutation.
 */
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle,
} from '@/components/ui/dialog';
import { Button } from '@/components/ui/button';

interface Props {
  open: boolean;
  onOpenChange: (open: boolean) => void;
  title: string;
  body: string;
  onConfirm: () => void;
  pending: boolean;
  /** When set, renders an inline error message and keeps the dialog open. */
  error?: string | null;
  /** Label for the destructive confirm button. Defaults to "Revoke session". */
  confirmLabel?: string;
}

export function ConfirmRevokeDialog({
  open,
  onOpenChange,
  title,
  body,
  onConfirm,
  pending,
  error,
  confirmLabel = 'Revoke session',
}: Props) {
  return (
    <Dialog open={open} onOpenChange={onOpenChange}>
      <DialogContent>
        <DialogHeader>
          <DialogTitle>{title}</DialogTitle>
          <DialogDescription>{body}</DialogDescription>
        </DialogHeader>
        {error && (
          <p role="alert" className="text-xs text-destructive px-1">
            {error}
          </p>
        )}
        <DialogFooter>
          <Button
            variant="outline"
            onClick={() => onOpenChange(false)}
          >
            Cancel
          </Button>
          <Button
            variant="destructive"
            onClick={onConfirm}
            disabled={pending}
          >
            {confirmLabel}
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  );
}
