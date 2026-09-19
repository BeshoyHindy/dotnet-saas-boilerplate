#!/bin/sh
# Configure the console at container start, then hand over to nginx. One built
# image serves every environment (ADR-0004): nothing below is baked into the bundle.
set -eu

# The API origin this console proxies /api and /health to. Required — a console with
# no API is a blank page, and failing here is louder than failing in the browser.
: "${APP_API_URL:?APP_API_URL is required (e.g. https://api.example.com)}"
# Object-storage origin the browser talks to directly: presigned uploads and the
# avatar/branding images. Empty means "no external storage origin", which is correct
# for a deployment serving files through the API.
: "${APP_STORAGE_URL:=}"
# Tenant the console signs operators in to. Operators live in the root tenant, so this is
# the only tenant this app ever names — there is no tenant field on its sign-in form.
: "${APP_DEFAULT_TENANT:=root}"
# Where the tenant app is deployed, used only to redirect a tenant user who signed in here
# (ADR-0008). Empty = no link offered; nothing else depends on it.
: "${APP_DASHBOARD_URL:=}"
# DNS server nginx re-resolves the API host with on every request (see the site
# template). 127.0.0.11 is Docker's embedded resolver, which serves compose service
# names and overlay-network aliases — correct for `docker compose` and for the Dokploy
# stack alike, since both run the console as a Docker container on a Docker network.
# Override for a runtime whose DNS lives elsewhere.
: "${APP_RESOLVER:=127.0.0.11}"

# A trailing slash would turn `proxy_pass` into a URI-rewriting one and drop /api.
APP_API_URL="${APP_API_URL%/}"
APP_STORAGE_URL="${APP_STORAGE_URL%/}"

# Content-Security-Policy.
#
#   script-src 'self'          — Vite emits external modules only; no inline scripts.
#   style-src … 'unsafe-inline' — React sets `style` attributes (tone colours, virtual
#                                list offsets). Nothing executable; dropping it would
#                                mean a nonce on every one of them. Google Fonts is
#                                named because index.html links its stylesheet and the
#                                theme provider lazy-injects more of them; self-host
#                                the families and both entries can go.
#   connect-src 'self' + storage — the API is same-origin through the proxy above;
#                                presigned uploads go straight to object storage.
#   img-src blob:/data:         — file previews and QR codes are generated client-side.
#   frame-ancestors 'none'      — this console is never framed.
APP_CSP="default-src 'self'; \
base-uri 'self'; \
object-src 'none'; \
frame-ancestors 'none'; \
form-action 'self'; \
script-src 'self'; \
style-src 'self' 'unsafe-inline' https://fonts.googleapis.com; \
font-src 'self' data: https://fonts.gstatic.com; \
img-src 'self' data: blob:${APP_STORAGE_URL:+ ${APP_STORAGE_URL}}; \
connect-src 'self'${APP_STORAGE_URL:+ ${APP_STORAGE_URL}}; \
worker-src 'self' blob:; \
manifest-src 'self'"

export APP_API_URL APP_STORAGE_URL APP_DEFAULT_TENANT APP_DASHBOARD_URL APP_RESOLVER APP_CSP

# Substitute ONLY our own variables: nginx configuration is full of $host, $uri and
# friends that envsubst would otherwise blank out.
envsubst '${APP_API_URL} ${APP_RESOLVER}' \
  < /etc/nginx/default.conf.template > /etc/nginx/conf.d/default.conf
envsubst '${APP_CSP}' \
  < /etc/nginx/security-headers.conf.template > /etc/nginx/security-headers.conf
envsubst '${APP_DEFAULT_TENANT} ${APP_DASHBOARD_URL}' \
  < /usr/share/nginx/html/config.json.template > /usr/share/nginx/html/config.json

# Drop the template so it is never served.
rm /usr/share/nginx/html/config.json.template

exec nginx -g 'daemon off;'
