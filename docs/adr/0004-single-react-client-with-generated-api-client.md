---
status: accepted
---
# One React client, typed from a checked-in OpenAPI document

The template ships a single frontend, `clients/web` (Vite, React, TypeScript, TanStack Query, Tailwind, Radix), instead of the upstream's separate operator and tenant apps. Operator-only screens (tenant management) live in the same app behind permissions. Two apps double the build, test and deploy surface for a template whose UI every product replaces.

The API contract is a checked-in document, `clients/openapi/v1.json`, exported from the API build by `scripts/export-openapi.sh`. The client generates types with `openapi-typescript` and calls through `openapi-fetch`, so requests and responses are type-checked without a heavyweight generated SDK. Drift is blocked on both sides in CI: the backend job re-exports and fails on any `git status` change under `clients/openapi/`; the frontend job regenerates and fails on any change to the generated types.

The client is served by an nginx container with a Content-Security-Policy, on the same registrable domain as the API, because the refresh token is a `SameSite=Strict` cookie (ADR-0002).
