import '@tanstack/query-core';

declare module '@tanstack/query-core' {
  interface Register {
    defaultError: Error;
  }
}
