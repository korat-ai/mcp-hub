/**
 * UserMenu — top-nav Account link + sign-out action.
 *
 * Placed in the header's headerActions slot (see __root.tsx). Reuses the
 * existing Button component and useSignOut hook from the account hooks module.
 * No new HTTP calls — sign-out goes entirely through useSignOut.
 */
import { Link } from '@tanstack/react-router';
import { UserCircle, LogOut } from 'lucide-react';
import { Button } from '@/components/ui/button';
import { useSignOut } from '@/account/hooks';

export function UserMenu() {
  const signOut = useSignOut();

  return (
    <div className="flex items-center gap-1">
      {/* Account link — navigates to the account profile section */}
      <Button variant="ghost" size="sm" asChild>
        <Link to="/account/profile" aria-label="Account">
          <UserCircle className="size-4" aria-hidden="true" />
          <span>Account</span>
        </Link>
      </Button>

      {/* Sign-out action — calls POST /api/auth/signout via useSignOut */}
      <Button
        variant="ghost"
        size="sm"
        aria-label="Sign out"
        disabled={signOut.isPending}
        onClick={() => signOut.mutate()}
      >
        <LogOut className="size-4" aria-hidden="true" />
        <span>Sign out</span>
      </Button>
    </div>
  );
}
