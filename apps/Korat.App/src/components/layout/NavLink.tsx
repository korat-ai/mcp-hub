import { Link } from '@tanstack/react-router';
import type { LucideIcon } from 'lucide-react';
import { cn } from '@/lib/utils';
import { Badge } from '@/components/ui/badge';

interface NavLinkProps {
  to: string;
  label: string;
  icon: LucideIcon;
  /**
   * Optional pending-count badge (e.g. pending access requests on the Space
   * item) — mirrors the design ref's rail badge (shell.jsx NavLink) and the
   * existing MobileTabBar badge. Omitted or 0 renders nothing.
   */
  badge?: number;
}

export function NavLink({ to, label, icon: Icon, badge }: NavLinkProps) {
  return (
    <Link
      to={to}
      activeOptions={{ exact: true }}
      className={cn(
        'group flex items-center gap-3 rounded-md px-3 py-2 text-sm',
        'text-muted-foreground hover:bg-muted hover:text-foreground transition-colors',
      )}
      activeProps={{
        'aria-current': 'page',
        className: cn(
          'relative bg-muted text-foreground',
          'before:absolute before:left-0 before:top-1/2 before:-translate-y-1/2',
          'before:h-5 before:w-[3px] before:bg-primary before:rounded-r',
        ),
      }}
    >
      <Icon className="size-4 shrink-0" aria-hidden="true" />
      <span className="flex-1">{label}</span>
      {typeof badge === 'number' && badge > 0 && (
        <Badge
          className="h-[18px] min-w-[18px] shrink-0 px-1 font-mono text-[10px] font-semibold leading-none"
          aria-label={`${badge} pending`}
        >
          {badge}
        </Badge>
      )}
    </Link>
  );
}
