import { X } from 'lucide-react';
import { Badge } from '@/components/ui/badge';
import { Button } from '@/components/ui/button';

export function ActiveFilterChip({
  label,
  dimension,
  onClear,
}: {
  label: string;
  /** Optional dimension being filtered on (e.g. 'Consumer' | 'Server' | 'Runtime'),
   *  rendered as an uppercase eyebrow label ahead of the value — prototype
   *  FilterChip (screens.jsx:110-124). Back-compat: omit to keep the generic
   *  "Filtered by <value>" rendering. */
  dimension?: string;
  onClear: () => void;
}) {
  return (
    <div className="flex items-center" data-testid="active-filter-chip">
      <Badge variant="outline" className="gap-1.5 font-normal">
        {dimension ? (
          <span className="eyebrow">{dimension}</span>
        ) : (
          <span className="text-muted-foreground">Filtered by</span>
        )}
        <span className="font-medium">{label}</span>
        <Button
          variant="ghost"
          size="xs"
          className="ml-1 -mr-1 h-4 px-1"
          aria-label="Clear filter"
          onClick={onClear}
        >
          <X className="size-3" /> clear
        </Button>
      </Badge>
    </div>
  );
}
