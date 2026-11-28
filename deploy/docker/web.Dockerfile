# syntax=docker/dockerfile:1
# Caddy image serving the React SPA and proxying /api to the API (only public entry point, #10).
# Node and Caddy are pinned to exact versions and image digests (W8, #28); Dependabot proposes updates.
FROM node:26.13.1-bookworm-slim@sha256:0042e17d232a87a2cd485be1da7900fca4d10a4ad11b29e9285da1d2e54798c9 AS build
WORKDIR /src
# Node 25+ no longer bundles Corepack; install a pinned copy (MIT), then pnpm comes from package.json.
RUN npm install --global corepack@0.36.0 && corepack enable
COPY package.json pnpm-lock.yaml pnpm-workspace.yaml ./
COPY apps/web/package.json apps/web/
COPY packages/api-client/package.json packages/api-client/
RUN pnpm install --frozen-lockfile
COPY apps/web/ apps/web/
COPY packages/api-client/ packages/api-client/
RUN pnpm --filter @zirrus/web build

FROM caddy:2.11.9-alpine@sha256:003a97808e781aa9f7e75cc2f43d34d461099031faf5e20faefb9f61126836f0
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
