import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';
import tailwindcss from '@tailwindcss/vite';
import { tanstackRouter } from '@tanstack/router-plugin/vite';
import { sentryVitePlugin } from '@sentry/vite-plugin';
import { execSync } from 'node:child_process';
import path from 'node:path';

const commitSha = process.env.VITE_COMMIT_SHA ?? (() => {
  try {
    return execSync('git rev-parse --short HEAD').toString().trim();
  } catch {
    return 'dev';
  }
})();

// Source-map upload is opt-in and requires the complete destination tuple.
// A token alone must never upload source maps to an implicit organization,
// project, or service.
const sentryAuthToken = process.env.SENTRY_AUTH_TOKEN;
const sentryOrg = process.env.SENTRY_ORG;
const sentryProject = process.env.SENTRY_PROJECT;
const sentryUrl = process.env.SENTRY_URL;
const sentryPlugin =
  sentryAuthToken && sentryOrg && sentryProject && sentryUrl
    ? [
        sentryVitePlugin({
          authToken: sentryAuthToken,
          org: sentryOrg,
          project: sentryProject,
          url: sentryUrl,
          // Release tag matches the runtime VITE_COMMIT_SHA so stack traces
          // resolve to the correct source revision in the configured service.
          release: { name: commitSha },
          // Plugin manages hidden source maps; we keep sourcemap:'hidden' below.
          sourcemaps: { filesToDeleteAfterUpload: ['../Korat.Cloud/wwwroot/app/**/*.map'] },
          // A source-map upload failure must not fail the application build:
          // the bundle remains valid without uploaded maps.
          errorHandler: (err) => {
            console.warn(
              `[sentry-vite-plugin] non-fatal: source-map upload failed, shipping without maps: ${err?.message ?? err}`,
            );
          },
        }),
      ]
    : [];

export default defineConfig({
  define: {
    'import.meta.env.VITE_COMMIT_SHA': JSON.stringify(commitSha),
    'import.meta.env.VITE_BUILD_TIME': JSON.stringify(new Date().toISOString()),
  },
  plugins: [
    tanstackRouter({
      routesDirectory: './src/routes',
      generatedRouteTree: './src/routeTree.gen.ts',
    }),
    react(),
    tailwindcss(),
    ...sentryPlugin,
  ],
  base: '/app/',
  build: {
    outDir: path.resolve(__dirname, '../Korat.Cloud/wwwroot/app'),
    emptyOutDir: true,
    // 'hidden' emits .map files (needed for source-map upload) but doesn't
    // reference them from the bundle — so browsers never download them.
    // When SENTRY_AUTH_TOKEN is absent, sourcemap is left as the default
    // (false) to keep the build output lean.
    sourcemap: sentryPlugin.length > 0 ? 'hidden' : false,
  },
  resolve: {
    alias: {
      '@': path.resolve(__dirname, './src'),
    },
  },
  server: {
    port: 5173,
    proxy: {
      '/api': 'http://localhost:5191',
    },
  },
});
