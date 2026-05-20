# syntax=docker/dockerfile:1.7
#
# docupilot-web — multi-stage Dockerfile (Angular 21 + Tailwind 4.3 → NGINX)
# -------------------------------------------------------------------------
# Build context is `./frontend/docupilot-web`:
#
#   docker build -t docupilot-web:dev frontend/docupilot-web
#
# Stage 1 (build):   node:22-alpine — `npm ci` (reproducible from lockfile)
#                    then `npm run build`. Angular 21 with the
#                    @angular/build esbuild builder emits to
#                    dist/docupilot-web/browser/. NOTE the `browser/`
#                    subfolder: Angular 17+ split SSR / browser outputs
#                    even when SSR is not configured. Defaults to
#                    production (angular.json sets defaultConfiguration:
#                    "production" on the build target), so plain
#                    `ng build` already yields the prod bundle — no
#                    `--configuration=production` needed.
# Stage 2 (runtime): nginx:1.27-alpine — serves the static bundle and
#                    reverse-proxies /api/* to the docupilot-api container
#                    on the compose network. See nginx.conf for the
#                    trailing-slash convention used by proxy_pass.
#
# Base-image note: the original DA-006 ticket suggested node:20-alpine,
# but Angular 21's `engines.node` field requires
# `^20.19.0 || ^22.12.0 || ^24.0.0`. node:20-alpine currently resolves to
# 20.x (varies by tag refresh). Pinning node:22-alpine guarantees a
# satisfying version with the longer LTS runway (Node 22 LTS through
# April 2027) and avoids surprise `npm install` failures.
#

# -----------------------------------------------------------------------------
# Stage 1: build
# -----------------------------------------------------------------------------
FROM node:22-alpine AS build
WORKDIR /app

# Copy lockfile + manifest first so the (slow) install layer is cached
# across source-only changes.
COPY package.json package-lock.json ./

# `npm ci` is the reproducible install: it bails if package-lock.json is
# out of date, and prunes any pre-existing node_modules. Use it instead of
# `npm install` in CI / image builds.
RUN npm ci --no-audit --no-fund --prefer-offline

# Bring in the rest of the project.
COPY . .

# Production build. Angular 21's build target has
# defaultConfiguration: "production", so this picks up output hashing,
# budgets, and the production environment.ts (the development config
# performs a fileReplacements swap to environment.development.ts; we are
# NOT in development here).
RUN npm run build

# -----------------------------------------------------------------------------
# Stage 2: runtime
# -----------------------------------------------------------------------------
FROM nginx:1.27-alpine AS runtime

# Replace the default site that ships with nginx:1.27-alpine with our SPA
# config (SPA fallback + /api reverse-proxy to the docupilot-api service).
COPY nginx.conf /etc/nginx/conf.d/default.conf

# Drop the prebuilt Angular bundle into nginx's default web root.
# Angular 21 emits to dist/<project>/browser — note the subfolder.
COPY --from=build /app/dist/docupilot-web/browser /usr/share/nginx/html

# nginx:alpine already exposes 80 in its own metadata, but redeclaring
# here documents the public-facing port for compose / readers.
EXPOSE 80

# The nginx:alpine image's default ENTRYPOINT/CMD already runs
# `nginx -g 'daemon off;'`; no override needed.
