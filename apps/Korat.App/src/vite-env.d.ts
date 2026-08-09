/// <reference types="vite/client" />

interface ImportMetaEnv {
  readonly VITE_COMMIT_SHA: string;
  readonly VITE_BUILD_TIME: string;
  readonly VITE_SENTRY_DSN: string | undefined;
  readonly VITE_SENTRY_ENVIRONMENT: string | undefined;
  readonly VITE_ENABLE_AGENT_PLATFORM: string | undefined;
}

interface ImportMeta {
  readonly env: ImportMetaEnv;
}
