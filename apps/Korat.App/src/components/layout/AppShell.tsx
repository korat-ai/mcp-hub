import type { ReactNode } from 'react';
import { useState } from 'react';
import {
  LayoutGrid,
  Cpu,
  Server,
  Shield,
  Activity,
  BrainCircuit,
  Bot,
  MessageCircle,
  Users,
  PanelLeft,
} from 'lucide-react';
import { useRouterState } from '@tanstack/react-router';
import { NavLink } from './NavLink';
import { VersionInfo } from './VersionInfo';
import { MobileTabBar } from './MobileTabBar';
import { useSpace } from '@/hooks/useSpace';
import { useMe } from '@/account/hooks';
import { Button } from '@/components/ui/button';
import { agentPlatformEnabled } from '@/lib/features';

const CORE_NAV_ITEMS = [
  { to: '/', label: 'Overview', icon: LayoutGrid },
  { to: '/servers', label: 'MCP servers', icon: Server },
  { to: '/grants', label: 'Access', icon: Shield },
  { to: '/sessions', label: 'Activity', icon: Activity },
  { to: '/nodes', label: 'Runtimes', icon: Cpu },
] as const;

const AGENT_PLATFORM_NAV_ITEMS = [
  { to: '/inference', label: 'Inference', icon: BrainCircuit },
  { to: '/agents', label: 'Agents', icon: Bot },
  { to: '/channels', label: 'Channels', icon: MessageCircle },
  { to: '/rooms', label: 'Rooms', icon: Users },
] as const;

const AUXILIARY_ROUTE_ITEMS = [
  { to: '/connected-apps', label: 'Access' },
] as const;

const NAV_ITEMS = agentPlatformEnabled
  ? [...CORE_NAV_ITEMS, ...AGENT_PLATFORM_NAV_ITEMS]
  : [...CORE_NAV_ITEMS];

// Direct routes remain available even when the optional module is absent from navigation.
const ALL_ROUTE_ITEMS = [
  ...CORE_NAV_ITEMS,
  ...AUXILIARY_ROUTE_ITEMS,
  ...AGENT_PLATFORM_NAV_ITEMS,
] as const;

/**
 * Derives the sidebar space label from stored space name and user display name.
 *
 * - When the stored name is the legacy placeholder "default" (case-insensitive),
 *   fall back to "{displayName}'s space".
 * - displayName fallback: email local-part (before @), else the raw stored name
 *   (never crash on missing data).
 * - Custom stored names are shown as-is.
 */
export function resolveSpaceLabel(
  storedName: string | null | undefined,
  displayName: string | null | undefined,
  primaryEmail?: string | null | undefined,
): string {
  const name = storedName ?? '';
  if (name.toLowerCase() !== 'default') {
    // Custom name — show as-is (handles empty too, though that shouldn't occur).
    return name || 'default';
  }
  // Legacy placeholder — derive a friendly label.
  const friendly = displayName?.trim()
    || primaryEmail?.split('@')[0]?.trim()
    || null;
  return friendly ? `${friendly}'s space` : name;
}

// Fix M8: single source of truth — derived from NAV_ITEMS
const TITLE_MAP: Record<string, string> = Object.fromEntries(
  ALL_ROUTE_ITEMS.map((i) => [i.to, i.label]),
);

function resolveTitle(pathname: string): string {
  if (pathname.startsWith('/approve')) return 'Approve';
  if (TITLE_MAP[pathname]) return TITLE_MAP[pathname];
  // Detail routes (e.g. /inference/$pointId, /nodes/$name, /servers/$serverId)
  // are drilled into from a nav item but aren't themselves nav entries —
  // fall back to the parent nav item's label instead of the generic "Korat".
  const parent = ALL_ROUTE_ITEMS.find((i) => i.to !== '/' && pathname.startsWith(`${i.to}/`));
  return parent?.label ?? 'Korat';
}

/** Inline brand mark — shared by desktop sidebar and mobile top bar. */
function BrandMark() {
  return (
    <span className="inline-flex items-center gap-2 select-none">
      {/* Italic "K" with amber dot — from design reference */}
      <span className="relative font-bold italic text-[18px] leading-none tracking-tight">
        K
        <span
          aria-hidden="true"
          className="absolute -right-1.5 bottom-0.5 size-1 rounded-full bg-primary"
        />
      </span>
      <span className="font-semibold text-[14px] leading-none tracking-tight">
        Korat<span className="text-primary">.</span>Console
      </span>
    </span>
  );
}

// Fix I1: scoped router subscription — only this component re-renders on navigation
function PageTitle() {
  const pathname = useRouterState({ select: (s) => s.location.pathname });
  const title = resolveTitle(pathname);
  const displaySubpath = `/app${pathname === '/' ? '' : pathname}`;
  return (
    <div className="flex items-baseline gap-3 flex-1 min-w-0">
      <h1 className="text-[15px] font-semibold leading-none tracking-tight truncate">
        {title}
      </h1>
      <span className="font-mono text-[11px] text-muted-foreground truncate">
        {displaySubpath}
      </span>
    </div>
  );
}

/**
 * Desktop nav list — subscribes to useSpace() only to compute the
 * pending-access-request count for the Overview item's badge (mirrors
 * MobileTabBar's pending badge), scoped here so the rest of
 * AppShell doesn't re-render on every /api/space poll.
 */
function SidebarNav() {
  const { data: space } = useSpace();
  const pendingCount = space?.pendingAccessRequests?.length ?? 0;
  return (
    // Fix M5b: aria-label="Main" for landmark navigation
    <nav aria-label="Main" className="flex flex-col gap-0.5 px-3 py-1">
      {NAV_ITEMS.map((item) => (
        <NavLink key={item.to} {...item} badge={item.to === '/' ? pendingCount : undefined} />
      ))}
    </nav>
  );
}

/** Renders the "SPACE / {name}" section at the bottom of the sidebar. */
function SpaceFooter() {
  const { data: space } = useSpace();
  const { data: me } = useMe();
  // Don't show the Space block to unauthenticated visitors (e.g. on /app/signin) —
  // without a user it would otherwise fall back to the meaningless "default" label.
  if (!me) return null;
  const label = resolveSpaceLabel(space?.displayName, me?.displayName, me?.primaryEmail);
  return (
    <div className="border-t border-border/40 px-[18px] py-3 flex flex-col gap-0.5">
      <span className="text-[10px] font-semibold uppercase tracking-widest text-muted-foreground">
        Space
      </span>
      <span className="text-[13px] font-medium leading-snug">{label}</span>
    </div>
  );
}

// Fix I2: slot props for header actions and banner
interface AppShellProps {
  children: ReactNode;
  headerActions?: ReactNode; // SyncIndicator + ThemeToggle + AccountButton (desktop header)
  /** Actions shown in the compact mobile top bar (<720px). Typically just ThemeToggle. */
  mobileHeaderActions?: ReactNode;
  banner?: ReactNode;        // AuthBanner (Task 6)
}

/** Compact top bar shown only on mobile (<720px): brand + dark-mode toggle slot. */
function MobileTopBar({ actions }: { actions?: ReactNode }) {
  return (
    <header
      aria-label="Mobile top bar"
      className="min-[720px]:hidden h-14 flex items-center px-4 border-b border-border/40 bg-card gap-3 shrink-0"
    >
      <BrandMark />
      {/* Push actions to the right */}
      <div className="flex-1" />
      {actions}
    </header>
  );
}

export function AppShell({ children, headerActions, mobileHeaderActions, banner }: AppShellProps) {

  // Desktop sidebar collapse — reclaims width for the content area. Purely a
  // display toggle (no persistence), mirrors shell.jsx's Header panelLeft
  // button. Only reachable on desktop (the toggle lives in the desktop-only
  // header); the mobile tab bar has no equivalent rail to collapse.
  const [sidebarOpen, setSidebarOpen] = useState(true);

  return (
    <div className="min-h-screen flex bg-muted">
      {/* ── Sidebar — hidden below 720px, and collapsible on desktop ── */}
      {sidebarOpen && (
        <aside className="hidden min-[720px]:flex w-60 shrink-0 flex-col border-r border-border/40">
          {/* Brand — same 56px height as header so they visually align */}
          <div className="h-14 flex items-center px-[18px]">
            <BrandMark />
          </div>

          {/* Nav items */}
          <SidebarNav />

          {/* Spacer */}
          <div className="flex-1" />

          {/* Space footer — mirrors design ref bottom section */}
          <SpaceFooter />

          {/* Build/version line — click opens fuller version dialog */}
          <VersionInfo />
        </aside>
      )}

      {/* ── Mobile bottom tab bar — visible only <720px ─────────── */}
      <MobileTabBar />

      {/* ── Main content area ────────────────────────────────────── */}
      <main className="flex-1 min-w-0 min-[720px]:p-5">
        {/* Mobile compact top bar (brand + dark-mode toggle only) */}
        <MobileTopBar actions={mobileHeaderActions} />

        {/* Desktop card wrapper */}
        <div className="min-[720px]:bg-card min-[720px]:rounded-xl min-[720px]:shadow-sm min-[720px]:overflow-hidden flex flex-col min-[720px]:h-[calc(100vh-2.5rem)]">
          {/* ── Desktop header (title/breadcrumb/sync/user-menu) ── */}
          <header className="hidden min-[720px]:flex h-14 items-center px-6 border-b border-border/40 gap-4">
            <Button
              variant="ghost"
              size="icon"
              aria-label={sidebarOpen ? 'Hide sidebar' : 'Show sidebar'}
              onClick={() => setSidebarOpen((open) => !open)}
            >
              <PanelLeft className="size-4" aria-hidden="true" />
            </Button>
            <PageTitle />
            {headerActions}
          </header>

          {banner}

          {/* ── Scrollable content ──────────────────────────────── */}
          {/* pb-20 on mobile so content isn't hidden behind the 64px tab bar */}
          <div className="flex-1 overflow-auto">
            <div className="max-w-[1080px] mx-auto p-4 min-[720px]:p-6 pb-[calc(5rem+env(safe-area-inset-bottom))] min-[720px]:pb-6">
              {children}
            </div>
          </div>
        </div>
      </main>
    </div>
  );
}
