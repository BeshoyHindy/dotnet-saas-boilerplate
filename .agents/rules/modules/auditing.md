# Module: Auditing

Append-only audit trail (entity changes, security events, exceptions, HTTP activity) with async channel-buffered persistence + DLQ. Module `Order = 300`.

**Entities / DbContext:** `AuditRecord`, `AuditDbContext`. `AuditEnvelope` is the in-flight event. Rich Contracts surface: `IAuditClient`, `ISecurityAudit`, `IAuditPublisher`, `IAuditSink`, `IAuditDlqSink`, `IAuditEnricher`, `NoAuditAttribute`, payload records.
**Areas:** read-only query side — GetAudits / ByCorrelation / ByTrace / Summary / Exception / Security. Full list: `Features/v1/` or `/scalar`.

## Gotchas

- **Static `Audit` fluent API** — `Audit.ForSecurity(...).WithUser(...).WriteAsync(ct)` (also `ForEntityChange`/`ForActivity`/`ForException`). Configured once at startup via `Audit.Configure(publisher, serializer, enrichers)`. Enrichers are held in a **volatile immutable array swapped atomically** — never mutate a live enricher list (it'd race the enrich loop).
- **Two interceptors, don't confuse them:** `AuditingSaveChangesInterceptor` (this module) captures EF entity diffs → EntityChange events and **skips `AuditDbContext`** (no recursive self-audit). `AuditableEntitySaveChangesInterceptor` (BuildingBlocks) stamps audit/soft-delete fields — different file, different job.
- **Channel-buffered, never blocks the request** — `ChannelAuditPublisher` has two lanes: default (`DropOldest` under pressure) and a **security lane that back-pressures and never drops** (login/permission/impersonation ride here). `AuditBackgroundWorker` drains both (security first), batches, writes via `IAuditSink`; on sink failure it retries then spills to `IAuditDlqSink` (file) so events survive a Postgres outage.
- `SqlAuditSink` groups a batch by `TenantId` and writes each group inside `ITenantScope.RunAsync` (null → Root) — the background writer has no ambient tenant, and `AuditDbContext` must be built under the tenant whose rows it holds. A tenant deleted between the event and the flush is logged and skipped, not fatal to the batch.
- **JSON masking** redacts fields by keyword (password/secret/token/apiKey/connectionString…) → `****`. Add sensitive keys there.
- Exclude an endpoint from activity auditing with `[NoAudit]` / the `NoAudit` endpoint extension.
- **Entity-diff masking** — `EntityDiffBuilder` masks any changed property whose name matches the one canonical `SensitiveFieldNames` list (`BuildingBlocks/Shared/Security`, which includes `securitystamp`) to `****` in both old and new value; a null value stays null. There is no second keyword list for entity diffs — add a name to `SensitiveFieldNames` instead. `IAuditExempt` (`Modules.Auditing.Contracts`) is the stronger opt-out: an entity implementing it is skipped by `AuditingSaveChangesInterceptor` entirely, not merely masked. **Existing `AuditRecords` rows written before this change still carry clear-text `PasswordHash`/`SecurityStamp`/token hashes and are not cleaned up automatically.** A live deployment that wants them scrubbed runs a one-off against the `jsonb` payload, naming every property `SensitiveFieldNames` matches (the SQL below only checks the exact names, not its substring/trailing-word rules, so widen the `ANY (...)` list to match what actually shows up):
  ```sql
  UPDATE audit."AuditRecords"
  SET "PayloadJson" = jsonb_set(
        "PayloadJson",
        '{changes}',
        (SELECT jsonb_agg(
                  CASE WHEN elem->>'name' = ANY (ARRAY['PasswordHash', 'SecurityStamp', 'RefreshTokenHash'])
                       THEN elem
                            || (CASE WHEN elem ? 'oldValue' THEN jsonb_build_object('oldValue', '****') ELSE '{}'::jsonb END)
                            || (CASE WHEN elem ? 'newValue' THEN jsonb_build_object('newValue', '****') ELSE '{}'::jsonb END)
                       ELSE elem
                  END)
         FROM jsonb_array_elements("PayloadJson" -> 'changes') elem))
  WHERE "EventType" = 1 -- EntityChange
    AND "PayloadJson" ? 'changes';
  ```
