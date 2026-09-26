# Security surface a first product inherits

Research for [#121](https://github.com/BeshoyHindy/dotnet-saas-boilerplate/issues/121), part of the map [#87](https://github.com/BeshoyHindy/dotnet-saas-boilerplate/issues/87).

- **Standard:** [OWASP ASVS 5.0.0](https://github.com/OWASP/ASVS/tree/v5.0.0_release) (tag `v5.0.0_release`), Level 1 requirements only (70 of 345 rows in the flat requirement export). Requirement text quoted below comes from `5.0/docs_en/OWASP_Application_Security_Verification_Standard_5.0.0_en.flat.json` in that repository.
- **This repo:** `develop` at `83f43cc`, reviewed on branch `research/security-surface`.
- **Method:** read the code under `src/` for authentication, sessions/tokens, tenant resolution and the tenant exchange, files, idempotency, mail, CORS/forwarded headers, and the operator surface, and checked each against every ASVS 5.0 Level 1 row in chapters V1–V15. Every finding and every "checked and sound" line cites the file and line it rests on. Findings only — no fixes were applied and no fix design is proposed; sizes are rough estimates for the decision ticket that graduates from this one ([#97](https://github.com/BeshoyHindy/dotnet-saas-boilerplate/issues/97)).
- **Upstream triage context:** [`docs/research/upstream-delta.md`](https://github.com/BeshoyHindy/dotnet-saas-boilerplate/blob/research/upstream-delta/docs/research/upstream-delta.md) on `research/upstream-delta`, read in full. A few of its already-identified items overlap this review's areas (mail encoding, audit-diff masking, CORS PATCH, the idempotency client-abort token); they are cross-referenced under [Noted from the upstream triage](#noted-from-the-upstream-triage) rather than re-derived.
- **Out of scope by instruction:** session revocation taking effect on the next request rather than immediately ([#118](https://github.com/BeshoyHindy/dotnet-saas-boilerplate/issues/118)), the tenancy guardrail gaps ([#117](https://github.com/BeshoyHindy/dotnet-saas-boilerplate/issues/117)), and the idempotency filter's documented per-process lock limit (`KeyedAsyncLock.cs`).

Sizes follow this repo's own convention (see `upstream-delta.md`): XS < 20 lines · S < 150 · M < 500 · L beyond that.

## Summary

| # | Area | Location | ASVS 5.0 L1 id | Severity | Fix size |
|---|---|---|---|---|---|
| 1 | Password policy | `src/Modules/Identity/Modules.Identity/IdentityModule.cs:165-168` | V6.2.5 | Low | XS |
| 2 | Password policy | registration / reset / change flow (no such check exists) | V6.2.4 | Medium | S |
| 3 | File upload content validation | `src/Modules/Files/Modules.Files/Services/NoOpFileScanner.cs`; `.../FinalizeUpload/FinalizeUploadCommandHandler.cs:63-72` | V5.2.2 | Medium | L (S/M for a minimal magic-byte check; the hook already exists) |
| 4 | Refresh-token cookie name | `src/Modules/Identity/Modules.Identity/RefreshTokenCookie.cs:49-59` | V3.3.1 | Low | XS |
| 5 | Mail HTML injection | `src/Modules/Identity/Modules.Identity/Events/UserRegisteredEmailHandler.cs`, `Services/UserPasswordService.cs` (already itemised in `upstream-delta.md`, commits `6f8d38d`/`27f23d6`) | V1.2.1 | Low | M |
| 6 | Disabling/deleting a user never revokes its sessions | `src/Modules/Identity/Modules.Identity/Services/UserStatusService.cs` (`ToggleStatusAsync`/`ApplyStatusChange`/`SaveAndAuditAsync`) | V7.4.2 | Medium | S |

Counts: **6 findings** — 0 high · 3 medium · 3 low.

## Findings

### 1. Password composition rules are enforced, which ASVS L1 asks to remove

`src/Modules/Identity/Modules.Identity/IdentityModule.cs:165-168`:

```csharp
options.Password.RequiredLength = IdentityModuleConstants.PasswordLength; // 10
options.Password.RequireDigit = true;
options.Password.RequireLowercase = true;
options.Password.RequireNonAlphanumeric = false;
options.Password.RequireUppercase = true;
```

**V6.2.5**: "Verify that passwords of any composition can be used, without rules limiting the type of characters permitted. There must be no requirement for a minimum number of upper or lower case characters, numbers, or special characters." `RequireDigit`, `RequireLowercase` and `RequireUppercase` are all `true`, so a 10-character all-lowercase passphrase is rejected even though it is stronger than many strings the policy accepts. `RequiredLength = 10` is itself sound and exceeds V6.2.1's 8-character floor.

**Severity: low** — this is a policy-shape violation (and works against the current NIST/ASVS guidance that length beats composition), not an exploitable weakness; a stricter-than-required policy does not create an attack path. **Fix size: XS** — flip three booleans.

### 2. No breached-password check on registration, reset or change

`src/Modules/Identity/Modules.Identity/IdentityModule.cs:162-178` — the `AddIdentity<AppUser, AppRole>(...)` registration configures `options.Password.*` and calls `.AddDefaultTokenProviders()`, but registers no `IPasswordValidator<AppUser>` beyond ASP.NET Core Identity's own composition-rule validator. `grep -rn "IPasswordValidator" src/` (excluding `obj`/`bin`) finds no implementation anywhere in the tree, and `grep -rn "pwned\|HaveIBeenPwned\|breach" src/` (excluding `obj`/`bin`) finds nothing either. `UserPasswordService.ResetPasswordAsync`/`ChangePasswordAsync` (`src/Modules/Identity/Modules.Identity/Services/UserPasswordService.cs:62-97`) call straight into `UserManager`, which runs whatever validators are registered — none of them check against a common/breached-password list.

**V6.2.4**: "Verify that passwords submitted during account registration or password change are checked against an available set of, at least, the top 3000 passwords which match the application's password policy." No such check exists anywhere in the password lifecycle.

**Severity: medium** — this is the control ASVS treats as the primary defence against credential-stuffing and trivial-password takeover, and it is entirely absent; the lockout policy (`IdentityModule.cs:173-175`, 5 attempts / 15 minutes) mitigates online brute force but not a first correct guess of a common password. **Fix size: S** — a FluentValidation rule backed by a bundled top-N list (no external service call needed for L1).

### 3. File content is never actually validated — only the attacker's own declared metadata is compared to itself

- `src/Modules/Files/Modules.Files/Services/IFileScanner.cs` documents itself as "Phase A ships a no-op default"; `NoOpFileScanner.cs` always returns `ScanStatus.Clean`.
- `FinalizeUploadCommandHandler.cs:63-72` rejects only when the storage-reported `Content-Type` differs from `asset.ContentType` — but `asset.ContentType` is exactly the value the same caller supplied in `RequestUploadUrlCommand.ContentType` before upload (`RequestUploadUrlCommandHandler.cs`), and the presigned PUT lets the browser set that same header. The check therefore verifies the client didn't lie to itself, not that the bytes match the declared type.
- `RequestUploadUrlCommandHandler.cs:45-51` only checks the file*name*'s extension against `category.AllowedExtensions` — again before any bytes exist.
- Files whose `Visibility` is `Public` are served back with `inline` `Content-Disposition` (`PublicFileUrlFactory.cs:45` builds the header; class doc at `:31`) so a browser renders them directly; private files can also be requested `inline` via `GetFileDownloadUrlQuery.Inline` (`GetFileDownloadUrlQueryHandler.cs:43`).

**V5.2.2**: "when the application accepts a file... it checks if the file extension matches an expected file extension and validates that the contents correspond to the type represented by the extension. This includes... checking the initial 'magic bytes'... For L1, this can focus just on files which are used to make specific business or security decisions." Avatars and theme assets rendered inline in a browser are exactly that category, and nothing here inspects actual file content.

**Severity: medium** — no known concrete exploit was demonstrated in this review (that would require enumerating every `category.AllowedExtensions` set and confirming an inline-rendered MIME type accepts attacker-supplied markup), but the gap is structural and the impact band (content-type confusion / stored content served inline) is real if any inline-eligible category allows an ambiguous type. **Fix size:** L for real scanning (ClamAV/equivalent, per the interface's own doc comment); S–M for a minimal magic-byte/signature check in `FinalizeUploadCommandHandler`, since the extension point already exists.

### 4. Refresh-token cookie doesn't use the `__Secure-` prefix

`src/Modules/Identity/Modules.Identity/RefreshTokenCookie.cs:25` declares `public const string Name = "refresh_token";`, and `Append` (`:49-59`) sets:

```csharp
HttpOnly = true, Secure = true, SameSite = SameSiteMode.Strict, Path = PathFor(tenantId)
```

**V3.3.1**: "Verify that cookies have the 'Secure' attribute set, and if the `__Host-` prefix is not used for the cookie name, the `__Secure-` prefix must be used for the cookie name." `Secure` is set, but the name is the bare `refresh_token` — neither prefix is used. (`__Host-` doesn't fit here on purpose: it forbids a non-root `Path`, and this cookie is deliberately scoped to `/api/v1/tenants/{tenantId}/auth/refresh` per tenant, so `__Secure-` is the applicable one.)

**Severity: low** — `Secure` already stops the browser sending the cookie over plain HTTP, which is the attack the prefix mainly guards against; the prefix adds a second, browser-enforced guarantee against a malicious cookie being planted by a sibling subdomain. **Fix size: XS** — change the one `Name` constant to `__Secure-refresh_token`; `Append`, `Delete`, `Read` and `DeleteWhenResponseStarts` all reference the constant rather than the literal, so nothing else in the file changes.

### 5. Mail HTML injection (cross-referenced from the upstream triage)

Already fully itemised in `upstream-delta.md` (commits `6f8d38d`/`27f23d6`): `Identity/Events/UserRegisteredEmailHandler.cs` interpolates `@event.FirstName` unencoded into a body sent as HTML, and `Identity/Services/UserPasswordService.cs` sends a plain-text reset sentence as an HTML body with an unescaped `&`. This review's independent contribution is the ASVS mapping: **V1.2.1** — "Verify that output encoding for an HTTP response, HTML document... is relevant for the context required... to avoid changing the message or document structure" — is the applicable Level 1 row (ASVS 5.0's dedicated HTML-encoding items, V1.1.x, are Level 2, so V1.2.1 is the correct L1 citation here, not V1.1.2). Severity and fix size unchanged from upstream-delta's assessment (low; the registrant also controls the recipient address; size M, spanning `BuildingBlocks/Mailing` — needs `buildingblocks-protection.md` approval).

### 6. Deactivating or deleting a user never revokes its `UserSession` rows

`src/Modules/Identity/Modules.Identity/Services/UserStatusService.cs`: `ToggleStatusAsync` (which `DeleteAsync` also delegates to) runs `BuildToggleContextAsync` → `ValidateTogglePermissionsAsync` → `ApplyStatusChange` → `SaveAndAuditAsync`. `ApplyStatusChange` only flips the user's own `Activate`/`Deactivate` domain state; `SaveAndAuditAsync` only calls `userManager.UpdateAsync` and writes an audit entry. Nowhere in this path is `ISessionService` (or `UserManager.UpdateSecurityStampAsync`) called, so a deactivated or deleted user's existing `UserSession` rows are left exactly as they were — not marked revoked, and the `SecurityStamp` that `SessionService.RotateRefreshTokenAsync` checks is untouched.

**V7.4.2**: "Verify that the application terminates all active sessions when a user account is disabled or deleted." This is distinct from the out-of-scope #118 (which is about checking `sid` against the session store on *every* request): #118 concerns how quickly an already-terminated session stops working, while this finding is that termination is never triggered on the session rows in the first place. The practical exposure is bounded today — `IdentityService.BuildClaimsForRefreshAsync` (`Services/IdentityService.cs:104`) calls `ValidateUserStatus(user)` before honouring a refresh, so a disabled user's refresh attempts fail on the very next refresh, and the live access token is naturally capped by `JwtOptions.AccessTokenMinutes` (default 30). But the `UserSession` rows themselves stay marked live, which would undercut #118 if that future check ever also surfaces "active sessions" to an end user or admin (a deactivated user would still show sessions as active), and it means the session store's own bookkeeping doesn't reflect reality.

**Severity: medium** — bounded by the access-token TTL today, but the rows are never corrected and nothing enqueues a cleanup. **Fix size: S** — call a bulk session-revoke (or bump the user's `SecurityStamp`, which the rotation path already checks) from `ApplyStatusChange`/`SaveAndAuditAsync` when `ActivateUser` is `false`.

## Noted from the upstream triage (not re-derived here)

These already appear in `upstream-delta.md`, overlap this review's areas, and are listed here only so this document is a complete picture of the security surface — not as new findings:

- **Audit-diff PII leak** (`921be0e`, P1 port in upstream-delta): `PasswordHash`/`SecurityStamp` and token hashes are copied verbatim into `AuditRecords` (`Modules.Auditing/Persistence/EntityDiffBuilder.cs:120-124`). No ASVS 5.0 Level 1 requirement covers audit-log content directly (that's chapter V16, Level 2+), so it isn't counted in this review's findings table, but it is a real exposure through `AuditingPermissions.AuditTrails.View`.
- **CORS PATCH gap** (`dcb3525`): two PATCH endpoints exist (`ToggleUserStatusEndpoint.cs`, `ChangeFileVisibilityEndpoint.cs`) but `appsettings.json`/`appsettings.Production.json` restrict `AllowedMethods` to `GET, POST, PUT, DELETE`. Only bites a client pointed cross-origin at the API (the shipped deploys are same-origin via nginx). Relevant to **V3.4.2** only insofar as the policy is otherwise a sound fixed allowlist (see Checked and sound, below); this is a completeness gap in that allowlist, not a CORS validation defect.
- **Idempotency client-abort token** (`bf86648` part c): a disconnect after a handler commits but before it returns can leave nothing cached for the idempotency key, so a client retry re-runs the side effect. Distinct from the per-process lock exclusion named in this ticket.

## Checked and sound

- **V7.2.1–V7.2.4 (session/refresh tokens):** `RefreshTokenValue.cs` issues 256 bits of CSPRNG entropy (`SecretByteLength = 32` bytes) via `RandomNumberGenerator.GetBytes`, stores only a SHA-256 hash (`Hash()`), and a new session/refresh token is minted on every authentication (`SessionService.CreateSessionAsync`). `SessionService.RotateRefreshTokenAsync` implements single-use rotation with reuse detection (RFC 9700 §4.14.2, comment at the "Reuse detection" branch) that revokes the whole session chain on replay.
- **V9.1.1, V9.1.3, V9.2.1 (self-contained tokens):** `ConfigureJwtBearerOptions.cs:54-64` validates the signature against a single pre-configured symmetric key (`ValidateIssuerSigningKey = true`), and issuer/audience/lifetime are all validated (`ValidateIssuer`, `ValidateAudience`, `ValidateLifetime` all `true`, `ClockSkew = 2 minutes`). No `jku`/`x5u`/`jwk` header is ever consulted. `JwtOptions.cs` fails startup if the signing key is missing, under 32 characters, or the shipped placeholder value. **Caveat on V9.1.2** (allow-listed algorithms): `TokenValidationParameters.ValidAlgorithms` is never set, so the effective set is whatever `Microsoft.IdentityModel` accepts for a `SymmetricSecurityKey` (HS256/384/512) rather than an application-declared allowlist; `ValidateIssuerSigningKey = true` does rule out `alg: none`. Only one key is configured, so symmetric/asymmetric key-confusion (the scenario V9.1.2 mainly targets) doesn't apply here — this is a minor completeness gap, not counted as a separate finding (XS if it were: `ValidAlgorithms = [SecurityAlgorithms.HmacSha256]`).
- **V7.4.1 (session termination):** `EndSessionCommandHandler.cs` revokes the session row server-side via `ISessionService`, and always returns 204 regardless of whether a live session was found — the class doc explains this is deliberate, so logout can't be used to probe stolen tokens.
- **V6.2.2/V6.2.3 (password change):** `ChangePasswordCommandHandler.cs` requires an authenticated user and both `Password` (current) and `NewPassword`; `UserPasswordService.cs:93` (`ChangePasswordAsync`) delegates to `UserManager.ChangePasswordAsync`, which itself verifies the current password before accepting the new one.
- **V8.2.1/V8.2.2/V8.3.1 (tenant exchange, operator surface):** `ExchangeOperatorTokenCommandHandler.cs` re-checks that the caller's own tenant is root server-side independent of the endpoint's permission policy (comment: "independently of the permission gate on the endpoint"), refuses to exchange an already-exchanged (nested) token, and refuses a deactivated target tenant or target user. `RevokeImpersonationGrantCommandHandler.cs:42-47` enforces tenant-scoped visibility on a lookup that isn't itself tenant-filtered, and returns 404 (not 403) for an out-of-scope grant so existence isn't disclosed — a deliberate BOLA guard.
- **V3.4.1 (HSTS):** `src/BuildingBlocks/Web/Security/SecurityHeadersMiddleware.cs:53` sets `Strict-Transport-Security: max-age=31536000; includeSubDomains` on every response (exceeds the L1 minimum, which doesn't require `includeSubDomains` until L2).
- **V3.4.2 (CORS origin policy):** `src/BuildingBlocks/Web/Cors/Extensions.cs` builds a fixed allowlist (`WithOrigins(settings.AllowedOrigins)`) with no `AllowCredentials` in either branch; the comment notes authentication is bearer-only so no ambient credential ever needs to cross an origin. `AllowAll` (origin-reflecting) is restricted to Development by `CorsOptionsValidator`.
- **V5.2.1 (upload size):** `RequestUploadUrlCommandHandler.cs:53-59` rejects a declared size over `category.MaxBytes` at request time, and `FinalizeUploadCommandHandler.cs:50-61` independently re-checks the actual stored size against the declared size (with a small slack for multipart) and deletes the object if it's over.
- **V5.3.2 (file path handling):** `StorageKeyBuilder.cs` strips every character outside `[a-zA-Z0-9_.-]` from both the owner type and the filename before it becomes part of an object key, and `IStorageService.ComposeKey` is the only place a tenant prefix is ever added — an architecture test (per the interface's doc comment) enforces that no caller builds a key another way.
- **V6.3.2 (no default accounts):** no `CreateUser`-with-generated-password flow exists outside self-registration; every tenant's admin password is operator-supplied at tenant creation (`Modules.Multitenancy.Contracts/v1/CreateTenant/CreateTenantCommand.cs:16`, `AdminPassword`, marked `[NotFingerprinted]`; validated in `CreateTenantCommandValidator.cs:44-49` against the same 8-char floor as the Identity policy) and buffered rather than defaulted (`CreateTenantCommandHandler.cs:34-36`). `Host/Boilerplate.DbMigrator/DemoSeed/DemoSeedGuard.cs` separately enforces the tenant's own live password policy on any seeded demo account.
- **Unauthenticated-failure detail not leaked in Production:** `ConfigureJwtBearerOptions.cs`'s `OnChallenge` only adds the JWT rejection reason to the 401 `ProblemDetails` body when `isDev` is true (`_environment.IsDevelopment()`); Production gets an opaque body. (The `audit-dlq/*.jsonl` local dead-letter files that surfaced this reason string on disk during review are themselves gitignored — `.gitignore:520`, `**/audit-dlq/` — and not tracked in the repo.)
- **Forwarded headers / reverse proxy trust:** already reviewed and confirmed sound in `upstream-delta.md` (`ProxyOptions.cs`, `ConfigureForwardedHeaders.cs` — fails closed, `X-Forwarded-Host` excluded on purpose per #13); not re-verified independently here beyond confirming the file still matches that description.
