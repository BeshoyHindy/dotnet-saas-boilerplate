/// <reference types="vitest/config" />
import { defineConfig, loadEnv } from "vite";
import react from "@vitejs/plugin-react";
import tailwindcss from "@tailwindcss/vite";
import path from "node:path";

export default defineConfig(({ mode }) => {
  const env = loadEnv(mode, process.cwd(), "");
  // The ONLY VITE_* variable, and it configures the dev proxy target — never the
  // runtime apiBase. That comes from /config.json (public/config.json in dev), which
  // ships apiBase="" so every request is same-origin.
  const apiBase = env.VITE_API_BASE_URL ?? "http://localhost:5030";

  return {
    plugins: [react(), tailwindcss()],
    resolve: {
      alias: {
        "@": path.resolve(__dirname, "./src"),
      },
    },
    server: {
      port: 5174,
      strictPort: true,
      // Dev mirrors the nginx image: the console proxies to the API rather than
      // letting the browser call it cross-origin. The refresh token is an HttpOnly
      // SameSite=Strict cookie and CORS allows no credentials (ADR-0002), so a
      // cross-origin dev server could never refresh a session.
      proxy: {
        "/api": { target: apiBase, changeOrigin: true, secure: false },
        "/openapi": { target: apiBase, changeOrigin: true, secure: false },
        "/scalar": { target: apiBase, changeOrigin: true, secure: false },
        // Health probes live at the root, not under /api. Without this the console's
        // /system/health page 404s in dev, because Vite would serve the request
        // itself instead of proxying it.
        "/health": { target: apiBase, changeOrigin: true, secure: false },
      },
    },
    test: {
      environment: "jsdom",
      include: ["src/**/*.test.ts", "src/**/*.test.tsx"],
      restoreMocks: true,
    },
  };
});
