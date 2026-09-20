---
status: superseded
superseded-by: 0008
---
# One React client, typed from a checked-in OpenAPI document

> **Superseded by [ADR-0008](0008-two-clients-dashboard-and-console.md).** The single-client
> decision below was reversed: the template ships two clients again, `clients/dashboard` (the
> tenant app) and `clients/console` (the operator tool). Everything this ADR decided about the
> *contract* — the checked-in `clients/openapi/v1.json`, `openapi-typescript` + `openapi-fetch`,
> the two-sided drift gate, runtime `/config.json`, and same-origin nginx delivery — still holds
> and is restated in ADR-0008. Only "one app" is retracted. Kept for the record.

The template ships a single frontend, the **console**, `clients/console` (Vite, React, TypeScript, TanStack Query, Tailwind, Radix), instead of the upstream's separate operator and tenant apps. It is named "console", not "admin": it serves tenant users and root operators alike, and operator-only screens (tenant management) sit in the same app behind permissions. Two apps double the build, test and deploy surface for a template whose UI every product replaces.

The API contract is a checked-in document, `clients/openapi/v1.json`, exported from the API build by `scripts/export-openapi.sh`. The client generates types with `openapi-typescript` and calls through `openapi-fetch`, so requests and responses are type-checked without a heavyweight generated SDK. Drift is blocked on both sides in CI: the backend job re-exports and fails on any `git status` change under `clients/openapi/`; the frontend job regenerates and fails on any change to the generated types.

The client is served by an nginx container with a Content-Security-Policy, on the same registrable domain as the API, because the refresh token is a `SameSite=Strict` cookie (ADR-0002).
