import { defineConfig } from 'vitest/config';
import react from '@vitejs/plugin-react';
import path from 'node:path';

export default defineConfig({
  plugins: [react()],
  resolve: { alias: { '@': path.resolve(__dirname, './src') } },
  test: {
    environment: 'jsdom',
    // Use https:// so jsdom honours the __Secure- cookie prefix (required for
    // csrf.test.ts which sets __Secure-korat_xsrf cookies via document.cookie).
    environmentOptions: { jsdom: { url: 'https://localhost/' } },
    globals: true,
    setupFiles: ['./tests/setup.ts'],
    css: false,
    coverage: {
      provider: 'v8',
      reporter: ['text-summary', 'json-summary'],
      reportsDirectory: './coverage',
      // 12 pre-existing test failures (unrelated to coverage; present with or
      // without --coverage) would otherwise suppress the report entirely —
      // v8's default is to skip it on any failure. Fixing those tests is out
      // of scope for this baseline measurement task.
      reportOnFailure: true,
      // Роуты включаем намеренно: они и есть пользовательская поверхность,
      // которую инвентаризирует реестр, а не просто склейка компонентов.
      include: ['src/**/*.{ts,tsx}'],
      exclude: [
        'src/**/*.test.{ts,tsx}',
        'src/routeTree.gen.ts',
        'src/vite-env.d.ts',
      ],
    },
  },
});
