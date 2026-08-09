import './index.css';
import { initSentry } from '@/lib/sentry';
// Sentry init must run before React renders so the ErrorBoundary can capture
// render-phase errors from the very first paint.
initSentry();

import { StrictMode } from 'react';
import { createRoot } from 'react-dom/client';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { RouterProvider } from '@tanstack/react-router';
import { toast } from 'sonner';
import { Toaster } from '@/components/ui/sonner';
import { ThemeProvider } from '@/components/layout/ThemeProvider';
import { ApiError } from '@/lib/api';
import { router } from './router';

const queryClient = new QueryClient({
  defaultOptions: {
    queries: {
      refetchOnWindowFocus: true,
      retry: 1,
      // Short staleTime aligns with the 5s polling interval used by useSpace/
      // useGrants/useSessions: data is considered fresh until just before the
      // next polling tick, so a focus/revisit within a tick won't re-fetch.
      staleTime: 4000,
    },
    mutations: {
      onError: (err) =>
        toast.error(err instanceof ApiError ? err.message : 'Action failed'),
    },
  },
});

const rootElement = document.getElementById('root');
if (!rootElement) {
  throw new Error('Root element #root not found in index.html');
}

createRoot(rootElement).render(
  <StrictMode>
    <ThemeProvider>
      <QueryClientProvider client={queryClient}>
        <RouterProvider router={router} />
        <Toaster position="bottom-right" />
      </QueryClientProvider>
    </ThemeProvider>
  </StrictMode>,
);
