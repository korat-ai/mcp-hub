import { useQuery } from '@tanstack/react-query';
import { oauthConsentsQueryOptions } from '@/lib/queries/oauthConsents';

/** Space-MCP inc-2a, Task 8: owner console — list of OAuth consents (client × Space). */
export function useOAuthConsents() {
  return useQuery(oauthConsentsQueryOptions());
}
