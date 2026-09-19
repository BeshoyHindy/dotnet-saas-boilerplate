# syntax=docker/dockerfile:1.7
#
# The console image: the SPA built once, served by nginx, configured at container
# start. One image promotes across environments — nothing environment-specific is
# baked in (ADR-0004).

FROM node:25-alpine AS build
WORKDIR /app
# Corepack activates the pnpm version pinned in package.json#packageManager, so the
# image and a developer's machine resolve the same dependency tree.
RUN corepack enable
COPY package.json pnpm-lock.yaml pnpm-workspace.yaml ./
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
