// DetailRow — a label/value line used inside a detail screen's summary card
// (host/status/last-seen, publisher/node/tools, etc). `value` accepts a node
// (e.g. a StatusBadge or EntityLink), not just text. Mirrors the -3
// prototype's DetailRow.
import type { ReactNode } from 'react';
import { cn } from '@/lib/utils';

interface Props {
  label: string;
  value: ReactNode;
  mono?: boolean;
}

export function DetailRow({ label, value, mono }: Props) {
  return (
    <div className="flex items-center justify-between gap-4 border-b border-border py-2.5 text-sm last:border-b-0">
      <span className="text-muted-foreground">{label}</span>
      <span className={cn(mono && 'font-mono text-xs text-foreground')}>{value}</span>
    </div>
  );
}
