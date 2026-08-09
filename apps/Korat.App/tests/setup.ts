import '@testing-library/jest-dom/vitest';
import { afterAll, afterEach, beforeAll, vi } from 'vitest';
import { setupServer } from 'msw/node';
import { http, HttpResponse } from 'msw';
import type { SpaceDto, GrantDto, SessionDto } from '@/types/api';

Object.defineProperty(globalThis, 'ResizeObserver', {
  configurable: true,
  writable: true,
  value: class {
    observe = vi.fn();
    unobserve = vi.fn();
    disconnect = vi.fn();
  },
});
Object.defineProperty(globalThis, 'scrollTo', {
  configurable: true,
  writable: true,
  value: vi.fn(),
});

// ---------------------------------------------------------------------------
// Sentry stub — prevents the real SDK from initialising in tests (no DSN) and
// satisfies any import of @/lib/sentry without network calls.
// ---------------------------------------------------------------------------
vi.mock('@/lib/sentry', () => ({
  initSentry: vi.fn(),
  Sentry: { captureException: vi.fn(), init: vi.fn() },
}));

const emptySpace: SpaceDto = {
  id: { value: 'default' },
  displayName: 'Test Space',
  nodes: [],
  mcpServers: [],
  pendingAccessRequests: [],
};

const emptyGrants: GrantDto[] = [];
const emptySessions: SessionDto[] = [];

const handlers = [
  http.get('/api/space', () => HttpResponse.json(emptySpace)),
  http.get('/api/grants', () => HttpResponse.json(emptyGrants)),
  http.get('/api/sessions', () => HttpResponse.json(emptySessions)),
];

export const server = setupServer(...handlers);

beforeAll(() => server.listen({ onUnhandledRequest: 'error' }));
afterEach(() => server.resetHandlers());
afterAll(() => server.close());
