/**
 * Controlled confirm dialog for destructive actions.
 *
 * Callers should treat onOpenChange(false) as an abort signal — Cancel
 * stays enabled even during isPending so users can always back out of a
 * stuck mutation. Wire AbortController.signal into your useMutation and
 * call abort() in onOpenChange(false) if you need true cancellation.
 */
import type { ReactNode } from 'react';
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
  description?: string;
  confirmLabel: string;
  destructive?: boolean;
  onConfirm: () => void;
  isPending?: boolean;
  /** Optional extra content rendered under the description — e.g. a warning block about
   *  dependents (bound channels, agents using this point as their brain). */
  children?: ReactNode;
}

export function ConfirmDialog({
  open,
  onOpenChange,
  title,
  description,
  confirmLabel,
  destructive,
  onConfirm,
  isPending,
  children,
}: Props) {
  return (
    <Dialog open={open} onOpenChange={onOpenChange}>
      <DialogContent className="rounded-2xl">
        <DialogHeader>
          <DialogTitle>{title}</DialogTitle>
          {description && <DialogDescription>{description}</DialogDescription>}
        </DialogHeader>
        {children}
        <DialogFooter>
          <Button
            variant="outline"
            onClick={() => onOpenChange(false)}
            // Cancel stays enabled — user can always back out of a stuck mutation.
            // Callers reacting to a long-running confirm should treat
            // onOpenChange(false) as an abort signal for their useMutation.
          >
            Cancel
          </Button>
          <Button
            variant={destructive ? 'destructive' : 'default'}
            onClick={onConfirm}
            disabled={isPending}
          >
            {confirmLabel}
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  );
}
