using Aspire.Hosting.ApplicationModel;

var builder = DistributedApplication.CreateBuilder(args);

// Per-app prefix from the AppHost assembly name (Boilerplate.AppHost -> boilerplate); namespaces Docker volumes + resource names so multiple Boilerplate apps don't clash.
#pragma warning disable CA1308 // resource + volume names are conventionally lowercase
var appPrefix = builder.Environment.ApplicationName
    .Replace(".AppHost", string.Empty, StringComparison.OrdinalIgnoreCase)
    .Replace('.', '-')
    .ToLowerInvariant();
#pragma warning restore CA1308

// Postgres + pgAdmin sidecar (auto-discovers registered databases); persistent so volumes and saved state survive restarts.
var postgresServer = builder.AddPostgres("postgres")
    .WithDataVolume($"{appPrefix}-postgres-data")
    .WithLifetime(ContainerLifetime.Persistent)
    .WithPgAdmin(pa => pa
        .WithHostPort(5050)
        .WithLifetime(ContainerLifetime.Persistent));

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

// RedisInsight cache browser (dev-only) sidecar; RI_REDIS_* pre-registers the Valkey connection via the container-network alias "redis".
builder.AddContainer("redis-insight", "redis/redisinsight", "3.8.0")
    .WithHttpEndpoint(port: 5540, targetPort: 5540, name: "http")
    .WithEnvironment("RI_REDIS_HOST0", "redis")
    .WithEnvironment("RI_REDIS_PORT0", "6379")
    .WithEnvironment("RI_REDIS_ALIAS0", "boilerplate-cache")
    .WithEnvironment("RI_ACCEPT_TERMS_AND_CONDITIONS", "true")
    .WithLifetime(ContainerLifetime.Persistent)
    .WaitFor(redis);

// Object storage (MinIO, S3-compatible). CORS via MINIO_API_CORS_ALLOW_ORIGIN so browser presigned PUTs from the admin (:5173)/dashboard (:5174) dev origins work without proxying through the API.
const string MinioBucket = "boilerplate-uploads";
const string AdminOrigin = "http://localhost:5173";
const string DashboardOrigin = "http://localhost:5174";

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
var minio = builder.AddContainer("minio", "quay.io/minio/minio", "RELEASE.2025-09-07T16-13-09Z")
    .WithArgs("server", "/data", "--console-address", ":9001")
    .WithHttpEndpoint(port: 9000, targetPort: 9000, name: "api")
    .WithHttpEndpoint(port: 9001, targetPort: 9001, name: "console")
    .WithEnvironment("MINIO_ROOT_USER", minioUser)
    .WithEnvironment("MINIO_ROOT_PASSWORD", minioPassword)
    .WithEnvironment("MINIO_API_CORS_ALLOW_ORIGIN", $"{AdminOrigin},{DashboardOrigin}")
    .WithVolume($"{appPrefix}-minio-data", "/data")
    .WithLifetime(ContainerLifetime.Persistent);

// Init container: bucket bootstrap (create + public-read). Script normalized to LF so /bin/sh in minio/mc doesn't choke on Windows CRLF.
var minioInitScript = ($$"""
until mc alias set local http://minio:9000 "$MC_USER" "$MC_PASS"; do
  echo "waiting for minio...";
  sleep 2;
done;
mc mb --ignore-existing local/{{MinioBucket}};
mc anonymous set download local/{{MinioBucket}};
""").ReplaceLineEndings("\n");

var minioInit = builder.AddContainer("minio-init", "quay.io/minio/mc", "RELEASE.2025-08-13T08-35-41Z")
    .WithEntrypoint("/bin/sh")
    .WithArgs("-c", minioInitScript)
    .WithEnvironment("MC_USER", minioUser)
    .WithEnvironment("MC_PASS", minioPassword)
    .WaitFor(minio);

var minioApiEndpoint = minio.GetEndpoint("api");

// Mail catcher: traps every message the API sends instead of delivering it. SMTP on :1025, inbox UI on :8025.
var mailpit = builder.AddContainer("mailpit", "axllent/mailpit", "v1.31")
    .WithEndpoint(port: 1025, targetPort: 1025, scheme: "tcp", name: "smtp")
    .WithHttpEndpoint(port: 8025, targetPort: 8025, name: "http")
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

// DB migrator: applies pending migrations + seeds the root tenant and its admin user, then exits. The
// API waits for its completion so it never starts against an unmigrated DB. Structural bootstrap only —
// no demo/sample data is seeded.
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
    .WithArgs("apply", "--seed");

// API Service. Startup order is migrator → API → console: the migrator must have exited 0 before the
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
    .WithEnvironment("Storage__S3__PublicBaseUrl", ReferenceExpression.Create($"{minioApiEndpoint}/{MinioBucket}"));

//#if (frontend)
// Admin console (React + Vite). Target the API's HTTPS endpoint directly — UseHttpsRedirection's 307 to https is cross-origin and strips the Authorization header.
builder.AddJavaScriptApp($"{appPrefix}-admin", "../../../clients/admin", "dev")
    .WithNpm()
    .WithReference(api)
    .WaitFor(api)
    .WithHttpEndpoint(port: 5173, targetPort: 5173, isProxied: false)
    .WithExternalHttpEndpoints()
    .WithEnvironment("VITE_API_BASE_URL", api.GetEndpoint("https"));

// Tenant-facing dashboard (React + Vite)
builder.AddJavaScriptApp($"{appPrefix}-dashboard", "../../../clients/dashboard", "dev")
    .WithNpm()
    .WithReference(api)
    .WaitFor(api)
    .WithHttpEndpoint(port: 5174, targetPort: 5174, isProxied: false)
    .WithExternalHttpEndpoints()
    .WithEnvironment("VITE_API_BASE_URL", api.GetEndpoint("https"));
//#else
// React apps excluded: discard the unused api handle to keep the no-frontend scaffold warning-clean (S1481 under TreatWarningsAsErrors).
_ = api;
//#endif

await builder.Build().RunAsync();
