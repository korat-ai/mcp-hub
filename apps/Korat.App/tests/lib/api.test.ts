import { describe, expect, it } from 'vitest';
import { http, HttpResponse } from 'msw';
import { server } from '../setup';
import { ApiError, api, getIdValue } from '@/lib/api';

describe('api wrapper', () => {
  it('returns parsed JSON on 200', async () => {
    const data = await api.space.get();
    expect(data.displayName).toBe('Test Space');
  });

  it('throws ApiError with status and body on 401', async () => {
    server.use(http.get('/api/space', () => new HttpResponse('nope', { status: 401 })));
    const err = await api.space.get().catch((e) => e);
    expect(err).toBeInstanceOf(ApiError);
    expect((err as ApiError).status).toBe(401);
    expect((err as ApiError).body).toBe('nope');
    expect((err as ApiError).message).toContain('401');
  });

  it('returns undefined on 204 no-content', async () => {
    server.use(http.post('/api/grants/g1/revoke', () => new HttpResponse(null, { status: 204 })));
    const out = await api.grants.revoke('g1');
    expect(out).toBeUndefined();
  });

  it('accepts IdValue or string in mutations', async () => {
    server.use(http.post('/api/grants/g2/revoke', () => new HttpResponse(null, { status: 204 })));
    await expect(api.grants.revoke('g2')).resolves.toBeUndefined();
    server.use(http.post('/api/grants/g3/revoke', () => new HttpResponse(null, { status: 204 })));
    await expect(api.grants.revoke({ value: 'g3' })).resolves.toBeUndefined();
  });
});

describe('getIdValue', () => {
  it('returns the string itself when given a string', () => {
    expect(getIdValue('abc')).toBe('abc');
  });

  it('returns .value when given an IdValue object', () => {
    expect(getIdValue({ value: 'xyz' })).toBe('xyz');
  });
});
