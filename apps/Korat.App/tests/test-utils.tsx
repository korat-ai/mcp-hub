import type { ReactNode } from 'react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';

/**
 * Wrap a tree in a fresh QueryClient with retry/refetch disabled — guarantees
 * tests don't share cache state or fire background refetches between assertions.
 */
export function withQueryClient(children: ReactNode) {
  const qc = new QueryClient({
    defaultOptions: {
      queries: { retry: false },
      mutations: { retry: false },
    },
  });
  return <QueryClientProvider client={qc}>{children}</QueryClientProvider>;
}
