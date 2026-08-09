import { describe, expect, it, beforeEach } from 'vitest'
import { readCsrfToken, attachCsrfHeader } from '@/lib/csrf'

// web-M4 minor: the server now issues the antiforgery REQUEST token in a cookie named
// __Host-XSRF-TOKEN (upgraded from XSRF-TOKEN) so the browser enforces Secure + Path=/ +
// no Domain, preventing sub-domain injection attacks.
const COOKIE_NAME = '__Host-XSRF-TOKEN'

describe('csrf', () => {
  // The SPA reads the antiforgery REQUEST token from the JS-readable __Host-XSRF-TOKEN cookie
  // (the __Secure-korat_xsrf cookie holds the HttpOnly cookie token and is NOT read here).
  // __Host- prefixed cookies require the Secure attribute (jsdom runs on https://localhost/),
  // so every set/clear must include `; Secure` or jsdom silently refuses to store the cookie.
  beforeEach(() => { document.cookie = `${COOKIE_NAME}=; Max-Age=0; path=/; Secure` })

  it('readCsrfToken returns null when cookie absent', () => {
    expect(readCsrfToken()).toBeNull()
  })

  it('readCsrfToken returns value when cookie present', () => {
    document.cookie = `${COOKIE_NAME}=abc123; path=/; Secure`
    expect(readCsrfToken()).toBe('abc123')
  })

  it('attachCsrfHeader skips GET / HEAD / OPTIONS', () => {
    document.cookie = `${COOKIE_NAME}=abc123; path=/; Secure`
    const h = new Headers()
    attachCsrfHeader(h, 'GET')
    expect(h.has('x-xsrf-token')).toBe(false)
  })

  it('attachCsrfHeader sets header on POST when cookie present', () => {
    document.cookie = `${COOKIE_NAME}=abc123; path=/; Secure`
    const h = new Headers()
    attachCsrfHeader(h, 'POST')
    expect(h.get('x-xsrf-token')).toBe('abc123')
  })

  it('attachCsrfHeader is a no-op on POST when cookie missing', () => {
    const h = new Headers()
    attachCsrfHeader(h, 'POST')
    expect(h.has('x-xsrf-token')).toBe(false)
  })
})
