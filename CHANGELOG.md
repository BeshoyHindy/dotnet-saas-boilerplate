# Changelog

Changes to the template itself. A scaffolded product does not receive this file: it starts from the
tagged state and keeps its own history.

## 1.1.0

The first release a real product is meant to scaffold from.

### Security

- A revoked session is refused on its next request (per-request `sid` check, 30 s per-instance cache).
- Deactivating or deleting a user revokes every session and bumps the security stamp in one save.
- Passwords: a bundled common-password list replaces the digit/upper/lower rules; minimum 10
  characters everywhere, tenant admins included.
- Uploads: finalize checks the first 1 KiB against the extension and declared type; script-capable
  types are refused unless a category opts in; a failed malware scan no longer lets a file through.
- Refresh cookie is `__Secure-refresh_token`; JWTs are accepted only with HS256.
- Entity-change audit diffs mask sensitive values; `IAuditExempt` skips an entity.
- The restricted CORS policy no longer carries a hidden `*` header and method.
- Production refuses RustFS's default credential as a placeholder secret.

### Behaviour

- Root operators open the Job monitor (Hangfire) from the console via a 15-minute `/jobs` cookie;
  `Hangfire.Manage` gates retry and delete (ADR-0009).
- Product modules are a documented, tested path (ADR-0010).
- Profile updates use ETag / If-Match and answer 412 on a stale write.
- A client disconnect no longer undoes an idempotent request.
- Auth mail is encoded HTML with a plain-text alternative; operators' mail links to the console.
- Both clients say why a session ended; the console keeps the acting-drop reason on screen.

### Breaking API changes

- `GET /identity/users` is removed; use the paged `GET /identity/users/search`.
- Groups, group members, a user's groups and notifications are paged (`PagedResponse<T>`), with
  PascalCase query parameters; the notifications page cap is 100.
- `GET /audits/security` no longer takes a `TenantId` parameter.

### Operations

- RustFS 1.0.0 replaces MinIO (compose, AppHost, tests, Dokploy); env names are `STORAGE_*`.
- Scheduled `pg_dump` backups with a documented restore; processed outbox/inbox rows are purged.
- Every container has a memory limit, rotated logs, and every image is pinned by digest.
- Logs carry the exception, request context and the right level; JSON in Production; an access log;
  `/health` is rate-limited; an opt-in local OTLP receiver (`docker compose --profile otel up`).
- Job and outbox spans link back to the request that caused them.
- Lists are paged and indexed (trigram search on users and groups).
- All dependencies on current stable versions (.NET 10.0.12, Aspire 13.5.4, Node 24 LTS); Asp.Versioning
  is held at 10.0.0 (see the pin in `src/Directory.Packages.props`).

### Template

- The API-only scaffold (`--frontend false`) is green and self-consistent.
- A scaffold's docs match what it contains; the first run is ordered (git init, chmod, commit).
- Template issue numbers and upstream names are gone from product code.
- The sandcastle pipeline waits out a usage limit, can fall back to a second account, and takes the
  round cap and planner queue depth from the environment. Rebuild its sandbox image after upgrading.
