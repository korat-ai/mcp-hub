import type { ReactNode } from 'react';
import { useQuery } from '@tanstack/react-query';
import { api } from '@/lib/api';
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogHeader,
  DialogTitle,
  DialogTrigger,
} from '@/components/ui/dialog';

// Injected at build time by vite.config.ts (git short sha + ISO build time).
const COMMIT = (import.meta.env.VITE_COMMIT_SHA as string | undefined) ?? 'dev';
const BUILD_TIME = (import.meta.env.VITE_BUILD_TIME as string | undefined) ?? null;
const REPO_URL = 'https://github.com/korat-ai/mcp-hub';

function formatTime(iso: string | null): string {
  if (!iso) return '—';
  const d = new Date(iso);
  return Number.isNaN(d.getTime()) ? iso : d.toLocaleString();
}

/** "registry.fly.io/korat-dev:deployment-01KT…" → "deployment-01KT…" */
function shortRelease(imageRef: string): string {
  const idx = imageRef.lastIndexOf(':');
  return idx >= 0 ? imageRef.slice(idx + 1) : imageRef;
}

function Row({ label, children }: { label: string; children: ReactNode }) {
  return (
    <div className="flex items-baseline justify-between gap-4">
      <span className="text-[11px] uppercase tracking-wide text-muted-foreground shrink-0">{label}</span>
      <span className="text-[12px] font-mono text-right break-all">{children}</span>
    </div>
  );
}

/**
 * Small clickable build line at the bottom of the sidebar. Click opens a dialog
 * with fuller console build + server runtime details.
 */
export function VersionInfo() {
  const { data, isLoading, isError } = useQuery({
    queryKey: ['meta', 'version'],
    queryFn: () => api.meta.version(),
    staleTime: 5 * 60_000,
    retry: false,
  });

  return (
    <Dialog>
      <DialogTrigger asChild>
        <button
          type="button"
          title="Version details"
          className="px-[18px] py-2 text-left text-[10px] font-mono text-muted-foreground/60 hover:text-muted-foreground transition-colors border-t border-border/40"
        >
          build {COMMIT}
        </button>
      </DialogTrigger>
      <DialogContent>
        <DialogHeader>
          <DialogTitle>Version</DialogTitle>
          <DialogDescription>Console build and server runtime.</DialogDescription>
        </DialogHeader>

        <div className="flex flex-col gap-4">
          {/* Maturity note — single source of truth for beta / Windows-alpha signals (#116) */}
          <p className="text-[11px] text-muted-foreground leading-relaxed">
            <span className="font-semibold text-primary">beta</span> — Korat is in active
            development. macOS and Linux CLI are stable.{' '}
            <span className="font-semibold">Windows CLI is alpha</span> — the native service and MCP
            spawning are built but still under active testing; expect rough edges.
          </p>

          <section className="flex flex-col gap-1.5">
            <h3 className="text-[10px] font-semibold uppercase tracking-widest text-muted-foreground">
              Console
            </h3>
            <Row label="Commit">
              {COMMIT !== 'dev' ? (
                <a href={`${REPO_URL}/commit/${COMMIT}`} target="_blank" rel="noreferrer">
                  {COMMIT}
                </a>
              ) : (
                COMMIT
              )}
            </Row>
            <Row label="Built">{formatTime(BUILD_TIME)}</Row>
          </section>

          <section className="flex flex-col gap-1.5">
            <h3 className="text-[10px] font-semibold uppercase tracking-widest text-muted-foreground">
              Server
            </h3>
            {isLoading && <p className="text-[12px] text-muted-foreground">Loading…</p>}
            {isError && <p className="text-[12px] text-muted-foreground">Unavailable</p>}
            {data && (
              <>
                <Row label="Environment">{data.environment}</Row>
                <Row label="Commit">{data.commit}</Row>
                <Row label="Region">{data.region ?? '—'}</Row>
                <Row label="Instance">{data.machineId ?? '—'}</Row>
                {data.imageRef && <Row label="Release">{shortRelease(data.imageRef)}</Row>}
                <Row label="Server time">{formatTime(data.serverTimeUtc)}</Row>
              </>
            )}
          </section>
        </div>
      </DialogContent>
    </Dialog>
  );
}
