/**
 * /account/cli — CLI Tokens section (list + revoke).
 *
 * TanStack Router file-based child of account.tsx (dot-notation = nested route).
 */
import { createFileRoute } from '@tanstack/react-router';
import { AccountCliRoute } from '@/routes/account';

export const Route = createFileRoute('/account/cli')({
  component: AccountCliRoute,
});
