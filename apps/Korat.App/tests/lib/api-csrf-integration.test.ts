/**
 * API client ↔ CSRF integration tests (task #7).
 *
 * Verifies that the `request()` helper in api.ts:
 *  - Attaches X-XSRF-TOKEN on POST mutations when the XSRF-TOKEN cookie is set.
 *  - Does NOT attach X-XSRF-TOKEN on GET requests.
 *  - Sets Content-Type: application/json when a JSON body is present.
 *  - Throws ApiError with the correct .status and .body on 4xx/5xx.
 *  - Returns undefined on 204 No Content.
 *
 * MSW intercepts the real fetch, giving us full header visibility via
 * the `request` object passed to each handler.
 */
import { describe, expect, it, beforeEach, afterEach } from 'vitest';
import { http, HttpResponse } from 'msw';
import { server } from '../setup';
import { ApiError, api } from '@/lib/api';

// Helper: set / clear the antiforgery request-token cookie between tests.
// web-M4 minor: the cookie was renamed to the __Host- prefix, which the browser (and jsdom
// on https://localhost/) only stores when the Secure attribute is present.
const COOKIE_NAME = '__Host-XSRF-TOKEN';
const TEST_TOKEN = 'test-csrf-token-abc123';

function setCsrfCookie(value: string) {
  document.cookie = `${COOKIE_NAME}=${value}; path=/; Secure`;
}

function clearCsrfCookie() {
  document.cookie = `${COOKIE_NAME}=; Max-Age=0; path=/; Secure`;
}

beforeEach(() => clearCsrfCookie());
afterEach(() => clearCsrfCookie());

// ── CSRF header attachment ────────────────────────────────────────────────────

describe('CSRF header — mutations', () => {
  it('POST sends X-XSRF-TOKEN header equal to the XSRF-TOKEN cookie', async () => {
    setCsrfCookie(TEST_TOKEN);

    let capturedToken: string | null = null;
    server.use(
      http.post('/api/grants/g1/revoke', ({ request }) => {
        capturedToken = request.headers.get('x-xsrf-token');
        return new HttpResponse(null, { status: 204 });
      }),
    );

    await api.grants.revoke('g1');
    expect(capturedToken).toBe(TEST_TOKEN);
  });

  it('POST does NOT send X-XSRF-TOKEN when XSRF-TOKEN cookie is absent', async () => {
    let capturedToken: string | null = 'sentinel';
    server.use(
      http.post('/api/grants/g1/revoke', ({ request }) => {
        capturedToken = request.headers.get('x-xsrf-token');
        return new HttpResponse(null, { status: 204 });
      }),
    );

    await api.grants.revoke('g1');
    expect(capturedToken).toBeNull();
  });
});

describe('CSRF header — GET requests', () => {
  it('GET /api/space does NOT send X-XSRF-TOKEN even when cookie is present', async () => {
    setCsrfCookie(TEST_TOKEN);

    let capturedToken: string | null = 'sentinel';
    server.use(
      http.get('/api/space', ({ request }) => {
        capturedToken = request.headers.get('x-xsrf-token');
        return HttpResponse.json({
          id: { value: 'sp1' },
          displayName: 'Space',
          nodes: [],
          mcpServers: [],
          pendingAccessRequests: [],
        });
      }),
    );

    await api.space.get();
    expect(capturedToken).toBeNull();
  });
});

// ── Content-Type header ────────────────────────────────────────────────────────

describe('Content-Type header', () => {
  it('sets Content-Type: application/json when a JSON body is sent', async () => {
    let capturedContentType: string | null = null;
    server.use(
      http.post('/signin/magic-link', ({ request }) => {
        capturedContentType = request.headers.get('content-type');
        return new HttpResponse(null, { status: 204 });
      }),
    );

    await api.auth.requestMagicLink('test@example.com');
    expect(capturedContentType).toContain('application/json');
  });

  it('does NOT set Content-Type on a body-less POST', async () => {
    let capturedContentType: string | null = 'sentinel';
    server.use(
      http.post('/api/grants/g1/revoke', ({ request }) => {
        capturedContentType = request.headers.get('content-type');
        return new HttpResponse(null, { status: 204 });
      }),
    );

    await api.grants.revoke('g1');
    expect(capturedContentType).toBeNull();
  });
});

// ── Error handling ────────────────────────────────────────────────────────────

describe('ApiError — 4xx / 5xx', () => {
  it('throws ApiError with .status and .body on 400', async () => {
    server.use(
      http.post('/api/grants/g1/revoke', () =>
        new HttpResponse('validation error', { status: 400 }),
      ),
    );

    const err = await api.grants.revoke('g1').catch((e) => e);
    expect(err).toBeInstanceOf(ApiError);
    expect((err as ApiError).status).toBe(400);
    expect((err as ApiError).body).toBe('validation error');
  });

  it('throws ApiError with .status and .body on 500', async () => {
    server.use(
      http.get('/api/space', () =>
        new HttpResponse('internal server error', { status: 500 }),
      ),
    );

    const err = await api.space.get().catch((e) => e);
    expect(err).toBeInstanceOf(ApiError);
    expect((err as ApiError).status).toBe(500);
    expect((err as ApiError).body).toBe('internal server error');
    expect((err as ApiError).message).toContain('500');
  });

  it('throws ApiError with empty body on 403 with no response body', async () => {
    server.use(
      http.get('/api/space', () => new HttpResponse(null, { status: 403 })),
    );

    const err = await api.space.get().catch((e) => e);
    expect(err).toBeInstanceOf(ApiError);
    expect((err as ApiError).status).toBe(403);
  });
});

// ── 204 No Content ────────────────────────────────────────────────────────────

describe('204 No Content', () => {
  it('returns undefined on 204 for a mutation', async () => {
    server.use(
      http.post('/api/grants/g1/revoke', () => new HttpResponse(null, { status: 204 })),
    );

    const result = await api.grants.revoke('g1');
    expect(result).toBeUndefined();
  });
});

// ── Sign-out CSRF ─────────────────────────────────────────────────────────────

describe('api.auth.signout — CSRF header', () => {
  it('POST /api/auth/signout sends X-XSRF-TOKEN when cookie is present', async () => {
    setCsrfCookie(TEST_TOKEN);

    let capturedToken: string | null = null;
    server.use(
      http.post('/api/auth/signout', ({ request }) => {
        capturedToken = request.headers.get('x-xsrf-token');
        return new HttpResponse(null, { status: 204 });
      }),
    );

    await api.auth.signout();
    expect(capturedToken).toBe(TEST_TOKEN);
  });

  it('POST /api/auth/signout returns undefined on 204', async () => {
    server.use(
      http.post('/api/auth/signout', () => new HttpResponse(null, { status: 204 })),
    );

    const result = await api.auth.signout();
    expect(result).toBeUndefined();
  });
});
