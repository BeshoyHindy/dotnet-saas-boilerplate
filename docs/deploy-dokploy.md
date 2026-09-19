# Deploying to Dokploy

From a blank VPS to a healthy, HTTPS application. Everything the deployment
needs is in this file — the compose stacks, the variables they read, the
commands that produce the two values you cannot invent, and what each failure
mode looks like.

The shape is ADR-0005: CI builds images and pushes them to GHCR, Dokploy pulls
them and never builds; the data plane and the application are two separate
stacks so that redeploying the app cannot touch the database; migrations run as
a one-shot container the API waits for; and readiness is a Traefik
load-balancer probe on `/health/ready`, because the API image is chiselled and
has no shell to run a container `HEALTHCHECK` with.

| File | What it is |
|---|---|
| [`deploy/dokploy/data-services.compose.yml`](../deploy/dokploy/data-services.compose.yml) | PostgreSQL, Valkey, MinIO, bucket creator |
| [`deploy/dokploy/app.compose.yml`](../deploy/dokploy/app.compose.yml) | migrator → api → console, all pulled from GHCR |
| [`deploy/dokploy/.env.example`](../deploy/dokploy/.env.example) | the variable contract, key names only |
| [`deploy/dokploy/dokploy-deploy.sh`](../deploy/dokploy/dokploy-deploy.sh) | trigger a deploy from CI and wait for it |
| [`deploy/dokploy/tests/run.sh`](../deploy/dokploy/tests/run.sh) | contract tests over all of the above |

## 0. Before you start

- A Linux VPS with a public IP, 4 GB RAM or more, nothing else on ports 80/443.
- A domain you control, with DNS you can edit.
- The container images published to GHCR (see [Images](#images)).

Three hostnames point at the server. Create one `A` record each:

| Record | Example | Serves |
|---|---|---|
| API | `api.example.com` | the .NET API |
| Console | `app.example.com` | the React console |
| Storage | `storage.example.com` | MinIO's S3 endpoint |

Storage needs its own public name because the API hands the browser **presigned**
upload and download URLs. An S3 signature covers the host it was signed for, so
the host the API signs with has to be the host the browser resolves — which is
why `Storage__S3__ServiceUrl` is the public URL and not an internal alias.

Do this before deploying: Let's Encrypt issues certificates over HTTP, so a
record that does not resolve yet is a certificate that never appears.

## 1. Install Dokploy

On a fresh server, as root:

```bash
curl -sSL https://dokploy.com/install.sh | sh
```

It installs Docker, initialises a single-node Swarm, creates the shared overlay
network **`dokploy-network`**, and starts Traefik and the Dokploy UI on port
3000. Open `http://<server-ip>:3000` and create the first user — that account is
the server owner, so do it immediately; the form is open until someone does.

Both compose stacks declare `dokploy-network` as `external: true`. They join the
network the installer made and never create one, which is what lets the two
stacks — separate compose projects — see each other, and what lets Traefik see
them.

## 2. Images

CI publishes three images to GHCR:

| Image | From |
|---|---|
| `ghcr.io/<owner>/boilerplate-api` | `src/Host/Dockerfile`, target `api` |
| `ghcr.io/<owner>/boilerplate-db-migrator` | `src/Host/Dockerfile`, target `migrator` |
| `ghcr.io/<owner>/boilerplate-console` | `clients/admin/Dockerfile` |

`IMAGE_TAG` is the only thing that differs between a deploy and a rollback:

| Environment | Branch | Tag |
|---|---|---|
| staging | `develop` | `dev-<sha>` (or `dev-latest`) |
| production | `v*` tag on `main` | the version, e.g. `1.4.0` |

Production must name a fixed version. `latest` makes a redeploy irreproducible —
the same button press a week later brings up different code.

> **Until the CI-workflows change lands**, the console image and the `dev-*`
> tags above do not exist yet, and CI still builds the two .NET images without
> the Dockerfile. Build and push them by hand meanwhile:
>
> ```bash
> OWNER=<your-ghcr-owner>; TAG=<your-tag>
> echo "$GHCR_TOKEN" | docker login ghcr.io -u "$OWNER" --password-stdin
> docker build -f src/Host/Dockerfile --target api      -t "ghcr.io/$OWNER/boilerplate-api:$TAG" .
> docker build -f src/Host/Dockerfile --target migrator -t "ghcr.io/$OWNER/boilerplate-db-migrator:$TAG" .
> docker build clients/admin -t "ghcr.io/$OWNER/boilerplate-console:$TAG"
> docker push "ghcr.io/$OWNER/boilerplate-api:$TAG"
> docker push "ghcr.io/$OWNER/boilerplate-db-migrator:$TAG"
> docker push "ghcr.io/$OWNER/boilerplate-console:$TAG"
> ```
>
> Set `IMAGE_TAG` to whatever `$TAG` you used. Nothing else in this guide
> changes.

If the packages are private, add the credentials once under **Settings →
Registry** in Dokploy (a GitHub personal access token with `read:packages`);
Dokploy then authenticates the pulls.

## 3. Fill in the variables

Both stacks read their values from Dokploy's **Environment** tab, which Dokploy
writes to a `.env` beside the compose file and `docker compose` interpolates.
`deploy/dokploy/.env.example` is the complete list of keys with no values, and a
contract test fails if a stack ever interpolates a variable that file does not
document (or the reverse).

There are deliberately **no defaults** in the compose files. `docker compose`
substitutes an empty string for a variable you forgot; with a default, a typo in
`ALLOWED_HOSTS` becomes a silently wrong allow-list, and without one it becomes
a container that refuses to boot and says why.

### Generate the secrets

Never type these by hand and never reuse a sample. The API runs as Production,
and `ProductionConfigurationGuard` refuses to boot on anything that reads like a
placeholder — `changeme`, `secret`, `dev-only` and friends:

```bash
openssl rand -base64 24 | tr -dc 'A-Za-z0-9'        # POSTGRES_PASSWORD
openssl rand -base64 24 | tr -dc 'A-Za-z0-9'        # MINIO_ROOT_PASSWORD
openssl rand -base64 48 | tr -dc 'A-Za-z0-9'        # JWT_SIGNING_KEY (32+ chars)
echo "$(openssl rand -base64 18 | tr -dc 'A-Za-z0-9')Aa1"   # SEED_ADMIN_PASSWORD
```

`SEED_ADMIN_PASSWORD` must satisfy the Identity policy: 10+ characters with an
upper, a lower and a digit — hence the suffix. It is used once, by the migrator,
to seed the root tenant's admin. Change it from the console after first sign-in.

### Find `PROXY_KNOWN_NETWORK`

This one is read off the server, not chosen. In Production the API turns on
forwarded headers with `TrustAnyProxy: false`, and `ProxyOptionsValidator` fails
the boot until a proxy is named — because `dokploy-network` is shared with every
other stack on the host, and a trusted neighbour could spoof `X-Forwarded-For`
into the rate limiter and the audit trail. On the server:

```bash
docker network inspect dokploy-network -f '{{range .IPAM.Config}}{{.Subnet}}{{end}}'
# e.g. 10.0.1.0/24
```

Use exactly that CIDR. `ProxyOptions__KnownProxies__0` with Traefik's container
IP works too, but the IP changes when Traefik restarts and the subnet does not.

### The worksheet

Same keys for both environments, different values. `[data]` goes in the
data-services service's Environment tab, `[app]` in the application's, `[both]`
in both.

| Key | Scope | Staging | Production |
|---|---|---|---|
| `STACK_NAME` | both | `boilerplate-staging` | `boilerplate-production` |
| `IMAGE_REGISTRY` | app | `ghcr.io` | `ghcr.io` |
| `IMAGE_OWNER` | app | your GHCR owner, lowercase | same |
| `IMAGE_TAG` | app | `dev-latest` | `1.4.0` |
| `API_DOMAIN` | app | `api.staging.example.com` | `api.example.com` |
| `CONSOLE_DOMAIN` | app | `app.staging.example.com` | `app.example.com` |
| `STORAGE_DOMAIN` | both | `storage.staging.example.com` | `storage.example.com` |
| `TRAEFIK_CERT_RESOLVER` | both | `letsencrypt` | `letsencrypt` |
| `ALLOWED_HOSTS` | app | `api.staging.example.com` | `api.example.com` |
| `PROXY_KNOWN_NETWORK` | app | from `docker network inspect` | same server, same value |
| `POSTGRES_DB` / `POSTGRES_USER` | both | `boilerplate` | `boilerplate` |
| `POSTGRES_PASSWORD` | both | generated | generated, different |
| `MINIO_ROOT_USER` | both | `boilerplate` | `boilerplate` |
| `MINIO_ROOT_PASSWORD` | both | generated | generated, different |
| `STORAGE_BUCKET` | both | `boilerplate` | `boilerplate` |
| `STORAGE_REGION` | app | `us-east-1` | `us-east-1` |
| `JWT_SIGNING_KEY` | app | generated | generated, different |
| `SEED_ADMIN_PASSWORD` | app | generated | generated, different |
| `MAIL_FROM` | app | `no-reply@example.com` | `no-reply@example.com` |
| `MAIL_DISPLAY_NAME` | app | your product name | same |
| `SMTP_HOST` / `SMTP_PORT` | app | your relay, `587` | same |
| `SMTP_USERNAME` / `SMTP_PASSWORD` | app | relay credentials | same |
| `SMTP_SECURE_SOCKET` | app | `StartTls` | `StartTls` |
| `APP_DEFAULT_TENANT` | app | `root` | `root` |
| `OTEL_EXPORTER_ENABLED` | app | `false` | `false`, or `true` with a collector |
| `OTEL_EXPORTER_OTLP_ENDPOINT` | app | blank | blank, or a collector URL |

`STACK_NAME` is what keeps the two environments apart on one server: it prefixes
every `dokploy-network` alias and every Traefik router, service and middleware
name. Two environments sharing a `STACK_NAME` will fight over both.

Telemetry needs **both** OTLP keys or neither: Production ships the exporter
disabled, so an endpoint with `OTEL_EXPORTER_ENABLED=false` is dead
configuration that looks live.

`ALLOWED_HOSTS` is a semicolon-separated list and must contain `API_DOMAIN`.
`*` is rejected outright. It is also the `Host` that Traefik's readiness probe
sends, so getting it wrong looks like a backend that is permanently down rather
than like a 400 you would ever see in a browser.

## 4. Create the project and the two stacks

In the Dokploy UI: **Projects → Create Project**, one per environment
(`boilerplate-staging`, `boilerplate-production`).

### 4a. The data plane — deploy this one first

**Create Service → Compose**, name it `data-services`, then:

| Field | Value |
|---|---|
| Source | Git (or the GitHub provider, if the repo is private) |
| Repository | this repository |
| Branch | `develop` for staging, `main` for production |
| Compose Path | `./deploy/dokploy/data-services.compose.yml` |
| Compose Type | **Docker Compose** |

**Compose Type must be Docker Compose, not Stack.** Docker Stack ignores
container-level `labels` and has no `depends_on` conditions, so both the Traefik
routing and the migrator gate would silently stop working.

Paste the `[data]` and `[both]` variables into **Environment**, then **Deploy**.
Watch the deployment log until it settles; `postgres`, `valkey` and `minio`
should be running, and `minio-init` and `minio-public-prefix` should each have
exited 0 — the first creating the bucket, the second opening anonymous reads on
the `uploads/` prefix that avatars and tenant branding are served from.

Deploy this stack again only when a data-service image version changes. That is
the whole point of the split: an application redeploy can never recreate,
restart or wipe the database, because it is a different compose project with its
own volumes.

### 4b. The application

**Create Service → Compose**, name it `app`, same source settings but:

| Field | Value |
|---|---|
| Compose Path | `./deploy/dokploy/app.compose.yml` |
| Compose Type | **Docker Compose** |

Paste the `[app]` and `[both]` variables, then **Deploy**.

What happens, in order:

1. Docker pulls the three images at `IMAGE_TAG` (`pull_policy: always`, so a
   moved tag is actually re-fetched).
2. `migrator` starts, waits for PostgreSQL, takes a PostgreSQL advisory lock,
   applies every pending migration, seeds the root tenant and its admin user,
   and exits 0. Concurrent deploys are safe: the second one blocks on the lock.
3. `api` starts — and only now, because of
   `depends_on: migrator: condition: service_completed_successfully`. If the
   migration fails, the migrator exits non-zero, the API never starts and the
   deploy fails. The API never migrates at startup.
4. `console` starts and renders `/config.json` from `APP_API_URL`,
   `APP_DASHBOARD_URL` and `APP_DEFAULT_TENANT`.
5. Traefik picks up the labels, requests certificates for the three hostnames,
   and begins probing `GET /health/ready` on the API every 10 s. A container
   that fails the probe is taken out of rotation.

## 5. Verify

```bash
curl -fsS https://api.example.com/health/live     # process is up
curl -fsS https://api.example.com/health/ready    # dependencies are usable
curl -fsSI https://app.example.com/ | head -1     # console serves
curl -fsS https://app.example.com/config.json     # console got its runtime config
curl -fsSI https://storage.example.com/minio/health/live | head -1
```

`/health/live` runs no checks and answers as soon as the process is listening.
`/health/ready` runs only the checks the API cannot serve without — it is the
one Traefik gates traffic on. `GET /health` returns the full report of every
check, which is the one to read when `ready` is failing and you want to know
which dependency.

Then sign in at `https://app.example.com` as `admin@root.com` with
`SEED_ADMIN_PASSWORD`, and change that password.

## 6. Deploy from CI

`deploy/dokploy/dokploy-deploy.sh` triggers a deployment and waits for **that**
deployment to finish. Dokploy's deploy endpoint enqueues a job and answers
immediately, so a plain webhook can only ever report that the request was
accepted — a pipeline using one goes green while the deployment is still
failing. The script stamps a unique token into the deployment title, polls for
the row carrying that token, and fails closed: a failed or cancelled deployment,
a timeout, an HTTP error, an unparseable body and a status it does not recognise
all exit non-zero.

### Get the two identifiers

**API token**: Dokploy UI → **Settings → API Keys** (older builds put it under
Settings → Profile → API/CLI) → generate. Store it as a repository secret and
pass it only as `DOKPLOY_API_KEY`. The script has no `--api-key` flag on
purpose and refuses one: `ps` is world-readable, so a token on an argv is
visible to every other process on the runner. Internally it reaches curl
through a config file on stdin rather than a `--header` argument.

**Compose id**: open the compose service in the UI and read `composeId` out of
the URL, or ask the API:

```bash
curl -s https://dokploy.example.com/api/project.all \
  -H 'accept: application/json' -H "x-api-key: $DOKPLOY_API_KEY" \
  | jq -r '.[] | .environments[]?.compose[]? | "\(.composeId)  \(.name)"'
```

### Run it

```bash
export DOKPLOY_URL=https://dokploy.example.com
export DOKPLOY_API_KEY=…            # repository secret — environment only
export DOKPLOY_COMPOSE_ID=…         # the app stack's composeId

deploy/dokploy/dokploy-deploy.sh --timeout 900 --interval 10
```

Needs `curl` and `jq`. Flags: `--url`, `--compose-id`, `--timeout` (default
900 s), `--interval` (default 10 s), `--title`; `--help` prints the lot. The
token is environment-only — there is no `--api-key`.

To roll the image forward first, set `IMAGE_TAG` in the stack's Environment tab
(by hand or through `compose.update`) and then run the script — Dokploy reads
the new value on the next deployment.

### The API it speaks

Reference: <https://docs.dokploy.com/docs/api>. The base URL is your Dokploy
instance plus `/api`; every call authenticates with an `x-api-key` header. A
browsable Swagger UI lives at `<your-dokploy-host>:3000/swagger`.

| Call | Purpose |
|---|---|
| `GET /api/project.all` | list projects and their services — where `composeId` comes from |
| `POST /api/compose.deploy` | `{"composeId","title","description"}` — enqueues a deployment and returns immediately. The `title` is stored verbatim on the deployment row, which is what makes correlation possible: the response itself carries no deployment id. |
| `GET /api/deployment.allByCompose?composeId=…` | that compose service's deployments, newest first |

Deployment `status` is one of `running`, `done`, `error`, `cancelled`. The
script treats anything else as a failure: a status it has never heard of is a
Dokploy it does not understand, and guessing "probably fine" is how a broken
release ships. Dokploy keeps only the last ten deployments per service, which is
another reason to correlate by token rather than by "the newest one".

## 7. Two environments, one pair of files

Staging and production run the *same* two compose files from the same
repository. Everything that differs is a variable:

| | Staging | Production |
|---|---|---|
| Tracks | `develop` | `v*` tags on `main` |
| `IMAGE_TAG` | `dev-<sha>` | `1.4.0` |
| Domains | `*.staging.example.com` | `*.example.com` |
| `STACK_NAME` | `boilerplate-staging` | `boilerplate-production` |

Per ADR-0007, every merge into `develop` deploys staging, and a `v*` tag on
`main` deploys production. A production deploy is therefore a tag, a fixed
`IMAGE_TAG` and one run of the deploy script.

**The branch and the tag decide different things.** The branch on the compose
service is only where Dokploy reads the two YAML files from — it never builds
anything, so `main` versus `develop` selects the *deployment shape*. What is
actually run is `IMAGE_TAG`. So a production release is: merge the release into
`main`, tag it, let CI publish `1.4.0`, then set `IMAGE_TAG=1.4.0` and deploy.
Bumping the branch alone changes nothing about the running code, and bumping
`IMAGE_TAG` alone is the normal case.

**Rollback** is the previous tag: set `IMAGE_TAG` back and deploy. The data
stack is untouched — but a migration that has already run is *not* rolled back
by this, so a rollback across a destructive migration needs a restore, not a
redeploy.

## 8. Tests

```bash
bash deploy/dokploy/tests/run.sh
```

Plain bash, no network, no Docker daemon required (the `docker compose config`
checks skip themselves when docker is absent); `jq` is needed, as it is by the
deploy script itself. Two suites:

- **`compose-contract.test.sh`** — no `build:` anywhere, every application image
  interpolated from `IMAGE_TAG`, every data-service image pinned and never
  `:latest`, the migrator gate, the external network, the `/health/ready`
  load-balancer probe with `passhostheader`, no host port published, no literal
  credential, anonymous storage reads scoped to `uploads/` and never widened to
  the bucket or to `tenants/`, and `.env.example` matching the interpolated
  variables in both directions.
- **`dokploy-deploy.test.sh`** — the deploy script against a stubbed `curl`:
  success, failure, cancellation, timeout, an unregistered deployment, a
  malformed body, a JSON object where an array was documented, an unknown
  status, HTTP errors, a refused `--api-key`, and *somebody else's* deployment
  going green while ours is still running.

Also useful directly:

```bash
docker compose -f deploy/dokploy/data-services.compose.yml config -q
docker compose -f deploy/dokploy/app.compose.yml config -q
```

(with an env file supplying the keys — the contract test generates a throwaway
one for exactly this).

## 9. Hardening follow-up

Two things this stack does are correct-but-broad, and worth tightening once a
deployment is real:

- **The API signs with the MinIO root credentials.** `Storage__S3__AccessKey` /
  `SecretKey` are `MINIO_ROOT_USER` / `MINIO_ROOT_PASSWORD`, so the application
  can create and delete buckets, not only objects in its own. The tighter shape
  is a MinIO service account (`mc admin user svcacct add`) carrying a policy
  scoped to `arn:aws:s3:::<bucket>/*` with just the object verbs the app uses —
  `GetObject`, `PutObject`, `DeleteObject`, `ListBucket` — and those keys in the
  app stack instead. Nothing in the compose files changes but the two values.
- **Anonymous reads are open on `uploads/`.** That prefix holds avatars and
  tenant branding, which are unsigned URLs by design. It is as narrow as the
  code currently allows, but it is still public-by-prefix rather than
  public-by-object; per-object ACLs would be narrower.

Related, and an application-level gap rather than a deployment one: the Files
module builds public URLs for `Visibility.Public` rows under `tenants/`, where
public and private objects share a key space. That prefix is deliberately *not*
anonymously readable — opening it would make `ChangeFileVisibility` unable to
take access away again — so those public URLs will 403 until the module serves
public files through a presigned URL or a per-object grant.

## 10. When it does not work

| Symptom | Cause |
|---|---|
| `Production configuration is not usable: Missing required configuration 'AllowedHosts'` | `ALLOWED_HOSTS` is empty or absent from the Environment tab. |
| `… 'AllowedHosts' contains '*'` | Name the hostnames. `*` is rejected in Production because a poisoned `Host` reaches password-reset links. |
| `… still holds a template placeholder` | A secret looks like a sample (`changeme`, `secret`, `dev-only`). Regenerate it with the commands in §3. |
| `ProxyOptions: Enabled is true but nothing is trusted` | `PROXY_KNOWN_NETWORK` is unset. Run the `docker network inspect` command in §3. |
| 404 from Traefik on a domain | The stack deployed before the DNS record existed, or `API_DOMAIN`/`CONSOLE_DOMAIN` does not match the record. Compose domains are label-driven and **not** hot-reloaded: redeploy after changing one. |
| 502/503 from Traefik, API container running | The readiness probe is failing. `curl` the API container directly from the host, or read `GET /health` for the full report. A missing `API_DOMAIN` in `ALLOWED_HOSTS` does this — the probe sends that Host and host filtering answers 400. |
| Certificate never issued | The `A` record did not resolve when Traefik asked, or port 80 is blocked. Fix DNS, then redeploy. |
| Console loads but every call is a CORS error | `CONSOLE_DOMAIN` is not the origin the browser actually uses; it is what the API puts in its allow-list. |
| Password-reset links point at a container IP | Traefik is not passing the original `Host`. `passhostheader=true` must stay on the API's load-balancer labels — `X-Forwarded-Host` is deliberately never honoured, so that label is the only path for the real host. |
| Uploads fail with a signature error | `STORAGE_DOMAIN` differs between the two stacks, or `Storage__S3__ServiceUrl` was pointed at an internal alias. The signature covers the host. |
| `NoSuchBucket` on first upload | The data stack's `minio-init` did not run, or `STORAGE_BUCKET` differs between the two stacks. |
| Avatars and tenant logos 403 | `minio-public-prefix` did not run. Redeploy the data stack; it is idempotent. Buckets are private by default and those URLs are unsigned. |
| Traces and metrics never arrive | `OTEL_EXPORTER_ENABLED` is not `true`. The endpoint alone does nothing — Production ships the exporter disabled. |
| `migrator` retries PostgreSQL and then fails | The data stack is not up, or `POSTGRES_PASSWORD` was changed against an existing volume. |
| `api` never starts, no error of its own | The migrator exited non-zero. Read the migrator's log — the API is gated on it and is behaving correctly by not starting. |
| Deploy script reports `timed out` while the UI shows success | The successful deployment is not the one the script started (another deploy of the same service). Check the deployment titles; the script's carries its `dpl-…` token. |

## See also

- [`docs/adr/0005-dokploy-deployment-shape.md`](adr/0005-dokploy-deployment-shape.md) — why the stacks are shaped this way
- [`docs/adr/0007-gitflow-branching.md`](adr/0007-gitflow-branching.md) — which branch deploys where
- [`README.md`](../README.md) — running the same images locally with `docker compose`
