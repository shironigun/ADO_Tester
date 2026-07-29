# syntax=docker/dockerfile:1

# ─────────────────────────────────────────────────────────────────────────────
# Stage 1 — build the Vite/React bundle
# ─────────────────────────────────────────────────────────────────────────────
FROM node:20-bookworm-slim AS build
WORKDIR /app

COPY package.json package-lock.json ./
RUN npm ci

COPY . .
# VITE_QA_API stays EMPTY: the SPA calls same-origin /api and nginx proxies it to
# the backend, so no CORS and no rebuild-per-environment.
RUN npm run build

# ─────────────────────────────────────────────────────────────────────────────
# Stage 2 — serve the static bundle + reverse-proxy /api to the backend
# ─────────────────────────────────────────────────────────────────────────────
FROM nginx:1.27-alpine AS runtime
COPY nginx.conf /etc/nginx/conf.d/default.conf
COPY --from=build /app/dist /usr/share/nginx/html
EXPOSE 80
