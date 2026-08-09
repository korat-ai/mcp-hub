export const queryKeys = {
  space: {
    all: ['space'] as const,
    detail: () => [...queryKeys.space.all] as const,
  },
  grants: {
    all: ['grants'] as const,
    list: () => [...queryKeys.grants.all, 'list'] as const,
    byId: (id: string) => [...queryKeys.grants.all, 'detail', id] as const,
  },
  sessions: {
    all: ['sessions'] as const,
    list: () => [...queryKeys.sessions.all] as const,
  },
  oauthConsents: {
    all: ['oauth-consents'] as const,
    list: () => [...queryKeys.oauthConsents.all, 'list'] as const,
  },
  accessRequests: {
    all: ['access-requests'] as const,
    byId: (id: string) => [...queryKeys.accessRequests.all, 'detail', id] as const,
  },
  pendingLink: {
    all: ['pendingLink'] as const,
  },
  // Account UI (SP3) — auth self-service + CLI token management
  auth: {
    me:       () => ['auth', 'me'] as const,
    sessions: () => ['auth', 'sessions'] as const,
  },
  cli: {
    tokens: () => ['cli', 'tokens'] as const,
  },
  inference: {
    all: ['inference'] as const,
    list: () => ['inference', 'list'] as const,
    keys: (pointId: string) => ['inference', 'keys', pointId] as const,
  },
  agents: {
    all: ['agents'] as const,
    list: () => ['agents', 'list'] as const,
    thread: (agentId: string) => ['agents', 'thread', agentId] as const,
  },
  channels: {
    all: ['channels'] as const,
    list: () => ['channels', 'list'] as const,
    byId: (id: string) => ['channels', 'detail', id] as const,
  },
  rooms: {
    all: ['rooms'] as const,
    detail: () => ['rooms', 'detail'] as const,
    transcript: (roomId: string) => ['rooms', 'transcript', roomId] as const,
  },
};
