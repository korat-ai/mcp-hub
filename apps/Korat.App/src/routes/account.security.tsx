/**
 * /account/security — Security section (email change + active sessions).
 *
 * TanStack Router file-based child of account.tsx (dot-notation = nested route).
 */
import { createFileRoute } from '@tanstack/react-router';
import { AccountSecurityRoute } from '@/routes/account';

export const Route = createFileRoute('/account/security')({
  component: AccountSecurityRoute,
});
