#!/bin/sh
set -e

# Fail fast on missing required values rather than serve a broken bundle.
: "${APP_API_URL:?APP_API_URL is required (e.g. https://api.example.com)}"
: "${APP_DASHBOARD_URL:?APP_DASHBOARD_URL is required (e.g. https://app.example.com)}"

# Defaults for non-required values.
: "${APP_DEFAULT_TENANT:=root}"

export APP_API_URL APP_DASHBOARD_URL APP_DEFAULT_TENANT

# Render the runtime config from the template, writing into nginx's web root.
envsubst < /usr/share/nginx/html/config.json.template > /usr/share/nginx/html/config.json

# Drop the template so it isn't served accidentally.
rm /usr/share/nginx/html/config.json.template

exec nginx -g 'daemon off;'
