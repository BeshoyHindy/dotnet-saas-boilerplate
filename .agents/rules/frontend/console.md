# Frontend — the console (`clients/console`)

There is ONE client (ADR-0004): the console, at `clients/console`. It serves tenant users and root
operators alike — operator screens (tenants, impersonation) sit in the same app behind permissions.
Read this for any React work.

Stack: React 19 · Vite 7 · TypeScript · TanStack Query v5 · React Router 7 · Radix UI · Tailwind v4 ·
class-variance-authority (shadcn-style). Path alias `@` → `src` (`vite.config.ts`).

## API client (`src/lib/api-client.ts`, `src/api/*`) — generated, never hand-written

- **No hand-written API types.** `clients/openapi/v1.json` is the contract (exported from the API by
  `bash scripts/export-openapi.sh`); `pnpm generate:api` turns it into `src/api/schema.d.ts`. Every DTO
  is `Schemas["Name"]` — i.e. `components["schemas"]["Name"]`. Both sides are drift-gated in CI.
- One client: `api` in `src/lib/api-client.ts`, an `openapi-fetch` client typed by `paths`. Call it as
  `unwrap(await api.GET("/api/v1/identity/users/{id}", { params: { path: { id } } }))`; `unwrapVoid`
  for 204s. `src/api/{feature}.ts` stays a thin, named wrapper per endpoint. No axios, no `apiFetch`.
- `unwrap` throws `ApiRequestError(status, message, problem)` on a non-OK response, parsing RFC 9457
  `application/problem+json` off `error`.
- Auth header `Authorization: Bearer <access>` is added by the client's own `fetch`, and it
  single-flights a refresh-and-retry on 401 — except while an exchanged (impersonation) token is
  installed, which has no refresh cookie.
- **No tenant header.** The server reads the tenant from the token's `tenant` claim (ADR-0002). The
  only place a tenant is named is the anonymous auth URLs, via `authPath(tenant, segment)`, and the
  tenant there must be the tenant **Id** from the claim — the refresh cookie's `Path` names it.
- **The refresh token is never in JavaScript.** It is an `HttpOnly; SameSite=Strict` cookie; the store
  keeps only the access token. That works because the console is served from the API's origin (nginx
  proxies `/api`, and so does the Vite dev server) — do not "fix" a CORS error by pointing the client
  at another origin.
- Query keys are PascalCase where the endpoint's are (`PageNumber`, `PageSize`, `Search`); the
  generated types tell you which.

## Env (`src/env.ts`) — runtime, not build-time

`loadRuntimeConfig()` fetches `/config.json` once at boot (awaited in `main.tsx` before React mounts); `env` is a getter that throws if read too early. One built image promotes across environments — the container entrypoint renders `config.json`, the nginx site and the CSP from `APP_*` variables. The only `VITE_*` var, `VITE_API_BASE_URL`, configures the **Vite dev proxy target only**; the runtime `apiBase` is `""` (same origin) in every environment.

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
Login is `POST /api/v1/tenants/{tenant}/auth/token` — the tenant is a path segment, the one place a caller may name one. No `X-Client-App` header: the SuperAdmin/dashboard app boundary it fed has no meaning with one console. localStorage keys are `boilerplate.console.*` and hold the access token, the tenant Id and the permission set — never a refresh token.

**Permissions are not in the JWT** (it carries role names). `AuthProvider` fetches them from
`GET /api/v1/identity/permissions` on every subject change (including entering a tenant) and exposes
`permissionsHydrated` so gated UI does not flash. Gate a *route* with
`<RouteGuard perms={[IdentityPermissions.Users.View]}>` using the constants in `src/lib/permissions.ts`
— which holds only what routes gate on; the role editor reads the server's catalog endpoint. Gate a
*nav item* with `perm`/`anyPerm` in `src/components/layout/nav-data.ts`. Mirror the permission the
server endpoint enforces, never a broader one.

**Entering a tenant** (operator): `useAuth().beginImpersonation(...)` installs the exchanged
access-only token and stashes the operator's own session; `stopImpersonation()` restores it. It happens
in place — there is no second app to hand off to.

## Design system (Tailwind v4, shadcn-style)

- **`cn()` is at `src/lib/cn.ts`** (`twMerge(clsx(...))`) — not `lib/utils.ts`. `components.json`: `style:new-york`, `baseColor:slate`, `cssVariables:true`, `iconLibrary:lucide`.
- UI primitives in `src/components/ui/` are cva-based: `cva(base, { variants, defaultVariants })` + Radix `Slot`/`asChild` + `cn(buttonVariants({...}))`. Layout primitives live in `src/components/list/`, re-exported from `index.ts`.
- **Tailwind v4 is CSS-first — there is NO `tailwind.config`.** Configured via the `@tailwindcss/vite` plugin and one entrypoint `src/styles/globals.css` (imported in `main.tsx`). Tokens: `:root` oklch primitives → semantic vars → an `@theme inline { --color-*: var(--…) }` block exposing them as utilities. `@custom-variant dark (&:is(.dark *))`.
- Add a new token in `globals.css` (primitive → semantic → `@theme inline`), then use the utility. Don't hard-code colors in components.

### Design language

Chroma-0 neutrals (`--neutral-*: oklch(L 0 0)` — untinted), a rose default brand with swappable
`.accent-{rose,indigo,violet,sky,emerald,amber}` classes, saffron secondary, Figtree/Outfit/JetBrains
Mono. The operator screens were re-toned to these tokens when they came over from the admin app —
there is no second palette any more.

## Testing — Vitest units + a SMALL Playwright smoke suite

- `pnpm test` runs Vitest (jsdom) over `src/**/*.test.ts`, next to the code. Pure logic belongs here:
  the API client's error mapping, the token store, audit humanisation. Prefer adding one of these.
- `pnpm test:e2e` runs the Playwright **smoke** suite — sign-in, user CRUD, operator enters a tenant.
  It is deliberately small (ADR-0004); do not grow a page-per-spec suite back into it.
- `playwright.config.ts`: `testDir: ./tests`, chromium, auto-boots `pnpm dev`, no real backend.
- **JWT seeding:** `seedAuthedSession(page, TEST_USER)` builds a fake JWT and `addInitScript`-writes `boilerplate.console.*` to localStorage before React boots (server isn't called, so signature is junk).
- **Route mocking:** `mockJsonResponse(page, urlGlob, body)` / `mockProblemDetails(...)`. `installShellMocks(page)` stubs every call `AppShell` fires. Playwright matches most-recently-registered first → broad shell mocks in `beforeEach`, page-specific mocks after (they win).
- `beforeEach`: `seedAuthedSession(page, TEST_USER)` → `installShellMocks(page)`.

## Add a page/feature

0. Contract first: if the endpoint is new, `bash scripts/export-openapi.sh` then `pnpm generate:api`
   and commit both artifacts. Nothing below can be typed until the contract knows about it.
1. API: extend `src/api/{feature}.ts` — `Schemas[...]` aliases + one `api.VERB(...)` wrapper each.
2. Page: `src/pages/{area}/{name}.tsx`, **named** export. `useQuery` with hierarchical key; `useMutation` invalidating in `onSuccess`, passing per-call data via `mutate(arg)`.
3. Route: add `const X = lazyNamed(() => import("@/pages/area/name"), "XPage")` and a child route under `AppShell`.
4. Test: a Vitest unit beside the code for its logic. Only touch the smoke suite if the page is one
   of the three journeys it covers.

Forms: hand-rolled controlled inputs are the norm; `react-hook-form` + `zod` came over with the
operator screens and is the right tool for a long, validated form (see
`src/components/tenants/create-tenant-dialog.tsx`). Wrap every route element in `withSuspense(...)`.
Long lists use `@tanstack/react-virtual`. Keep neutrals at chroma 0 and add tokens in
`src/styles/globals.css` (primitive → semantic → `@theme inline`), never hard-coded colours.
