import { afterEach } from "vitest";
import { cleanup } from "@testing-library/react";

// Vitest runs without `globals`, so React Testing Library cannot register its own
// afterEach — unmount whatever a page test rendered before the next one starts.
afterEach(() => {
  cleanup();
});
