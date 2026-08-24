# syntax=docker/dockerfile:1
#
# IncidentIQ web dashboard: Vite/React build served by nginx.
# Build context is this directory:
#   docker build -f apps/web/Dockerfile apps/web

# ---------- build ----------
FROM node:24-alpine AS build
WORKDIR /app

COPY package.json package-lock.json ./
RUN npm ci

COPY . .
RUN npm run build

# ---------- runtime ----------
FROM nginx:1.27-alpine AS runtime

COPY nginx.conf /etc/nginx/conf.d/default.conf
COPY --from=build /app/dist /usr/share/nginx/html

# The stock nginx entrypoint runs every script in this directory before
# starting nginx; ours rewrites config.js from the container's environment.
COPY docker-entrypoint.d/10-incidentiq-config.sh /docker-entrypoint.d/10-incidentiq-config.sh
RUN chmod +x /docker-entrypoint.d/10-incidentiq-config.sh

EXPOSE 80

HEALTHCHECK --interval=15s --timeout=5s --start-period=10s --retries=5 \
    CMD wget --quiet --spider http://127.0.0.1/healthz || exit 1
