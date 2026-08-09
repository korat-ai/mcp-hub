/**
 * Account self-service data hooks.
 *
 * Each hook is the single source of truth for its endpoint URL and its cache
 * key. Components never call api() directly — they go through these hooks.
 *
 * Cache discipline (spec §4):
 * - Every mutation success path invalidates exactly the keys whose server
 *   state it changed.
 * - useSignOut calls queryClient.clear() to drop all cached PII, then navigates
 *   to /signin. Current-device sign-out goes exclusively through useSignOut (UserMenu).
 * - useRevokeSession handles only non-current sessions (invalidates auth.sessions).
 * - No optimistic cache writes; rely on invalidate-then-refetch.
 */
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useNavigate } from '@tanstack/react-router';
import { api } from '@/lib/api';
import { queryKeys } from '@/lib/queryKeys';

// ---------------------------------------------------------------------------
// useMe — GET /api/auth/me
// ---------------------------------------------------------------------------

export function useMe() {
  return useQuery({
    queryKey: queryKeys.auth.me(),
    queryFn: () => api.auth.me(),
  });
}

// ---------------------------------------------------------------------------
// useUpdateMe — PUT /api/auth/me
// ---------------------------------------------------------------------------

export function useUpdateMe() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (body: { displayName: string }) => api.auth.updateMe(body),
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: queryKeys.auth.me() });
    },
  });
}

// ---------------------------------------------------------------------------
// useSessions — GET /api/auth/sessions
// ---------------------------------------------------------------------------

export function useSessions() {
  return useQuery({
    queryKey: queryKeys.auth.sessions(),
    queryFn: () => api.auth.sessions.list(),
  });
}

// ---------------------------------------------------------------------------
// useRevokeSession — POST /api/auth/sessions/{id}/revoke
//
// Only for non-current sessions — the current-session Revoke button is not
// rendered (SessionsList hides it). Sign-out of the current device is handled
// by useSignOut (UserMenu). Removing the `current` param prevents callers from
// accidentally triggering the clear+navigate path from a non-current session.
// ---------------------------------------------------------------------------

export function useRevokeSession() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (id: string) => api.auth.sessions.revoke(id),
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: queryKeys.auth.sessions() });
    },
  });
}

// ---------------------------------------------------------------------------
// useRevokeOtherSessions — POST /api/auth/sessions/revoke-others
//
// Revokes every active session except the current device. The current session
// is identified server-side from the session cookie.
// ---------------------------------------------------------------------------

export function useRevokeOtherSessions() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: () => api.auth.sessions.revokeOthers(),
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: queryKeys.auth.sessions() });
    },
  });
}

// ---------------------------------------------------------------------------
// useRequestEmailChange — POST /api/auth/email/change
//
// Does NOT invalidate auth.me — the email is not changed until the user
// clicks the verification link sent to the new address.
// ---------------------------------------------------------------------------

export function useRequestEmailChange() {
  return useMutation({
    mutationFn: (body: { newEmail: string }) => api.auth.email.change(body),
  });
}

// ---------------------------------------------------------------------------
// useConfirmEmailChange — POST /api/auth/email/change/confirm
//
// On success, invalidates auth.me so the header + profile reflect the new
// primary email immediately.
// ---------------------------------------------------------------------------

export function useConfirmEmailChange() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (body: { token: string }) => api.auth.email.confirm(body),
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: queryKeys.auth.me() });
    },
  });
}

// ---------------------------------------------------------------------------
// useCliTokens — GET /api/cli/tokens
// ---------------------------------------------------------------------------

export function useCliTokens() {
  return useQuery({
    queryKey: queryKeys.cli.tokens(),
    queryFn: () => api.cli.tokens.list(),
  });
}

// ---------------------------------------------------------------------------
// useRevokeCliToken — POST /api/cli/tokens/{id}/revoke
// ---------------------------------------------------------------------------

export function useRevokeCliToken() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (id: string) => api.cli.tokens.revoke(id),
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: queryKeys.cli.tokens() });
    },
  });
}

// ---------------------------------------------------------------------------
// useSignOut — POST /api/auth/signout
//
// Clears the entire query cache (drops all cached PII) then navigates to
// /signin. The backend has already cleared the __Host- session cookie.
// ---------------------------------------------------------------------------

export function useSignOut() {
  const qc = useQueryClient();
  const navigate = useNavigate();
  return useMutation({
    mutationFn: () => api.auth.signout(),
    onSuccess: () => {
      qc.clear();
      navigate({ to: '/signin' });
    },
  });
}
