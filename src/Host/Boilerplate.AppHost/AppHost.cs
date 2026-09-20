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
var redis = builder.AddContainer("redis", "valkey/valkey", "9.1.0")
    .WithEndpoint(targetPort: 6379, scheme: "tcp", name: "tcp")
    .WithVolume($"{appPrefix}-redis-data", "/data")
    .WithLifetime(ContainerLifetime.Persistent);

var redisEndpoint = redis.GetEndpoint("tcp");
var redisConnectionString = ReferenceExpression.Create(
    $"{redisEndpoint.Property(EndpointProperty.HostAndPort)}");

// Object storage (MinIO, S3-compatible). CORS via MINIO_API_CORS_ALLOW_ORIGIN so browser
// presigned PUTs from a client's dev origin reach it without proxying through the API.
const string MinioBucket = "boilerplate-uploads";

// The two clients (ADR-0008): the dashboard is the app a tenant's users work in, the
// console is the operator tool. Dev ports stay fixed — a client's origin is part of its
// contract here (the Vite proxy, the SameSite=Strict refresh cookie, and the CORS entry
// below all name it) — unlike the container ports above, which Aspire allocates.
//
// Declared unconditionally, outside any template conditional: the template's markers are
// plain C# comments, so both arms of one reach the real compiler and a constant declared
// in each would be a duplicate. With `--frontend false` the pair simply goes unused
// beyond MinIO's CORS header, which is harmless.
const string DashboardOrigin = "http://localhost:5173";
const string ConsoleOrigin = "http://localhost:5174";
// MinIO takes a comma-separated allow-list; both clients upload straight to it.
const string ClientOrigins = $"{DashboardOrigin},{ConsoleOrigin}";

// Secrets are Aspire parameters, never literals in this file: the password is generated on first
// run and persisted to this project's user-secrets, so it survives restarts without being committed.
// Read the current values from the Aspire dashboard (Resources → Parameters) if you need them.
var minioUser = builder.AddParameter("minio-user", "boilerplate");
var minioPassword = builder.AddParameter(
    "minio-password",
    new GenerateParameterDefault { MinLength = 24, Lower = true, Upper = true, Numeric = true, Special = false },
    secret: true,
    persist: true);

// quay.io, not the implicit docker.io: MinIO no longer publishes to Docker Hub.
//
// NO FIXED HOST PORTS. The container listens on its own 9000/9001, but the host side is left to
// Aspire to allocate. Pinning them made a second AppHost instance (another checkout or worktree)
// unrunnable in the worst possible way: the persistent MinIO container of the first instance still
// holds 9000/9001, the new container fails to bind, Docker leaves it attached to no network, and
// `minio-init` then loops on "waiting for minio..." forever — which, through `WaitForCompletion`,
// hangs the API and every client behind it with no error anywhere. Everything that needs the real
// address takes it from the endpoint below, so nothing here knows a port number.
var minio = builder.AddContainer("minio", "quay.io/minio/minio", "RELEASE.2025-09-07T16-13-09Z")
    .WithArgs("server", "/data", "--console-address", ":9001")
    .WithHttpEndpoint(targetPort: 9000, name: "api")
    .WithHttpEndpoint(targetPort: 9001, name: "console")
    .WithEnvironment("MINIO_ROOT_USER", minioUser)
    .WithEnvironment("MINIO_ROOT_PASSWORD", minioPassword)
    .WithEnvironment("MINIO_API_CORS_ALLOW_ORIGIN", ClientOrigins)
    .WithVolume($"{appPrefix}-minio-data", "/data")
    .WithLifetime(ContainerLifetime.Persistent);

var minioApiEndpoint = minio.GetEndpoint("api");

// Init container: bucket bootstrap (create + public-read). Script normalized to LF so /bin/sh in minio/mc doesn't choke on Windows CRLF.
// The endpoint arrives as $MC_URL: Aspire resolves an endpoint reference injected into a *container*
// to the container-network form (http://minio:9000), so this keeps working whatever the host port is.
var minioInitScript = ($$"""
until mc alias set local "$MC_URL" "$MC_USER" "$MC_PASS"; do
  echo "waiting for minio...";
  sleep 2;
done;
mc mb --ignore-existing local/{{MinioBucket}};
mc anonymous set download local/{{MinioBucket}};
""").ReplaceLineEndings("\n");

var minioInit = builder.AddContainer("minio-init", "quay.io/minio/mc", "RELEASE.2025-08-13T08-35-41Z")
    .WithEntrypoint("/bin/sh")
    .WithArgs("-c", minioInitScript)
    .WithEnvironment("MC_URL", minioApiEndpoint)
    .WithEnvironment("MC_USER", minioUser)
    .WithEnvironment("MC_PASS", minioPassword)
    .WaitFor(minio);

// Mail catcher: traps every message the API sends instead of delivering it. SMTP and the inbox UI
// listen on the container's 1025/8025; the host ports are Aspire's to allocate, for the same
// reason MinIO's are (a second instance must not be blocked by the first). Open the inbox from the
// Aspire dashboard's "mailpit" resource link; the API is wired to the SMTP endpoint by reference.
var mailpit = builder.AddContainer("mailpit", "axllent/mailpit", "v1.31")
    .WithEndpoint(targetPort: 1025, scheme: "tcp", name: "smtp")
    .WithHttpEndpoint(targetPort: 8025, name: "http")
    .WithEnvironment("MP_SMTP_AUTH_ACCEPT_ANY", "true")
    .WithEnvironment("MP_SMTP_AUTH_ALLOW_INSECURE", "true")
    .WithExternalHttpEndpoints();

var mailpitSmtp = mailpit.GetEndpoint("smtp");

// Password the seeded root admin user signs in with. Generated + persisted like the MinIO one above.
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
    .WaitForCompletion(minioInit)
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
    .WithEnvironment("Storage__S3__Bucket", MinioBucket)
    .WithEnvironment("Storage__S3__Region", "us-east-1")
    .WithEnvironment("Storage__S3__ServiceUrl", minioApiEndpoint)
    .WithEnvironment("Storage__S3__AccessKey", minioUser)
    .WithEnvironment("Storage__S3__SecretKey", minioPassword)
    .WithEnvironment("Storage__S3__ForcePathStyle", "true")
    .WithEnvironment("Storage__S3__PublicBaseUrl", ReferenceExpression.Create($"{minioApiEndpoint}/{MinioBucket}"))
//#if (frontend)
    // Password-reset and email-confirmation links point at a client page, so the origin
    // the API mails has to be a client's (issue #46), not the API's own — and it is the
    // DASHBOARD's: those mails go to a tenant's users, who live there. An operator never
    // receives one at the console (ADR-0008).
    .WithEnvironment("OriginOptions__OriginUrl", DashboardOrigin)
//#endif
    ;

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
//#else
// React apps excluded: discard the unused api handle to keep the no-frontend scaffold warning-clean (S1481 under TreatWarningsAsErrors).
_ = api;
//#endif

await builder.Build().RunAsync();
