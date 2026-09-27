import type { ReactElement } from "react";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { MemoryRouter, Route, Routes } from "react-router-dom";
import { render } from "@testing-library/react";

/**
 * Render one page the way the router would, for a page test (`src/pages/**\/*.test.tsx`).
 *
 * Each call gets its own QueryClient with retries off, so an error state shows on the
 * first failure instead of after the shared client's back-off, and no cached data leaks
 * between tests. `path` is the route pattern (e.g. `/identity/users/:userId`) and `url`
 * the address to open, so `useParams` sees what it would in the app.
 *
 * The page's API module (`@/api/*`) and `@/auth/use-auth` are mocked by the test itself
 * with `vi.mock` — a page test never reaches `fetch` or the real AuthProvider.
 */
export function renderPage(
  page: ReactElement,
  { path = "/", url = path }: { path?: string; url?: string } = {},
) {
  const queryClient = new QueryClient({
    defaultOptions: { queries: { retry: false }, mutations: { retry: false } },
  });
  return render(
    <QueryClientProvider client={queryClient}>
      <MemoryRouter initialEntries={[url]}>
        <Routes>
          <Route path={path} element={page} />
        </Routes>
      </MemoryRouter>
    </QueryClientProvider>,
  );
}

/** A promise that never settles — holds a query in its loading state. */
export function pending<T>(): Promise<T> {
  return new Promise<T>(() => {});
}
