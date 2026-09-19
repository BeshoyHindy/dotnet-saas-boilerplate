#!/bin/sh
set -e

: "${APP_API_URL:?APP_API_URL is required (e.g. https://api.example.com)}"
: "${APP_DEFAULT_TENANT:=root}"

export APP_API_URL APP_DEFAULT_TENANT

envsubst < /usr/share/nginx/html/config.json.template \
       > /usr/share/nginx/html/config.json
rm /usr/share/nginx/html/config.json.template

exec nginx -g 'daemon off;'
