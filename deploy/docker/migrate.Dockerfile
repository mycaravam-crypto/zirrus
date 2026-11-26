# syntax=docker/dockerfile:1
# One-off migration container (ADR 0005): a self-contained EF Core migration bundle.
FROM mcr.microsoft.com/dotnet/sdk:10.0@sha256:8c6edfb26c85153f236d26efebd9ad349aec403583f343f201ee67838b23658d AS build
WORKDIR /src
COPY global.json Directory.Build.props Directory.Packages.props .editorconfig dotnet-tools.json ./
COPY src/ src/
RUN dotnet tool restore && dotnet restore src/Zirrus.Infrastructure/Zirrus.Infrastructure.csproj --locked-mode
RUN dotnet ef migrations bundle -p src/Zirrus.Infrastructure -s src/Zirrus.Infrastructure \
      --self-contained -r linux-x64 -o /out/efbundle

FROM mcr.microsoft.com/dotnet/runtime-deps:10.0-noble-chiseled@sha256:2bb1d484500b032aaafb75e876ffbea135b969d6259cacfcbf18a793e5d6e6ec
WORKDIR /app
COPY --from=build /out/efbundle ./efbundle
USER app
LABEL org.opencontainers.image.source="https://github.com/mycaravam-crypto/zirrus" \
      org.opencontainers.image.licenses="Apache-2.0" \
      org.opencontainers.image.title="zirrus-migrate"
# Reads the connection string from the mounted secret /run/secrets/ConnectionStrings__Zirrus.
ENTRYPOINT ["/app/efbundle"]
