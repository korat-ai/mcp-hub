/**
 * MobileTabBar — fixed bottom tab bar, visible only at <720px.
 *
 * 5 relay-focused tabs: Overview / Servers / Access / Activity / Runtimes.
 * Overview carries an amber count badge when there are pending access requests.
 */
import { LayoutGrid, Cpu, Server, Shield, Activity } from 'lucide-react';
import { Link, useRouterState } from '@tanstack/react-router';
import { cn } from '@/lib/utils';
import { useSpace } from '@/hooks/useSpace';
import { Badge } from '@/components/ui/badge';

const TAB_ITEMS = [
  { to: '/',         label: 'Overview', icon: LayoutGrid },
  { to: '/servers',  label: 'Servers',  icon: Server },
  { to: '/grants',   label: 'Access',   icon: Shield },
  { to: '/sessions', label: 'Activity', icon: Activity },
  { to: '/nodes',    label: 'Runtimes', icon: Cpu },
] as const;

function PendingBadge() {
  const { data } = useSpace();
  const count = data?.pendingAccessRequests?.length ?? 0;
  if (count === 0) return null;
  return (
    <Badge
      className="absolute -top-1 -right-1 h-4 min-w-4 px-1 text-[10px] font-bold leading-none"
      aria-label={`${count} pending access requests`}
    >
      {count}
    </Badge>
  );
}

export function MobileTabBar() {
  const pathname = useRouterState({ select: (s) => s.location.pathname });

  return (
    <nav
      aria-label="Mobile navigation"
      // Visible only below 720px. Matches the spec breakpoint exactly.
      className="fixed bottom-0 inset-x-0 z-50 min-[720px]:hidden bg-card border-t border-border/40"
    >
      <div className="flex items-stretch h-16 pb-[env(safe-area-inset-bottom)]">
        {TAB_ITEMS.map(({ to, label, icon: Icon }) => {
          const isActive =
            to === '/'
              ? pathname === '/'
              : pathname.startsWith(to);
          return (
            <Link
              key={to}
              to={to}
              className={cn(
                'relative flex flex-1 flex-col items-center justify-center gap-0.5 text-[10px] font-medium transition-colors no-underline hover:no-underline',
                isActive ? 'text-primary' : 'text-muted-foreground hover:text-foreground',
              )}
              aria-current={isActive ? 'page' : undefined}
            >
              <span className="relative">
                <Icon className="size-5" aria-hidden="true" />
                {to === '/' && <PendingBadge />}
              </span>
              <span>{label}</span>
            </Link>
          );
        })}
      </div>
    </nav>
  );
}
