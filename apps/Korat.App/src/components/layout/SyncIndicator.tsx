import { useEffect, useState } from 'react';
import { useLastSynced } from '@/hooks/useLastSynced';
import { POLL_FRESH_THRESHOLD_MS } from '@/lib/polling';

function formatSecondsAgo(updatedAtMs: number | null, nowMs: number): string {
  if (!updatedAtMs) return 'syncing…';
  const sec = Math.max(0, Math.floor((nowMs - updatedAtMs) / 1000));
  if (sec < 5) return 'synced just now';
  return `synced ${sec}s ago`;
}

export function SyncIndicator() {
  const sync = useLastSynced();
  const [now, setNow] = useState(() => Date.now());

  useEffect(() => {
    const id = setInterval(() => setNow(Date.now()), 1000);
    return () => clearInterval(id);
  }, []);

  // Poll is failing — red "offline" pill instead of the dimming "synced Ns
  // ago" text (design ref shell.jsx:143-163, mode:'error').
  if (sync.status === 'error') {
    return (
      <span
        role="status"
        className="inline-flex items-center gap-1.5 h-[26px] rounded-full border border-destructive/30 px-2.5 text-xs font-mono text-destructive"
      >
        <span aria-hidden="true" className="inline-block size-1.5 rounded-full bg-destructive" />
        offline
      </span>
    );
  }

  // "Fresh" = within POLL_FRESH_THRESHOLD_MS of last update (5s polling tick + 1s render slack)
  const fresh = sync.status === 'live' && now - sync.updatedAt < POLL_FRESH_THRESHOLD_MS;

  return (
    <div className="flex items-center gap-2 text-xs font-mono text-muted-foreground">
      <span
        aria-hidden="true"
        className={
          'inline-block size-2 rounded-full transition-opacity ' +
          (fresh ? 'bg-primary animate-pulse' : 'bg-muted-foreground/40')
        }
      />
      <span>{formatSecondsAgo(sync.updatedAt, now)}</span>
    </div>
  );
}

/**
 * Compact status dot for the mobile top bar — just the coloured pulse dot,
 * no text, with an accessible label so screen readers know the sync state.
 */
export function SyncDot() {
  const sync = useLastSynced();
  const [now, setNow] = useState(() => Date.now());

  useEffect(() => {
    const id = setInterval(() => setNow(Date.now()), 1000);
    return () => clearInterval(id);
  }, []);

  if (sync.status === 'error') {
    return (
      <span
        role="status"
        aria-label="offline"
        title="offline"
        className="inline-block size-2 rounded-full bg-destructive"
      />
    );
  }

  const fresh = sync.status === 'live' && now - sync.updatedAt < POLL_FRESH_THRESHOLD_MS;
  const label = formatSecondsAgo(sync.updatedAt, now);

  return (
    <span
      role="status"
      aria-label={label}
      title={label}
      className={
        'inline-block size-2 rounded-full transition-opacity ' +
        (fresh ? 'bg-primary animate-pulse' : 'bg-muted-foreground/40')
      }
    />
  );
}
