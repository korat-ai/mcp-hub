import { useQuery } from '@tanstack/react-query';
import { sessionsQueryOptions } from '@/lib/queries/sessions';

export function useSessions() {
  return useQuery(sessionsQueryOptions());
}
