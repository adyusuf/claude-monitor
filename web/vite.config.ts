/// <reference types="vitest/config" />
import { defineConfig } from "vite";
import react from "@vitejs/plugin-react";

// Development only: the dev server forwards /api to the API (one origin, as in production, global #17).
// The one place a host appears for the web tier; the built app itself only ever calls relative /api paths.
const apiTarget = process.env.WEB_API_PROXY ?? "http://localhost:5080";

export default defineConfig({
  plugins: [react()],
  server: { port: 5173, proxy: { "/api": { target: apiTarget, changeOrigin: false } } },
  build: { outDir: "dist", sourcemap: false },
  test: {
    environment: "jsdom",
    globals: true,
    setupFiles: ["src/test/setup.ts"],
    include: ["src/**/*.test.{ts,tsx}"],
    coverage: {
      provider: "v8",
      include: ["src/**/*.{ts,tsx}"],
      // The only exclusions (global #29): the tests and their helpers, the entry point that only mounts the app,
      // and type-only declarations.
      exclude: ["src/**/*.test.{ts,tsx}", "src/test/**", "src/main.tsx", "src/api/types.ts", "src/vite-env.d.ts"],
      thresholds: { lines: 80 },
      reporter: ["text-summary", "text"],
    },
  },
});
