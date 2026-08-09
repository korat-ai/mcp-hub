import { useQuery } from '@tanstack/react-query';
import { spaceQueryOptions } from '@/lib/queries/space';

export function useSpace() {
  return useQuery(spaceQueryOptions());
}
