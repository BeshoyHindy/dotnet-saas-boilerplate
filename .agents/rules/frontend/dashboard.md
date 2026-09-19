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

## Sign-in

`login()` needs a tenant, and it is the one place a caller may name one (ADR-0002). Keep tenant
resolution claim/route based: never add a server endpoint that maps an email to tenants or lists
them for an anonymous caller — that is user and tenant enumeration.
