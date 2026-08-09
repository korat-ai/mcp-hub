import type { LucideIcon } from 'lucide-react';

interface Props {
  icon: LucideIcon;
  title: string;
  hint?: string;
}

export function EmptyState({ icon: Icon, title, hint }: Props) {
  return (
    <div className="flex flex-col items-center justify-center text-center py-16 gap-3">
      <div className="size-12 rounded-full bg-muted flex items-center justify-center text-muted-foreground">
        <Icon className="size-5" aria-hidden="true" />
      </div>
      <h4 className="text-sm font-semibold">{title}</h4>
      {hint && <p className="text-xs text-muted-foreground max-w-xs">{hint}</p>}
    </div>
  );
}
