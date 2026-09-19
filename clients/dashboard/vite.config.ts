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
      port: 5173,
      strictPort: true,
      // Tenant subdomains have to reach the dev server: `acme.localhost:5173` resolves to
      // tenant "acme" (src/auth/tenant-resolution.ts), and Vite's default host check
      // answers 403 for any Host it was not told about. `.localhost` covers every label
      // under it, and every browser resolves *.localhost to the loopback address without
      // a hosts-file entry. Dev only — the nginx image does its own host handling.
      allowedHosts: [".localhost"],
      // Dev mirrors the nginx image: the dashboard proxies to the API rather than
      // letting the browser call it cross-origin. The refresh token is an HttpOnly
      // SameSite=Strict cookie and CORS allows no credentials (ADR-0002), so a
      // cross-origin dev server could never refresh a session.
      proxy: {
        "/api": { target: apiBase, changeOrigin: true, secure: false },
        "/openapi": { target: apiBase, changeOrigin: true, secure: false },
        "/scalar": { target: apiBase, changeOrigin: true, secure: false },
        // Health probes live at the root, not under /api. Without this the dashboard's
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
