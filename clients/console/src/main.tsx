import { StrictMode } from "react";
import { createRoot } from "react-dom/client";
import { App } from "@/App";
import { loadRuntimeConfig } from "@/env";
import "@/styles/globals.css";

// Runtime config must resolve before React mounts so env.apiBase reads
// inside components see the right value on first paint.
await loadRuntimeConfig();

const rootElement = document.getElementById("root");
if (!rootElement) {
  throw new Error("Root element '#root' not found");
}

// Nothing installs a credential before React mounts. A token must only ever come
// from a response the console itself asked for — never from the URL (fragment or
// query), which anyone can hand a signed-in user. The two-app era's
// `#impersonate?token=…` hand-off is gone with the second app (ADR-0004).

createRoot(rootElement).render(
  <StrictMode>
    <App />
  </StrictMode>,
);
