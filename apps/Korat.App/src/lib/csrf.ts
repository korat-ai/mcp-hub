// Read the antiforgery REQUEST token, which the server exposes in a JS-readable cookie.
// (The __Secure-korat_xsrf cookie holds the COOKIE token and is HttpOnly — echoing it back
// fails validation as "cookie token and request token were swapped".)
// web-M4 minor: the server now issues the cookie with the __Host- prefix, which enforces
// Secure + Path=/ + no Domain at the browser level for defence-in-depth.
const COOKIE_NAME = '__Host-XSRF-TOKEN'

export function readCsrfToken(): string | null {
  if (typeof document === 'undefined') return null
  const prefix = COOKIE_NAME + '='
  for (const raw of document.cookie.split(';')) {
    const trimmed = raw.trim()
    if (trimmed.startsWith(prefix)) return decodeURIComponent(trimmed.slice(prefix.length))
  }
  return null
}

export function attachCsrfHeader(headers: Headers, method: string): Headers {
  if (method === 'GET' || method === 'HEAD' || method === 'OPTIONS') return headers
  const token = readCsrfToken()
  if (token) headers.set('X-XSRF-TOKEN', token)
  return headers
}
