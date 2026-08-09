// DetailBack — the "← Nodes" style back link at the top of a detail screen.
// Kept intentionally tiny (a Link, not a Button) so it reads as navigation,
// not an action — mirrors the -3 prototype's DetailBack.
import { Link, type LinkProps } from '@tanstack/react-router';
import { ArrowLeft } from 'lucide-react';

interface Props {
  to: LinkProps['to'];
  label: string;
}

export function DetailBack({ to, label }: Props) {
  return (
    <Link
      to={to}
      className="inline-flex w-fit items-center gap-1.5 text-sm text-muted-foreground transition-colors hover:text-foreground"
    >
      <ArrowLeft className="size-3.5" aria-hidden="true" />
      {label}
    </Link>
  );
}
