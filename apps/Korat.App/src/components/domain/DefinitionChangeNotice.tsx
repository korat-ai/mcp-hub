import { AlertTriangle } from 'lucide-react';
import type { DefinitionChangeDto } from '@/types/api';

interface Props {
  change: DefinitionChangeDto;
}

/**
 * Р27: the diff an owner must see before re-approving a server whose launch definition changed.
 *
 * A permission in Korat is bound to a server's definition, not just its id (Р26). When a
 * re-publish changes the command behind an approved name, permissions are suspended and the
 * consumer's next attempt raises a fresh access request. That request looks exactly like a
 * first-time one — same consumer, same server name — which is precisely the problem: the
 * dangerous case and the routine case are visually identical.
 *
 * So this component does not say "the definition changed". It shows what it was and what it is
 * now, because the decision the owner is making is a comparison, and a notice without the
 * comparison trains people to click through the one screen that was supposed to stop them.
 */
export function DefinitionChangeNotice({ change }: Props) {
  const previous = [change.previousCommand, change.previousArguments]
    .filter(Boolean)
    .join(' ')
    .trim();
  const current = [change.currentCommand, change.currentArguments]
    .filter(Boolean)
    .join(' ')
    .trim();

  return (
    <div
      className="rounded-md border border-destructive/40 bg-destructive/5 px-3 py-2 text-xs"
      data-testid="definition-change-notice"
    >
      <div className="flex items-center gap-1.5 font-semibold text-destructive">
        <AlertTriangle className="size-3.5 shrink-0" aria-hidden="true" />
        This server's launch command changed since it was last approved
      </div>
      <dl className="mt-1.5 space-y-1">
        <div className="flex gap-2">
          <dt className="w-16 shrink-0 text-muted-foreground">Was</dt>
          <dd className="font-mono break-all line-through opacity-70">{previous || '—'}</dd>
        </div>
        <div className="flex gap-2">
          <dt className="w-16 shrink-0 text-muted-foreground">Now</dt>
          <dd className="font-mono break-all">{current || '—'}</dd>
        </div>
      </dl>
      <p className="mt-1.5 text-muted-foreground">
        Approving grants access to the new command. Korat verifies the command, not the program it
        runs — if this machine is compromised, the command can be honest and the program not.
      </p>
    </div>
  );
}
