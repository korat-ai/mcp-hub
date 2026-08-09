import { AlertCircle } from 'lucide-react';
import { Button } from '@/components/ui/button';

interface Props {
  message: string;
  detail?: string;
  onRetry?: () => void;
}

export function ErrorState({ message, detail, onRetry }: Props) {
  return (
    <div className="flex flex-col items-center justify-center text-center py-16 gap-3" role="alert">
      <div className="size-12 rounded-full bg-destructive/15 flex items-center justify-center text-destructive">
        <AlertCircle className="size-5" aria-hidden="true" />
      </div>
      <h4 className="text-sm font-semibold">{message}</h4>
      {detail && <p className="text-xs font-mono text-muted-foreground">{detail}</p>}
      {onRetry && (
        <Button variant="outline" size="sm" onClick={onRetry} className="mt-2">
          Retry
        </Button>
      )}
    </div>
  );
}
