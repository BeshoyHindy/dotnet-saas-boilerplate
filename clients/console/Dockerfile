# syntax=docker/dockerfile:1.7
#
# The console image: the SPA built once, served by nginx, configured at container
# start. One image promotes across environments — nothing environment-specific is
# baked in (ADR-0004).

FROM node:25-alpine AS build
WORKDIR /app
COPY package.json pnpm-lock.yaml pnpm-workspace.yaml ./
# Node 25 ships without corepack, and corepack does not support Node 25 yet, so pnpm is
# installed directly — at the version pinned in package.json#packageManager, which stays
# the one source of truth for it.
RUN npm install --global "pnpm@$(node -p "require('./package.json').packageManager.split('@')[1]")"
RUN pnpm install --frozen-lockfile
COPY . .
RUN pnpm build

FROM nginx:alpine AS runtime
WORKDIR /usr/share/nginx/html

# nginx:alpine ships busybox without envsubst; gettext provides it.
RUN apk add --no-cache gettext

# Replace the default site with ours.
RUN rm -rf ./* /etc/nginx/conf.d/default.conf
COPY docker/default.conf.template /etc/nginx/default.conf.template
COPY docker/security-headers.conf.template /etc/nginx/security-headers.conf.template

COPY --from=build /app/dist/ ./
COPY docker/config.json.template ./config.json.template
COPY docker/docker-entrypoint.sh /docker-entrypoint.sh
RUN chmod +x /docker-entrypoint.sh

# Non-root: nginx:alpine includes the `nginx` user (uid 101). We do NOT switch to
# it here because the upstream image's master process needs root to bind :80;
# nginx forks its workers as `nginx` on its own per its default config.

EXPOSE 80
ENTRYPOINT ["/docker-entrypoint.sh"]
