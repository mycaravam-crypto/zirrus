# syntax=docker/dockerfile:1
# Caddy image serving the React SPA and proxying /api to the API (only public entry point, #10).
# Node and Caddy are pinned to exact versions; digest pinning follows in W8 (#28).
FROM node:26.13.0-bookworm-slim AS build
WORKDIR /src
RUN corepack enable
COPY package.json pnpm-lock.yaml pnpm-workspace.yaml ./
COPY apps/web/package.json apps/web/
COPY packages/api-client/package.json packages/api-client/
RUN pnpm install --frozen-lockfile
COPY apps/web/ apps/web/
COPY packages/api-client/ packages/api-client/
RUN pnpm --filter @zirrus/web build

FROM caddy:2.11.9-alpine
# Run as an unprivileged user; Compose sets net.ipv4.ip_unprivileged_port_start=80 so it can bind 80/443.
RUN addgroup -S caddy && adduser -S -G caddy -H caddy \
 && mkdir -p /data /config && chown -R caddy:caddy /data /config
COPY deploy/caddy/Caddyfile /etc/caddy/Caddyfile
COPY --from=build /src/apps/web/dist /srv
USER caddy
EXPOSE 80 443
LABEL org.opencontainers.image.source="https://github.com/mycaravam-crypto/zirrus" \
      org.opencontainers.image.licenses="Apache-2.0" \
      org.opencontainers.image.title="zirrus-web"
HEALTHCHECK --interval=10s --timeout=3s --retries=6 CMD ["wget", "-q", "-O", "-", "http://127.0.0.1:8081/healthz"]
