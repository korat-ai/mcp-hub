// MiniSection — a labelled sub-block inside a detail screen (icon + eyebrow
// title + count), used to group a related-entity list (servers/sessions/
// inference points on a node or server). Mirrors the -3 prototype's
// MiniSection; the actual list markup (usually a Card of MiniRows) is
// supplied by the caller as children so this stays a pure layout wrapper.
import type { ReactNode } from 'react';
import type { LucideIcon } from 'lucide-react';

interface Props {
  icon: LucideIcon;
  title: string;
  count: number;
  children: ReactNode;
}

export function MiniSection({ icon: Icon, title, count, children }: Props) {
  return (
    <div className="flex flex-col gap-2">
      <div className="flex items-center gap-1.5 eyebrow">
        <Icon className="size-3.5" aria-hidden="true" />
        <span>{title}</span>
        <span className="text-muted-foreground/60">· {count}</span>
      </div>
      {children}
    </div>
  );
}
