/**
 * /account/profile — Profile section (display name + connected providers).
 *
 * TanStack Router file-based child of account.tsx (dot-notation = nested route).
 */
import { createFileRoute } from '@tanstack/react-router';
import { AccountProfileRoute } from '@/routes/account';

export const Route = createFileRoute('/account/profile')({
  component: AccountProfileRoute,
});
