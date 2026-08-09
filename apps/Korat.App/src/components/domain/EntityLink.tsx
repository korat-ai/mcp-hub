import { Link, type LinkProps } from '@tanstack/react-router';
import {
  Tooltip,
  TooltipContent,
  TooltipProvider,
  TooltipTrigger,
} from '@/components/ui/tooltip';
import { shortId } from '@/lib/format';
import { cn } from '@/lib/utils';

export type EntityLinkProps = {
  /** Friendly display name; falls back to shortId(rawId) when absent. */
  name?: string;
  /** Raw id shown in tooltip. */
  rawId: string;
  /** Target route. Omit to render a non-navigating tooltip span (e.g. sessions). */
  to?: LinkProps['to'];
  /** Path params for `to` routes with a dynamic segment (e.g. /servers/$serverId). */
  // eslint-disable-next-line @typescript-eslint/no-explicit-any
  params?: any;
  // eslint-disable-next-line @typescript-eslint/no-explicit-any
  search?: any;
  className?: string;
};

export function EntityLink({ name, rawId, to, params, search, className }: EntityLinkProps) {
  const display = name ?? shortId(rawId);
  const inner = to ? (
    <Link
      to={to}
      params={params}
      search={search}
      className={cn(
        'font-medium underline-offset-4 hover:underline focus-visible:underline focus-visible:outline-none',
        className,
      )}
    >
      {display}
    </Link>
  ) : (
    <span className={cn('cursor-default font-medium', className)}>{display}</span>
  );
  return (
    <TooltipProvider>
      <Tooltip>
        <TooltipTrigger asChild>{inner}</TooltipTrigger>
        <TooltipContent>
          <span className="font-mono text-xs">{rawId}</span>
        </TooltipContent>
      </Tooltip>
    </TooltipProvider>
  );
}
