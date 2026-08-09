// MiniRow — a row inside a detail screen's MiniSection Card (div-based, not a
// table row). `onClick` makes it a keyboard-reachable cross-nav affordance
// (a trailing chevron is appended automatically, mirroring the -3
// prototype's chevronRight); omit it for a purely informational row (e.g.
// a session line that doesn't drill anywhere further).
import type { KeyboardEvent, ReactNode } from 'react';
import { ChevronRight } from 'lucide-react';
import { cn } from '@/lib/utils';

interface Props {
  children: ReactNode;
  onClick?: () => void;
  className?: string;
}

export function MiniRow({ children, onClick, className }: Props) {
  const interactive = Boolean(onClick);

  function handleKeyDown(e: KeyboardEvent<HTMLDivElement>) {
    if (!onClick) return;
    if (e.key === 'Enter' || e.key === ' ') {
      e.preventDefault();
      onClick();
    }
  }

  return (
    <div
      onClick={onClick}
      onKeyDown={handleKeyDown}
      tabIndex={interactive ? 0 : undefined}
      role={interactive ? 'button' : undefined}
      className={cn(
        'flex items-center gap-3 border-b border-border px-4 py-3 text-sm last:border-b-0',
        interactive && 'cursor-pointer transition-colors hover:bg-muted/40',
        className,
      )}
    >
      {children}
      {interactive && (
        <ChevronRight className="size-4 shrink-0 text-muted-foreground" aria-hidden="true" />
      )}
    </div>
  );
}
