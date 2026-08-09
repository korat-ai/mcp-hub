import { useQuery } from '@tanstack/react-query';
import { grantsQueryOptions } from '@/lib/queries/grants';

export function useGrants() {
  return useQuery(grantsQueryOptions());
}
