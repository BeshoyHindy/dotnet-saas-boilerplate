# Frontend — the dashboard (`clients/dashboard`), the TENANT app

Read [`clients.md`](clients.md) first: everything about the API client, runtime env, data
fetching, routing, the design system and testing is shared with the console and is not repeated
here. This file is only what is TRUE OF THE DASHBOARD AND NOT OF THE CONSOLE.

The dashboard is the product — what a tenant's own users sign in to (ADR-0008). Dev port **5173**,
package `@boilerplate/dashboard`, image `boilerplate-dashboard`, localStorage prefix
`boilerplate.dashboard.*`. It is the origin the API puts in mailed links (`OriginOptions__OriginUrl`),
because password-reset and confirmation mails go to a tenant's users.

## What it has

Overview, My Files, identity administration for the signed-in tenant (users, roles, groups), audits,
health, sessions, trash, and settings — profile, security, appearance and **the tenant's own
branding**. Screens gate on the permission the server enforces; a tenant admin sees the
administration section, a basic member does not.

## What it deliberately does NOT have — and must not grow

- **No acting layer.** There is no `src/auth/acting-store.ts`, no `src/api/operator.ts`, no acting
  banner, and no same-tenant "Impersonate" action on the user detail page. Acting as another user —
  in any tenant, including one's own — is the console's job (ADR-0008, issue #9). This app holds
  exactly one credential: the signed-in user's own access token.
- **No `X-Console-As-Operator` sentinel.** With one credential there is nothing to opt out of, so
  `api-client.ts` has no `AS_OPERATOR` export. If you are copying transport code over from the
  console, drop it — a request in this app is always sent as the person who signed in.
- **No platform surface.** No tenant registry, no impersonation grants list, no cross-tenant audits.
  `src/api/tenants.ts` reaches only the caller's OWN tenant (`/tenants/me/status` and the
  current-tenant theme endpoints); `src/lib/permissions.ts` carries no `Permissions.Tenants.*` or
  `Permissions.Platform.*` constant. Adding one is the signal that the screen belongs in the console.

## Sign-in resolves the tenant, the user never types it

`src/auth/tenant-resolution.ts`, in order: `?tenant=` on the URL (mailed links carry it) → the
subdomain when the host has one (`acme.app.example.com`, `acme.localhost:5173`; never a bare host,
an apex, `www`, or an IP) → the tenant last used successfully on this device → `env.defaultTenant`.
When the query parameter or the subdomain answers, the field is not rendered at all: the form says
"Signing in to <tenant>" with a "Not your workspace?" escape that reveals it. Otherwise the field
appears **below** email and password, labelled "Workspace" and prefilled.

Only the tenant **id** is remembered (localStorage, `try/catch`, written only after a sign-in that
succeeded) — never a token. Keep it claim/route based (ADR-0002): never add a server endpoint that
maps an email to tenants or lists them for an anonymous caller — that is user and tenant enumeration.

The resolver is pure and unit-tested (`tenant-resolution.test.ts`); add a case there rather than
reasoning about hostnames in a component. Vite's `server.allowedHosts` must keep accepting
`*.localhost` so subdomain resolution can be exercised in dev.

## Demo mode (`env.demoMode`)

Off by default. When `APP_DEMO_MODE`/`VITE_DEMO_MODE` is on, the login page offers "Sign in with a
demo account" (`src/pages/login.demo-accounts.ts`, `src/components/auth/demo-accounts-dialog.tsx`):
picking an account signs in with that account's own tenant. The shared password is **never a literal
in the repo** — it comes from runtime config (`APP_DEMO_PASSWORD`/`VITE_DEMO_PASSWORD`). With demo
mode on and no password configured, `demoPickOutcome` returns `prefill`: the picker fills the tenant
and email and focuses the password field instead of signing in.
