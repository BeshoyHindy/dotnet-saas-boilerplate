# Quality gaps the two React clients hand to a first product

Research for [#130](https://github.com/BeshoyHindy/dotnet-saas-boilerplate/issues/130), part of the map [#87](https://github.com/BeshoyHindy/dotnet-saas-boilerplate/issues/87).

- **Standard:** [WCAG 2.2](https://www.w3.org/TR/WCAG22/) AA success criteria for the accessibility half, plus the [Vite](https://vite.dev/guide/features.html#build-optimizations) and [React](https://react.dev/reference/react/lazy) docs for the code-splitting claims. Everything else (error/empty/loading states, error surfacing, test coverage) is assessed straight against the code and this repo's own conventions in `.agents/rules/frontend/*.md`.
- **This repo:** `develop`, reviewed on branch `research/client-quality`.
- **Method:** read `clients/dashboard` and `clients/console` — the API client and query-client error plumbing, auth/session/acting-token lifecycle, the shared `Field`/dialog/design-token primitives, representative list and detail pages, `eslint.config.js`, `vite.config.ts`, `routes.tsx`, and every `src/**/*.test.ts(x)` file — and ran `pnpm install && pnpm build` in both to read real bundle output. Both clients share most of this code on purpose (ADR-0008 duplication), so most findings apply to both; where one client's extra surface (the acting layer) is involved, that's called out.
- **Out of scope by instruction:** Aspire container issues, first-compose cost, the `libgssapi` log, the per-process idempotency lock, dependency versions, the object store.

Sizes: S/M/L per this repo's own convention (see `upstream-delta.md`): S < 150 lines changed, M < 500, L beyond that.

## Summary

| id | client(s) | area | location | severity | fix size |
|---|---|---|---|---|---|
| C1 | both | expired session gives no explanation | `src/auth/protected-route.tsx`, `src/auth/auth-context.tsx` (silent-refresh catch) | medium | S |
| C2 | both | notification "mark read" mutations swallow failures | `src/components/notifications/notification-bell.tsx` | low | S |
| C3 | both | 37 render-correctness lint rules held at `warn`, not gating | `eslint.config.js` (`set-state-in-effect`, `static-components`, `refs`) | medium | L |
| C4 | both | no page/component-level test coverage | `src/**/*.test.tsx` (only `auth-context.test.tsx` exists per client) | medium | M |
| C5 | both | no bundle-size guardrail as the app grows | `vite.config.ts` (no `manualChunks`/visualizer) | low | S |
| C6 | console | an involuntary acting-session drop is communicated only by a transient toast | `src/auth/auth-context.tsx:310-324`, `src/components/layout/acting-banner.tsx` | medium | S |

Counts: **6 findings** — 0 high · 4 medium · 2 low.

## Findings

### C1. An expired or dead session redirects to `/login` with no explanation (both clients)

`src/auth/protected-route.tsx` (identical in both clients) renders a "Restoring your session…" spinner while `isInitializing`, then either renders the route or `<Navigate to="/login" replace state={{ from: location }} />`. The `state` it attaches carries only `from` (the post-login redirect target) — never a reason.

Two paths land here silently:
- **Boot-time silent refresh failure** — `src/auth/auth-context.tsx`'s boot effect (dashboard: lines ~109-124) calls `refreshAccessToken()` and, on any failure ("Refresh token dead (expired, revoked, or DB reseeded)"), calls `endSessionLocally()` with no message stored anywhere.
- **Mid-session refresh failure** — `src/lib/api-client.ts`'s `refreshAccessToken()` calls `endSessionLocally()` and throws `ApiRequestError(status, "Refresh failed")` on a non-OK refresh response; the query/mutation that triggered it gets a generic error, but by the time the user notices, `ProtectedRoute` has already redirected and the query's own error UI is gone.

`src/pages/login.tsx` has a `notice` state, but it's wired only to the demo-account picker's outcome (`setNotice(outcome.reason)`), not to "you were signed out." A user (or operator) whose session died simply reappears at a blank sign-in form, indistinguishable from having never been logged in — for a first product this reads as a bug ("did I lose my work?") rather than an expected, explained event.

**Severity: medium** — not a security or data-loss issue (nothing is lost; `endSessionLocally()` clears state correctly), but it's a real first-impression/support-ticket generator, and the ticket specifically calls out "an expired session" as a state to review. **Fix size: S** — thread a reason through `Navigate`'s `state` (or a `?reason=` query param, consistent with the existing `authPath` pattern) and have `login.tsx` turn it into the same `notice` banner it already renders for other cases.

### C2. Notification "mark as read" mutations have no `onError` (both clients)

`src/components/notifications/notification-bell.tsx`:

```
const markAllMutation = useMutation({
  mutationFn: markAllNotificationsRead,
  onSuccess: () => { /* optimistic cache updates */ },
});

const markOneMutation = useMutation({
  mutationFn: (id: string) => markNotificationRead(id),
  onSuccess: () => { /* cache invalidation */ },
});
```

Neither mutation has an `onError`. Every other `useMutation` call site in both clients that this review sampled (dialogs, identity pages, settings) pairs its `useMutation` with an `onError` that surfaces `ApiRequestError`'s `problem.detail`/`title` via `sonner`'s `toast.error(...)`; this is the one component in each client where a failed call (network blip, expired session mid-click) leaves the bell's unread count and inbox silently wrong with no feedback to the user and nothing in the console.

**Severity: low** — cosmetic/notification-badge drift only, no data at risk, and it self-heals on the next successful fetch (`staleTime: 30_000`). **Fix size: S** — add the same `onError: (err) => toast.error(...)` pattern already used everywhere else in both files.

### C3. 37 render-correctness lint rules are held at `warn`, not `error` (both clients)

`eslint.config.js` (byte-identical in both clients) documents, in its own comment, that `eslint-plugin-react-hooks` 7's `recommended` config adds 14 React Compiler rules at `error`, of which 11 already pass and are left gating; three are deliberately downgraded to `warn`:

```
'react-hooks/set-state-in-effect': 'warn',   // ~29 instances
'react-hooks/static-components': 'warn',     // ~5 instances
'react-hooks/refs': 'warn',                  // ~3 instances (use-inactivity-timeout.ts)
```

The comment is candid that these aren't style nits: `set-state-in-effect` flags effects that seed or reset state from props/queries ("doing that blind is how you introduce render loops"), and `refs` flags a ref read during render in `use-inactivity-timeout.ts`. `pnpm lint` runs as a required step in `.github/workflows/frontend.yml`, so these 37 sites can ship indefinitely without ever failing CI — they're visible in editor/CI output but never block a merge.

**Severity: medium** — the rules exist specifically because the underlying pattern (state derived from an effect instead of during render) is a documented source of extra renders and, in the worst case, render loops; 37 live instances is a nontrivial surface for a first product to inherit un-triaged. **Fix size: L** — per the repo's own comment, each `set-state-in-effect`/`static-components` site is a real design change (derived state or a key-reset, or hoisting a component out of its parent), not a mechanical fix.

### C4. No page- or component-level Vitest coverage in either client (both clients)

Per client, `src/**/*.test.tsx` contains exactly one file — `auth-context.test.tsx` — and every other test file is `*.test.ts` covering pure logic (`api-client.test.ts`, `query-client.test.ts`, `token-store.test.ts`, `tenant-resolution.test.ts`, `operator-gate.test.ts` in console, `tenants.test.ts`, `audits.test.ts`, `nav-targets.test.ts`, `login.demo-accounts.test.ts` in dashboard). This matches `.agents/rules/frontend/clients.md`'s stated convention ("Pure logic belongs here … Prefer adding one of these") — but the consequence is that nothing under `pnpm test` renders a single page component, so:

- The loading/empty/error branches this review found well-implemented on representative pages (e.g. `src/pages/identity/users.tsx:198-276`, console's `src/pages/tenants/list.tsx`) have no regression test at all beyond manual review and type-checking.
- Every `onError`/`toast.error(...)` pairing described in the "checked and sound" section below is likewise unverified by any automated test.
- The two Playwright smoke suites are deliberately tiny by design (`clients.md`: "do not grow a page-per-spec suite back into either") and only cover sign-in plus one CRUD/operator-entry journey per client — they exercise none of the above.

**Severity: medium** — for a first product, a regression in any list page's empty-state copy, an accidentally-removed `onError`, or a broken loading skeleton would ship undetected; this is exactly the gap the ticket asks to name ("what a product would change first"). **Fix size: M** — a handful of React Testing Library tests for the highest-traffic list/detail pages (loading → data → empty → error) would close most of the gap without growing into a page-per-spec suite.

### C5. No bundle-size guardrail, though today's numbers are healthy (both clients)

`vite.config.ts` (identical shape in both clients) has no `build.rollupOptions.output.manualChunks` and no bundle-analysis plugin. `routes.tsx` in both clients lazy-loads all 25 page routes via the shared `lazyNamed()` helper, so this isn't currently a problem: a real `pnpm build` in each client (run for this review) produced:

- **dashboard:** `dist/` = 1.3 MB total; entry chunk `index-*.js` = 209 KB raw / 65.8 KB gzip; next-largest named chunks `search-*.js` (93.5 KB / 31.1 KB gzip) and `query-client-*.js` (78.8 KB / 22.8 KB gzip, an artifact of Rollup's auto-chunk naming, not a literal "query client" module).
- **console:** `dist/` = 1.4 MB total; entry chunk `index-*.js` = 209 KB raw / 65.7 KB gzip; largest named chunks `query-client-*.js` (125 KB / 34.9 KB gzip) and `schemas-*.js` (111 KB / 33.9 KB gzip, the generated OpenAPI schema module).

**Severity: low** — nothing here is oversized for a React 19 + Radix + TanStack Query SPA today, and every route is already route-split. Flagged only because there is no guardrail (`size-limit`, a Rollup visualizer, or a CI budget check) to catch this creeping as pages are added. **Fix size: S** — add a `size-limit`/`rollup-plugin-visualizer` check to `frontend.yml`'s build step.

### C6. An involuntary acting-session drop is communicated only by a transient toast (console only)

`src/lib/api-client.ts`'s `authFetch` correctly detects a 401 on the acting token specifically (not the operator's own session) and calls `actingStore.drop(reason)` with a clear, specific message ("Your session inside {tenant} ended (revoked or expired). You are back in your own account."). `src/auth/auth-context.tsx:310-324` subscribes to the store, consumes that notice, and shows it via `toast.warning("Stopped acting", { description: notice })`, then clears the query cache. `ActingBanner` (`src/components/layout/acting-banner.tsx:47-48`), which is the operator's persistent, `role="status" aria-live="polite"` indicator that they're acting, simply disappears the moment the drop happens — it carries no memory of *why* it went away.

The design is otherwise sound (this review verified the 401-vs-refresh branching is unit-tested in `api-client.test.ts`), but the sole record of *why* an operator was bounced out of a tenant mid-task is a `sonner` toast, which auto-dismisses and is easy to miss if the operator wasn't looking at the screen at that exact moment (mid-scroll, mid-dialog, tabbed away). Given the console's acting layer is explicitly the higher-blast-radius surface in this codebase (cross-tenant power, per `.agents/rules/frontend/console.md`), an operator who doesn't catch the toast has no way to later ask "why am I back in my own account?" — there's no persisted banner or history to check.

**Severity: medium** — no data or security exposure (the drop itself is correct and safe), but a confusing, easy-to-miss UX moment on the one surface where "did something just change under me?" matters most. **Fix size: S** — keep `ActingBanner` (or a short-lived successor) visible with the drop reason for a few seconds instead of only firing a toast, or add the notice to a small persistent log the operator can check.

## Checked and sound

- **jsx-a11y is genuinely enforced, not just installed.** `eslint.config.js` (both clients) extends `jsx-a11y/recommended` and additionally promotes `no-noninteractive-element-interactions`, `no-noninteractive-element-to-interactive-role`, `no-aria-hidden-on-focusable` and `anchor-has-content` to `error`; the only rule turned off is `no-autofocus`, with a documented, narrow justification (destructive confirmation dialogs, the command-palette search input). `pnpm lint` is a required step in `.github/workflows/frontend.yml` for both clients (`client` job → `Lint` step), so this isn't a config that could silently rot.
- **Forms wire ARIA correctly.** The shared `Field` component (`src/components/list/field.tsx`, both clients) automatically threads `aria-describedby`/`aria-invalid` onto its child control and renders the error string with `role="alert"`; console's `create-tenant-dialog.tsx` correctly passes `error={errors.field?.message}` from `react-hook-form` into every `Field` instance (lines 348, 372, 418, 436, 466, 503).
- **Keyboard/focus basics are handled.** Both `AppShell`s render a first-focusable, initially-`sr-only` "Skip to main content" link (console: `src/components/layout/app-shell.tsx:19-30`) that becomes visible on focus; both clients' `globals.css` define a global `:focus-visible { outline: 2px solid var(--color-ring); outline-offset: 2px; }` with a comment explaining the deliberate move away from a box-shadow halo. Dialogs/dropdowns are all built on `@radix-ui/react-dialog`/`@radix-ui/react-dropdown-menu`, which supply focus trapping and restoration per Radix's own accessibility documentation; no icon-only button (`size="icon"`) was found without an accompanying `aria-label`.
- **Color contrast was deliberately tuned, not accidental.** `src/styles/globals.css` (both clients) sets `--muted-foreground: oklch(0.500 0 0)` in light mode and `oklch(0.730 0 0)` in dark mode against `--background`/`--neutral-850` respectively — comfortably inside WCAG 2.2 AA's 4.5:1 text-contrast threshold — and a full primitive → semantic → `@theme inline` token pipeline means no component hard-codes a color outside it.
- **API errors are surfaced, not swallowed, almost everywhere.** `ApiRequestError` carries the parsed RFC 9457 `problem+json` body; the large majority of `useMutation` call sites sampled across both clients pair an `onError` with `toast.error(description: problem?.detail ?? problem?.title ?? message)` (e.g. console's `create-tenant-dialog.tsx:276-282`, `impersonate-dialog.tsx:347-353`). The one gap found is C2, above.
- **`endSessionLocally()` is a real single choke point.** Both clients' `query-client.ts` document and enforce it as the one place that clears `tokenStore`/`actingStore`/query cache together, and every "session over" path (`refreshAccessToken`'s failure branch, `authFetch`'s token-gone branch, the boot silent-refresh catch) calls it rather than clearing state by hand.
- **The acting-session 401 vs. refresh distinction is correct and unit-tested.** `src/lib/api-client.ts`'s `authFetch` captures the acting `jti` before the first send, distinguishes a 401 on the acting token (drop, no refresh spent) from a 401 on the operator's own token (normal refresh-and-retry), and guards against identity drift mid-flight (a synthetic 401 if the acting session changed between capture and retry) — all covered by scenario tests in `api-client.test.ts`.
- **Code-splitting is comprehensive.** Every one of the 25 routes in both clients' `routes.tsx` is wrapped in the shared `lazyNamed()` helper and rendered inside `<Suspense fallback={<RouteFallback />}>`, whose fallback itself carries `role="status" aria-busy="true"` plus an `sr-only` "Loading…" label — so route-level code splitting doesn't come at the cost of an unannounced loading state.
- **Representative list pages cover loading, empty and error states.** `src/pages/identity/users.tsx:198-276` (dashboard) and console's `src/pages/tenants/list.tsx` both render a loading state, an explicit empty state (`EntityEmpty`), and an error band sourced from `query.error`, matching the pattern described (but not test-covered — see C4) across the rest of both codebases.
