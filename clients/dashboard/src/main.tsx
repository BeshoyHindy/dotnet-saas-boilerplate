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
// from a response the dashboard itself asked for — never from the URL (fragment or
// query), which anyone can hand a signed-in user. There are two apps again
// (ADR-0008), but no token is ever handed between them: an operator who wants to act
// inside a tenant does it in the console, on a credential the server mints for them.

createRoot(rootElement).render(
  <StrictMode>
    <App />
  </StrictMode>,
);
