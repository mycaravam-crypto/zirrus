# syntax=docker/dockerfile:1
# Zirrus.Worker image (ADR 0011, #10). Base images pinned by digest; Dependabot proposes updates.
FROM mcr.microsoft.com/dotnet/sdk:10.0@sha256:8c6edfb26c85153f236d26efebd9ad349aec403583f343f201ee67838b23658d AS build
WORKDIR /src
COPY global.json Directory.Build.props Directory.Packages.props .editorconfig ./
COPY src/ src/
RUN dotnet restore src/Zirrus.Worker/Zirrus.Worker.csproj --locked-mode
# The OpenAPI document is generated and committed during development; skip generation in images.
RUN dotnet publish src/Zirrus.Worker/Zirrus.Worker.csproj -c Release -o /app --no-restore -p:OpenApiGenerateDocuments=false

# Chiseled runtime: no shell or package manager; non-root "app" user (UID 1654).
FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled@sha256:bcdfe6b8e923ad3d05f01dfa805ab97c99d742823ce030856002a6db38122712
WORKDIR /app
COPY --from=build /app ./
ENV ASPNETCORE_HTTP_PORTS=8080
USER app
EXPOSE 8080
LABEL org.opencontainers.image.source="https://github.com/mycaravam-crypto/zirrus" \
      org.opencontainers.image.licenses="Apache-2.0" \
      org.opencontainers.image.title="zirrus-worker"
# No shell in the image: the app probes its own readiness endpoint (HostingExtensions).
HEALTHCHECK --interval=10s --timeout=5s --start-period=20s --retries=6 CMD ["dotnet", "/app/Zirrus.Worker.dll", "--healthcheck"]
ENTRYPOINT ["dotnet", "/app/Zirrus.Worker.dll"]
