# syntax=docker/dockerfile:1.7
#
# The dashboard image: the SPA built once, served by nginx, configured at container
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

# nginx-unprivileged, not nginx: the master process runs as uid 101 rather than root.
# The stock image needs root purely to bind :80, so this one listens on 8080 instead —
# see docker/default.conf.template, and every compose/Traefik port that names it.
FROM nginxinc/nginx-unprivileged:1.31-alpine AS runtime

# Package installation and the file layout need root; the image drops back to 101 below.
USER root

WORKDIR /usr/share/nginx/html

# nginx's alpine image ships busybox without envsubst; gettext provides it.
RUN apk add --no-cache gettext

# Replace the default site with ours.
RUN rm -rf ./* /etc/nginx/conf.d/default.conf
COPY docker/default.conf.template /etc/nginx/default.conf.template
COPY docker/security-headers.conf.template /etc/nginx/security-headers.conf.template

COPY --from=build /app/dist/ ./
COPY docker/config.json.template ./config.json.template
COPY docker/docker-entrypoint.sh /docker-entrypoint.sh
RUN chmod +x /docker-entrypoint.sh

# The entrypoint renders three files at start — the site, the security headers and
# config.json — and deletes the config template afterwards, so uid 101 needs write
# access to those directories, not just to the files.
RUN chown -R 101:101 /usr/share/nginx/html /etc/nginx/conf.d \
  && chown 101:101 /etc/nginx

USER 101

EXPOSE 8080
ENTRYPOINT ["/docker-entrypoint.sh"]
