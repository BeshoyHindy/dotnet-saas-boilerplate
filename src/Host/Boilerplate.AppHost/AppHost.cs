using Aspire.Hosting.ApplicationModel;

var builder = DistributedApplication.CreateBuilder(args);

// Per-app prefix from the AppHost assembly name (Boilerplate.AppHost -> boilerplate); namespaces Docker volumes + resource names so multiple Boilerplate apps don't clash.
#pragma warning disable CA1308 // resource + volume names are conventionally lowercase
var appPrefix = builder.Environment.ApplicationName
    .Replace(".AppHost", string.Empty, StringComparison.OrdinalIgnoreCase)
    .Replace('.', '-')
    .ToLowerInvariant();
#pragma warning restore CA1308

// Postgres; persistent so volumes and saved state survive restarts. No database-browser
// sidecar: the stack is the application and its dependencies, and a pgAdmin (or
// RedisInsight) container is a tool a developer installs, not something a template
// should start — and pay for in RAM — on every run.
//
// The volume name is versioned. Postgres only reads POSTGRES_PASSWORD when it initialises an
// empty data directory, so a volume outlives the password it was created with: once the
// persisted `postgres-password` parameter no longer matches (user-secrets reset, another
// machine's volume, a regenerated Initial migration), every connection fails with "password
// authentication failed", the migrator never starts, and the API and clients hang behind it.
// Bump the suffix to start clean without deleting anyone's data; the old volume is left alone.
var postgresServer = builder.AddPostgres("postgres")
    .WithDataVolume($"{appPrefix}-postgres-data-v2")
    .WithLifetime(ContainerLifetime.Persistent);

var postgres = postgresServer.AddDatabase("boilerplate-db");

// Warm pooled-connection floor for the long-running API — Npgsql's default Minimum Pool Size of 0 lets the pool drain to cold, so /health/ready's ~10 concurrent DbContext checks cold-open a cohort at once and intermittently stall the probe; a floor keeps connections warm for reuse.
var apiPgConnection = ReferenceExpression.Create(
    $"{postgres.Resource.ConnectionStringExpression};Minimum Pool Size=5");

// Valkey (BSD-3 Redis fork) as a plain container: Aspire 13.4.0 AddRedis() forces TLS-by-default in run mode and never materializes the container, so we drop to plain RESP over TCP. Name stays "redis" so config keys don't churn.
var redis = builder.AddContainer("redis", "valkey/valkey", "9.1.2")
    .WithEndpoint(targetPort: 6379, scheme: "tcp", name: "tcp")
    .WithVolume($"{appPrefix}-redis-data", "/data")
    .WithLifetime(ContainerLifetime.Persistent);

var redisEndpoint = redis.GetEndpoint("tcp");
var redisConnectionString = ReferenceExpression.Create(
    $"{redisEndpoint.Property(EndpointProperty.HostAndPort)}");

// Object storage (RustFS, S3-compatible). CORS via RUSTFS_CORS_ALLOWED_ORIGINS so browser
// presigned PUTs from a client's dev origin reach it without proxying through the API.
const string StorageBucket = "boilerplate-uploads";

// The two clients (ADR-0008): the dashboard is the app a tenant's users work in, the
// console is the operator tool. Dev ports stay fixed — a client's origin is part of its
// contract here (the Vite proxy, the SameSite=Strict refresh cookie, and the CORS entry
// below all name it) — unlike the container ports above, which Aspire allocates.
//
// Declared unconditionally, outside any template conditional: the template's markers are
// plain C# comments, so both arms of one reach the real compiler and a constant declared
// in each would be a duplicate. With `--frontend false` the pair simply goes unused
// beyond the object store's CORS allow-list, which is harmless.
const string DashboardOrigin = "http://localhost:5173";
const string ConsoleOrigin = "http://localhost:5174";
// RustFS takes a comma-separated allow-list; both clients upload straight to it.
const string ClientOrigins = $"{DashboardOrigin},{ConsoleOrigin}";

// Secrets are Aspire parameters, never literals in this file: the secret key is generated on first
// run and persisted to this project's user-secrets, so it survives restarts without being committed.
// Read the current values from the Aspire dashboard (Resources → Parameters) if you need them.
var storageAccessKey = builder.AddParameter("storage-access-key", "boilerplate");
var storageSecretKey = builder.AddParameter(
    "storage-secret-key",
    new GenerateParameterDefault { MinLength = 24, Lower = true, Upper = true, Numeric = true, Special = false },
    secret: true,
    persist: true);

// RustFS replaced MinIO, whose repository is archived and whose images no registry serves
// anonymously any more. Pinned by tag AND digest, and every object-store site in the repository
// (both compose stacks, both integration factories) carries the identical pin, which
// Architecture.Tests enforces, so a bump is one value, not five drifting ones.
const string StorageImage = "rustfs/rustfs";
const string StorageImageTag = "1.0.0";
const string StorageImageDigest = "8cc9801755448b71a786705ce76692c77e14936cccd87cf2fc31842e58f4d1ff";

// The bucket bootstrap runs the AWS CLI, pinned the same way and to the same pin as the compose
// stacks' storage-init.
const string AwsCliImage = "amazon/aws-cli";
const string AwsCliImageTag = "2.37.4";
const string AwsCliImageDigest = "fdd8d1fcbea9c371678dee5a40df8b178c7a781b4586605756ee28114c97ead6";

// NO FIXED HOST PORTS. The container listens on its own 9000/9001, but the host side is left to
// Aspire to allocate. Pinning them made a second AppHost instance (another checkout or worktree)
// unrunnable in the worst possible way: the persistent store container of the first instance still
// holds 9000/9001, the new container fails to bind, Docker leaves it attached to no network, and
// `storage-init` then waits forever — which, through `WaitForCompletion`, hangs the API and every
// client behind it with no error anywhere. Everything that needs the real address takes it from
// the endpoint below, so nothing here knows a port number.
//
// The image runs as uid 10001 and owns /data as that user, so a fresh volume is writable with no
// volume-owner step. The volume is `-storage-data`, not the old `-minio-data`: a volume the MinIO
// images wrote belongs to another uid (delete the old one with `docker volume rm` when you like).
var storage = builder.AddContainer("storage", StorageImage, StorageImageTag)
    .WithImageSHA256(StorageImageDigest)
    .WithHttpEndpoint(targetPort: 9000, name: "api")
    // The RustFS console; open it at <endpoint>/rustfs/console/ (the endpoint root answers 403).
    .WithHttpEndpoint(targetPort: 9001, name: "console")
    .WithEnvironment("RUSTFS_ACCESS_KEY", storageAccessKey)
    .WithEnvironment("RUSTFS_SECRET_KEY", storageSecretKey)
    .WithEnvironment("RUSTFS_CONSOLE_ENABLE", "true")
    .WithEnvironment("RUSTFS_CORS_ALLOWED_ORIGINS", ClientOrigins)
    .WithVolume($"{appPrefix}-storage-data", "/data")
    // The S3 API's readiness, not /health: liveness answers a moment before the API does.
    .WithHttpHealthCheck("/health/ready", endpointName: "api")
    .WithLifetime(ContainerLifetime.Persistent);

var storageApiEndpoint = storage.GetEndpoint("api");

// Init container: create the bucket, then allow anonymous GET under `uploads/` — and nowhere else,
// the same grant as both compose stacks. Avatars and tenant branding are unsigned URLs there;
// Files-module objects under `tenants/` stay private. Idempotent: head-bucket skips a create that
// already happened, and put-bucket-policy rewrites the same policy. Script normalized to LF so bash
// doesn't choke on Windows CRLF. The endpoint arrives as $AWS_ENDPOINT_URL: Aspire resolves an
// endpoint reference injected into a *container* to the container-network form
// (http://storage:9000), so this keeps working whatever the host port is.
var storageInitScript = ($$"""
set -euo pipefail
aws s3api head-bucket --bucket "$BUCKET" 2>/dev/null || aws s3api create-bucket --bucket "$BUCKET"
aws s3api put-bucket-policy --bucket "$BUCKET" --policy "{\"Version\":\"2012-10-17\",\"Statement\":[{\"Effect\":\"Allow\",\"Principal\":{\"AWS\":[\"*\"]},\"Action\":[\"s3:GetObject\"],\"Resource\":[\"arn:aws:s3:::$BUCKET/uploads/*\"]}]}"
""").ReplaceLineEndings("\n");

var storageInit = builder.AddContainer("storage-init", AwsCliImage, AwsCliImageTag)
    .WithImageSHA256(AwsCliImageDigest)
    .WithEntrypoint("/bin/bash")
    .WithArgs("-c", storageInitScript)
    .WithEnvironment("AWS_ENDPOINT_URL", storageApiEndpoint)
    .WithEnvironment("AWS_ACCESS_KEY_ID", storageAccessKey)
    .WithEnvironment("AWS_SECRET_ACCESS_KEY", storageSecretKey)
    .WithEnvironment("AWS_DEFAULT_REGION", "us-east-1")
    .WithEnvironment("BUCKET", StorageBucket)
    .WaitFor(storage);

// Mail catcher: traps every message the API sends instead of delivering it. SMTP and the inbox UI
// listen on the container's 1025/8025; the host ports are Aspire's to allocate, for the same
// reason the object store's are (a second instance must not be blocked by the first). Open the inbox from the
// Aspire dashboard's "mailpit" resource link; the API is wired to the SMTP endpoint by reference.
var mailpit = builder.AddContainer("mailpit", "axllent/mailpit", "v1.31")
    .WithEndpoint(targetPort: 1025, scheme: "tcp", name: "smtp")
    .WithHttpEndpoint(targetPort: 8025, name: "http")
    .WithEnvironment("MP_SMTP_AUTH_ACCEPT_ANY", "true")
    .WithEnvironment("MP_SMTP_AUTH_ALLOW_INSECURE", "true")
    .WithExternalHttpEndpoints();

var mailpitSmtp = mailpit.GetEndpoint("smtp");

// Password the seeded root admin user signs in with. Generated + persisted like the storage secret key above.
// IdentityModule's policy is 10+ characters with an upper, a lower and a digit, hence the constraints.
var seedAdminPassword = builder.AddParameter(
    "seed-admin-password",
    new GenerateParameterDefault
    {
        MinLength = 20,
        Lower = true,
        Upper = true,
        Numeric = true,
        Special = false,
        MinLower = 2,
        MinUpper = 2,
        MinNumeric = 2,
    },
    secret: true,
    persist: true);

// Password every demo account signs in with (--demo below). Generated + persisted exactly like the
// admin one above, and read the same way: Aspire dashboard → Resources → Parameters. Demo accounts
// are a development affordance — the migrator refuses --demo outright when the environment is
// Production, so this parameter never travels to a real deployment.
var seedDemoPassword = builder.AddParameter(
    "seed-demo-password",
    new GenerateParameterDefault
    {
        MinLength = 20,
        Lower = true,
        Upper = true,
        Numeric = true,
        Special = false,
        MinLower = 2,
        MinUpper = 2,
        MinNumeric = 2,
    },
    secret: true,
    persist: true);

// DB migrator: applies pending migrations + seeds the root tenant and its admin user, then exits. The
// API waits for its completion so it never starts against an unmigrated DB. `--demo` adds the demo
// tenants (acme, globex) and the people inside them, so a fresh stack has something to sign in as.
var migrator = builder.AddProject<Projects.Boilerplate_DbMigrator>($"{appPrefix}-db-migrator")
    .WithReference(postgres)
    .WaitFor(postgres)
    // The migrator is a plain console Host, which reads DOTNET_ENVIRONMENT — Aspire only sets
    // ASPNETCORE_ENVIRONMENT, so without this it defaults to Production and the Production-only
    // option validators reject a dev stack (e.g. the placeholder signing key it injects for itself).
    .WithEnvironment("DOTNET_ENVIRONMENT", builder.Environment.EnvironmentName)
    .WithEnvironment("DatabaseOptions__Provider", "POSTGRESQL")
    .WithEnvironment("DatabaseOptions__ConnectionString", postgres.Resource.ConnectionStringExpression)
    .WithEnvironment("DatabaseOptions__MigrationsAssembly", "Boilerplate.Migrations.PostgreSQL")
    .WithEnvironment("Seed__DefaultAdminPassword", seedAdminPassword)
    .WithEnvironment("Seed__DemoPassword", seedDemoPassword)
    .WithArgs("apply", "--seed", "--demo");

// API Service. Startup order is migrator → API → clients: the migrator must have exited 0 before the
// API boots, and the React apps below wait on the API.
var api = builder.AddProject<Projects.Boilerplate_Api>($"{appPrefix}-api")
    .WithReference(postgres)
    .WaitFor(postgres)
    .WaitFor(redis)
    .WaitFor(mailpit)
    .WaitForCompletion(storageInit)
    .WaitForCompletion(migrator)
    .WithExternalHttpEndpoints()
    .WithEnvironment("DatabaseOptions__Provider", "POSTGRESQL")
    .WithEnvironment("DatabaseOptions__ConnectionString", apiPgConnection)
    .WithEnvironment("DatabaseOptions__MigrationsAssembly", "Boilerplate.Migrations.PostgreSQL")
    .WithEnvironment("CachingOptions__Redis", redisConnectionString)
    .WithEnvironment("CachingOptions__EnableSsl", "false")
    // SMTP points at the Mailpit container above — nothing leaves the machine and no credentials are needed.
    .WithEnvironment("MailOptions__UseSendGrid", "false")
    .WithEnvironment("MailOptions__From", "no-reply@localhost")
    .WithEnvironment("MailOptions__DisplayName", "Boilerplate")
    .WithEnvironment("MailOptions__Smtp__Host", ReferenceExpression.Create($"{mailpitSmtp.Property(EndpointProperty.Host)}"))
    .WithEnvironment("MailOptions__Smtp__Port", ReferenceExpression.Create($"{mailpitSmtp.Property(EndpointProperty.Port)}"))
    // Mailpit speaks plain SMTP and never advertises STARTTLS, which the SmtpOptions default requires.
    .WithEnvironment("MailOptions__Smtp__SecureSocket", "None")
    .WithEnvironment("Storage__Provider", "s3")
    .WithEnvironment("Storage__S3__Bucket", StorageBucket)
    .WithEnvironment("Storage__S3__Region", "us-east-1")
    .WithEnvironment("Storage__S3__ServiceUrl", storageApiEndpoint)
    .WithEnvironment("Storage__S3__AccessKey", storageAccessKey)
    .WithEnvironment("Storage__S3__SecretKey", storageSecretKey)
    .WithEnvironment("Storage__S3__ForcePathStyle", "true")
    .WithEnvironment("Storage__S3__PublicBaseUrl", ReferenceExpression.Create($"{storageApiEndpoint}/{StorageBucket}"))
    ;

// The origin the API writes into password-reset and email-confirmation links. It must be
// set: the Identity module refuses to mail a link without one rather than guess it from the
// request. Two separately conditioned statements, in this order, because the template's
// markers are plain C# comments: both run in the template's own tree, and the later one
// wins there.
//#if (!frontend)
// No client ships with this product, so there is no client page to link to yet: the links
// carry the API's own origin until the front end you build takes over.
api.WithEnvironment("OriginOptions__OriginUrl", api.GetEndpoint("https"));
//#endif
//#if (frontend)
// The links point at a client page, so the origin has to be a client's (issue #46), not
// the API's own — and it is the DASHBOARD's: those mails go to a tenant's users, who live
// there. An operator never receives one at the console (ADR-0008).
api.WithEnvironment("OriginOptions__OriginUrl", DashboardOrigin);
//#endif

//#if (frontend)
// The two React clients (ADR-0008), each its own independent pnpm project and its own
// image. VITE_API_BASE_URL is the dev server's PROXY target, not the browser's API base —
// the browser only ever talks to its own client's origin, which is what lets the
// SameSite=Strict refresh cookie work with CORS credentials off. The HTTPS endpoint is the
// target because UseHttpsRedirection's 307 would otherwise strip the Authorization header.
//
// `isProxied: false` on a fixed port for both: the origin the browser sees has to be the
// one the cookie, the CORS allow-list and the config above were written for.

// The dashboard — what a tenant's own users sign in to.
builder.AddJavaScriptApp($"{appPrefix}-dashboard", "../../../clients/dashboard", "dev")
    .WithPnpm()
    .WithReference(api)
    .WaitFor(api)
    .WithHttpEndpoint(port: 5173, targetPort: 5173, isProxied: false)
    .WithExternalHttpEndpoints()
    .WithEnvironment("VITE_API_BASE_URL", api.GetEndpoint("https"))
    // The demo-account picker is on in the local stack and nowhere else by default: this
    // is the one environment whose accounts are seeded, disposable and nobody's data.
    .WithEnvironment("VITE_DEMO_MODE", "true")
    // The same generated parameter the migrator seeds the demo accounts with, so the picker
    // signs in with one click. Dev only: a Vite env value is public to the browser by design.
    .WithEnvironment("VITE_DEMO_PASSWORD", seedDemoPassword);

// The console — the operator tool. Root operators only; a tenant user who signs in here
// is told so (see clients/console/src/auth/operator-gate.tsx).
builder.AddJavaScriptApp($"{appPrefix}-console", "../../../clients/console", "dev")
    .WithPnpm()
    .WithReference(api)
    .WaitFor(api)
    .WithHttpEndpoint(port: 5174, targetPort: 5174, isProxied: false)
    .WithExternalHttpEndpoints()
    .WithEnvironment("VITE_API_BASE_URL", api.GetEndpoint("https"))
    // Prefills the seeded operator's email only. Its password is `seed-admin-password`
    // above, not the demo tenants' shared one, so the console never signs in for you.
    .WithEnvironment("VITE_DEMO_MODE", "true");
//#endif

await builder.Build().RunAsync();
