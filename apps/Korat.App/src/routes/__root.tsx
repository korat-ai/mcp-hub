import { createRootRoute, Outlet, useRouterState } from '@tanstack/react-router';
import { AppShell } from '@/components/layout/AppShell';
import { ErrorBoundary } from '@/components/layout/ErrorBoundary';
import { AuthGate } from '@/components/layout/AuthGate';
import { ThemeToggle } from '@/components/layout/ThemeToggle';
import { SyncIndicator, SyncDot } from '@/components/layout/SyncIndicator';
import { AuthBanner } from '@/components/layout/AuthBanner';
import { UserMenu } from '@/components/UserMenu';

export const Route = createRootRoute({
  component: RootLayout,
});

/**
 * Reads the current pathname and returns whether we are on the sign-in page
 * (or any /signin/* sub-route like /signin/link-confirm). Scoped subscription
 * so only this component re-renders on navigation — AppShell itself does not.
 */
function useIsSignInRoute(): boolean {
  return useRouterState({
    select: (s) => s.location.pathname.startsWith('/signin'),
  });
}

/** Exported for tests — allows mounting the root shell in a minimal router. */
export function RootLayout() {
  const isSignIn = useIsSignInRoute();

  return (
    <ErrorBoundary>
      <AppShell
        headerActions={
          isSignIn ? undefined : (
            <>
              <SyncIndicator />
              <ThemeToggle />
              <UserMenu />
            </>
          )
        }
        mobileHeaderActions={isSignIn ? undefined : <><SyncDot /><ThemeToggle /></>}
        banner={isSignIn ? undefined : <AuthBanner />}
      >
        <AuthGate>
          <Outlet />
        </AuthGate>
      </AppShell>
    </ErrorBoundary>
  );
}
