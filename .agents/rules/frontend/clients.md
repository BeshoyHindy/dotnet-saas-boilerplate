# Frontend — rules for BOTH clients

There are TWO clients (ADR-0008), each an independent pnpm project with its own lockfile,
Dockerfile, nginx template and test suites:

| | `clients/dashboard` | `clients/console` |
|---|---|---|
| Who signs in | a tenant's own users | root operators |
| Package | `@boilerplate/dashboard` | `@boilerplate/console` |
| Dev port | 5173 | 5174 |
| Image | `boilerplate-dashboard` | `boilerplate-console` |
| localStorage prefix | `boilerplate.dashboard.*` | `boilerplate.console.*` |

**Everything in this file applies to both.** The console's extra surface — the acting layer
and the operator gate — is in `console.md`; what the dashboard deliberately does not have is
in `dashboard.md`. Read this file plus the one for the client you are changing.

**Duplication between the two trees is accepted** (ADR-0008): `src/lib`, `src/auth`,
`src/components/ui` exist twice on purpose. Do NOT introduce a shared workspace package —
each image is built with its own client directory as the Docker context. Fixing a bug in
shared-looking code? Check whether the other client has the same copy, and fix both.

Stack: React 19 · Vite 7 · TypeScript · TanStack Query v5 · React Router 7 · Radix UI · Tailwind v4 ·
class-variance-authority (shadcn-style). Path alias `@` → `src` (`vite.config.ts`).

## API client (`src/lib/api-client.ts`, `src/api/*`) — generated, never hand-written

- **No hand-written API types.** `clients/openapi/v1.json` is the ONE contract for both clients
  (exported from the API by `bash scripts/export-openapi.sh`); each client's `pnpm generate:api`
  turns it into its own `src/api/schema.d.ts`. Every DTO is `Schemas["Name"]` — i.e.
  `components["schemas"]["Name"]`. Both clients and the backend are drift-gated in CI, and
  `bash scripts/check-openapi-drift.sh frontend` regenerates **both** clients.
- One client per app: `api` in `src/lib/api-client.ts`, an `openapi-fetch` client typed by `paths`.
  Call it as `unwrap(await api.GET("/api/v1/identity/users/{id}", { params: { path: { id } } }))`;
  `unwrapVoid` for 204s. `src/api/{feature}.ts` stays a thin, named wrapper per endpoint. No axios,
  no `apiFetch`.
- `unwrap` throws `ApiRequestError(status, message, problem)` on a non-OK response, parsing RFC 9457
  `application/problem+json` off `error`.
- Auth header `Authorization: Bearer <access>` is added by the client's own `fetch`, and it
  single-flights a refresh-and-retry on 401.
- **No tenant header.** The server reads the tenant from the token's `tenant` claim (ADR-0002). The
  only place a tenant is named is the anonymous auth URLs, via `authPath(tenant, segment)`, and the
  tenant there must be the tenant **Id** from the claim — the refresh cookie's `Path` names it.
- **The refresh token is never in JavaScript.** It is an `HttpOnly; SameSite=Strict` cookie; the store
  keeps only the access token. That works because each client is served from the API's origin (nginx
  proxies `/api`, and so does the Vite dev server) — do not "fix" a CORS error by pointing a client
  at another origin.
- Query keys are PascalCase where the endpoint's are (`PageNumber`, `PageSize`, `Search`); the
  generated types tell you which.

## Env (`src/env.ts`) — runtime, not build-time

`loadRuntimeConfig()` fetches `/config.json` once at boot (awaited in `main.tsx` before React mounts); `env` is a getter that throws if read too early. One built image promotes across environments — the container entrypoint renders `config.json`, the nginx site and the CSP from `APP_*` variables. The only `VITE_*` var, `VITE_API_BASE_URL`, configures the **Vite dev proxy target only**; the runtime `apiBase` is `""` (same origin) in every environment. A new runtime setting means all three of: the `RuntimeConfig` type, `docker/config.json.template`, and a default in `docker/docker-entrypoint.sh`.

## Data fetching (TanStack Query v5)

- Shared `queryClient` (`src/lib/query-client.ts`): `staleTime: 30_000`, `refetchOnWindowFocus:false`, no retry on 401/403 else `failureCount < 2`.
- **Query keys are inline literal arrays**, hierarchical, params object last: `["users", {pageNumber, searchTerm}]`, `["user", id]`, `["user", id, "roles"]`. No central key factory.
- `useQuery`/`useMutation` live inline in page components. Invalidate in `onSuccess`: `queryClient.invalidateQueries({ queryKey: ["users"] })`. Pagination: `placeholderData: keepPreviousData`.

### ⚠️ The `mutate(arg)` race-safe pattern (golden rule #9)

`useMutation` reads its options at execute time, so values produced at call time (e.g. a fresh
`crypto.randomUUID()` client id) must ride **through `mutate(arg)`** and be read from the `variables`
argument of `onMutate`/`onSuccess`/`onError` — never from component state the callbacks close over,
or two rapid calls collide.

```ts
mutation.mutate({ text, clientId: crypto.randomUUID() });
// onMutate: ({ clientId }) => insert optimistic `temp:${clientId}`
// onSuccess: (real, { clientId }) => swap temp → real
// onError:   (_e, { clientId }) => rollback
```

## Routing (`routes.tsx`, `App.tsx`)

- `createBrowserRouter`, flat config. Pages are **named exports** loaded via a `lazyNamed(importer, name)` helper (adapts named → `React.lazy`'s default contract). No default exports.
- Nesting: public auth routes → `<ProtectedRoute/>` → `<AppShell/>` → page children. `errorElement: <RouteError/>`.
- Provider tree: `ThemeProvider > QueryClientProvider > AuthProvider > … > RouterProvider` + `sonner` `<Toaster/>`.

## Auth (`src/auth/`)

`token-store.ts` (localStorage + pub/sub), `jwt.ts` (`decodeJwt`), `AuthProvider`/`useAuth()`, `ProtectedRoute`, `RouteGuard`.
Login is `POST /api/v1/tenants/{tenant}/auth/token` — the tenant is a path segment, the one place a caller may name one. **The user never types a tenant**: the dashboard resolves it (`?tenant=` → subdomain → last used → `defaultTenant`), and the console always signs in to `defaultTenant`, since operators live in the root tenant. localStorage holds the access token, the tenant Id and the permission set — never a refresh token.

**Permissions are not in the JWT** (it carries role names). `AuthProvider` fetches them from
`GET /api/v1/identity/permissions` on every subject change and exposes `permissionsHydrated` so
gated UI does not flash. Gate a *route* with `<RouteGuard perms={[IdentityPermissions.Users.View]}>`
using the constants in `src/lib/permissions.ts` — which holds only what routes gate on; the role
editor reads the server's catalog endpoint. Gate a *nav item* with `perm`/`anyPerm` in
`src/components/layout/nav-data.ts`. Mirror the permission the server endpoint enforces, never a
broader one.

**One clear-site rule.** Every "this session is over" path — logout, a dead refresh
(`refreshAccessToken`'s non-OK branch), a token-gone 401 (`authFetch`'s `!accessToken` branch), boot's
failed silent refresh — MUST call `endSessionLocally()` (`src/lib/query-client.ts`), never clear
`tokenStore` by hand. It clears the token store and the query cache together (and, in the console,
the acting session), so a forced sign-out can never hand the next person who signs in on that tab a
stranger's cached data. A path the user did not ask for passes a reason —
`endSessionLocally("expired")` for a dead refresh or a failed boot restore of a session that
existed; the inactivity guard marks `"inactivity"` itself — and the login page turns it into its
notice banner (`signedOutNotice` in `src/auth/inactivity.ts`). A deliberate logout passes none.

## Render correctness — the React Compiler lint rules are errors

`pnpm lint` gates every `eslint-plugin-react-hooks` rule at `error`, including
`set-state-in-effect`, `static-components` and `refs`. What they mean in practice:

- **State that follows a prop or query is adjusted during render, not in an effect.** Keep the
  previous value in state and compare, or set it in the event handler that caused the change (a
  filter's `onChange` also resets `pageNumber`). A dialog that clears its form on close, a form
  seeded from a query's payload, and a sidebar following the route all use this:

  ```tsx
  const [wasOpen, setWasOpen] = useState(open);
  if (open !== wasOpen) {
    setWasOpen(open);
    if (!open) setName("");
  }
  ```
- **Derive, don't reset.** An image fallback remembers *which* `src` failed; a value that is
  meaningless without its input (`expiresAt ? label : null`) is computed from it.
- **Never create a component during render.** Pick an icon inside a small component that returns
  the JSX (`<EventTypeIcon eventType={…} />`), not `const Icon = iconFor(x)`.
- **Never read or write `ref.current` during render.** Mirror a prop into a ref from an effect.

## Design system (Tailwind v4, shadcn-style)

- **`cn()` is at `src/lib/cn.ts`** (`twMerge(clsx(...))`) — not `lib/utils.ts`. `components.json`: `style:new-york`, `baseColor:slate`, `cssVariables:true`, `iconLibrary:lucide`.
- UI primitives in `src/components/ui/` are cva-based: `cva(base, { variants, defaultVariants })` + Radix `Slot`/`asChild` + `cn(buttonVariants({...}))`. Layout primitives live in `src/components/list/`, re-exported from `index.ts`.
- **Tailwind v4 is CSS-first — there is NO `tailwind.config`.** Configured via the `@tailwindcss/vite` plugin and one entrypoint `src/styles/globals.css` (imported in `main.tsx`). Tokens: `:root` oklch primitives → semantic vars → an `@theme inline { --color-*: var(--…) }` block exposing them as utilities. `@custom-variant dark (&:is(.dark *))`.
- Add a new token in `globals.css` (primitive → semantic → `@theme inline`), then use the utility. Don't hard-code colors in components.
- Both clients share one design language: chroma-0 neutrals (`--neutral-*: oklch(L 0 0)` — untinted), a rose default brand with swappable `.accent-{rose,indigo,violet,sky,emerald,amber}` classes, saffron secondary, Figtree/Outfit/JetBrains Mono. There is no second palette; a token added to one client's `globals.css` usually belongs in the other's too.

## Testing — Vitest units + a SMALL Playwright smoke suite, per client

- `pnpm test` runs Vitest (jsdom) over `src/**/*.test.ts`, next to the code. Pure logic belongs here:
  the API client's error mapping, the token store, audit humanisation, tenant resolution. Prefer
  adding one of these.
- **Page tests** (`src/pages/**/*.test.tsx`, React Testing Library) cover the busiest list and
  detail page of each client — the dashboard's users list and user detail, the console's tenant
  registry and tenant detail — in each of their states: loading, data, empty, error. Render with
  `renderPage(<Page />, { path, url })` from `src/test/render-page.tsx` (a fresh `QueryClient`
  with retries off, inside a `MemoryRouter`), and `vi.mock` the page's `@/api/*` module and
  `@/auth/use-auth` — a page test never reaches `fetch` or the real `AuthProvider`. `pending()`
  holds a query in its loading state. Add a page here only when it is as central as these; the
  default for new logic is still a `*.test.ts`.
- `pnpm size` checks the bundle budget in `.size-limit.json` (size-limit, gzipped) against
  `dist/` — run it after `pnpm build`. CI fails a PR that takes the entry chunk, the entry
  stylesheet or the total route JS over its limit. Raise a limit on purpose, in the PR that earns
  it, and keep it ~15% above the new size.
- `pnpm exec playwright test --workers=1` runs the **smoke** suite — the dashboard: sign-in and user
  CRUD; the console: sign-in and an operator entering a tenant. Each is deliberately small
  (ADR-0008); do not grow a page-per-spec suite back into either.
- `playwright.config.ts`: `testDir: ./tests`, chromium, auto-boots `pnpm dev` (5173 dashboard, 5174
  console), no real backend.
- **JWT seeding:** `seedAuthedSession(page, TEST_USER)` builds a fake JWT and `addInitScript`-writes
  that client's `boilerplate.{app}.*` keys to localStorage before React boots (the server isn't
  called, so the signature is junk).
- **Route mocking:** `mockJsonResponse(page, urlGlob, body)` / `mockProblemDetails(...)`. `installShellMocks(page)` stubs every call `AppShell` fires. Playwright matches most-recently-registered first → broad shell mocks in `beforeEach`, page-specific mocks after (they win).
- `beforeEach`: `seedAuthedSession(page, TEST_USER)` → `installShellMocks(page)`.

## Add a page/feature

0. Contract first: if the endpoint is new, `bash scripts/export-openapi.sh` then `pnpm generate:api`
   **in each client that will call it**, and commit every artifact. Nothing below can be typed until
   the contract knows about it.
1. Decide **which client** it belongs to. A screen for a tenant's users goes in the dashboard; a
   screen about the platform, or one an operator needs while acting, goes in the console. If both
   need it, it is two pages — say so in the PR rather than importing across the boundary.
2. API: extend `src/api/{feature}.ts` — `Schemas[...]` aliases + one `api.VERB(...)` wrapper each.
3. Page: `src/pages/{area}/{name}.tsx`, **named** export. `useQuery` with hierarchical key; `useMutation` invalidating in `onSuccess`, passing per-call data via `mutate(arg)`.
4. Route: add `const X = lazyNamed(() => import("@/pages/area/name"), "XPage")` and a child route under `AppShell`.
5. Test: a Vitest unit beside the code for its logic. Only touch the smoke suite if the page is one
   of the journeys it covers.

Forms: hand-rolled controlled inputs are the norm; `react-hook-form` + `zod` is the right tool for a
long, validated form (see the console's `src/components/tenants/create-tenant-dialog.tsx`). Wrap every
route element in `withSuspense(...)`. Long lists use `@tanstack/react-virtual`.
