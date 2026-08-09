import type { ReactNode } from 'react';
import { Link, useRouterState } from '@tanstack/react-router';
import { cn } from '@/lib/utils';

const ITEMS = [
  { to: '/grants', label: 'Permissions' },
  { to: '/connected-apps', label: 'Connected apps' },
] as const;

/** Shared navigation for the owner-facing access-control area. */
export function AccessSection({ children }: { children: ReactNode }) {
  const pathname = useRouterState({ select: (state) => state.location.pathname });

  return (
    <div className="flex flex-col gap-4">
      <nav aria-label="Access" className="flex gap-1 border-b border-border/60">
        {ITEMS.map((item) => {
          const active = pathname.startsWith(item.to);
          return (
            <Link
              key={item.to}
              to={item.to}
              aria-current={active ? 'page' : undefined}
              className={cn(
                'px-3 py-2 text-sm font-medium no-underline hover:no-underline border-b-2 -mb-px transition-colors',
                active
                  ? 'border-primary text-foreground'
                  : 'border-transparent text-muted-foreground hover:text-foreground',
              )}
            >
              {item.label}
            </Link>
          );
        })}
      </nav>
      {children}
    </div>
  );
}
