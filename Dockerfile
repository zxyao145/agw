FROM node:24-bookworm-slim AS web-build
WORKDIR /src/src/clients
RUN corepack enable
COPY src/clients/package.json src/clients/pnpm-lock.yaml src/clients/pnpm-workspace.yaml ./
# pnpm resolves workspace patchedDependencies during fetch, even for filtered builds.
COPY src/clients/patches ./patches
COPY src/clients/turbo.json src/clients/tsconfig.json src/clients/tsconfig.react.json ./
RUN pnpm fetch --frozen-lockfile --filter @agw/clients --filter @agw/web...
COPY src/clients/web ./web
COPY src/clients/packages ./packages
RUN pnpm install --offline --frozen-lockfile --filter @agw/clients --filter @agw/web...
RUN NEXT_OUTPUT_MODE=export pnpm exec turbo run build --filter=@agw/web...

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS server-build
ARG APP_VERSION=0.1.0-local
WORKDIR /src
COPY src/server/Directory.Build.props src/server/Directory.Packages.props ./src/server/
COPY src/server/Agw.A2A/Agw.A2A.csproj ./src/server/Agw.A2A/
COPY src/server/Agw.Agents.Contracts/Agw.Agents.Contracts.csproj ./src/server/Agw.Agents.Contracts/
COPY src/server/Agw.Agents/Agw.Agents.csproj ./src/server/Agw.Agents/
COPY src/server/Agw.Auth/Agw.Auth.csproj ./src/server/Agw.Auth/
COPY src/server/Agw.Data/Agw.Data.csproj ./src/server/Agw.Data/
COPY src/server/Agw.Files/Agw.Files.csproj ./src/server/Agw.Files/
COPY src/server/Agw.ControlPlane.Host/Agw.ControlPlane.Host.csproj ./src/server/Agw.ControlPlane.Host/
COPY src/server/Agw.DataPlane.Host/Agw.DataPlane.Host.csproj ./src/server/Agw.DataPlane.Host/
COPY src/server/Agw.Host/Agw.Host.csproj ./src/server/Agw.Host/
COPY src/server/Agw.Infrastructure/Agw.Infrastructure.csproj ./src/server/Agw.Infrastructure/
COPY src/server/Agw.Integrations/Agw.Integrations.csproj ./src/server/Agw.Integrations/
COPY src/server/Agw.Jobs.Contracts/Agw.Jobs.Contracts.csproj ./src/server/Agw.Jobs.Contracts/
COPY src/server/Agw.Jobs/Agw.Jobs.csproj ./src/server/Agw.Jobs/
COPY src/server/Agw.Migrations.Postgres/Agw.Migrations.Postgres.csproj ./src/server/Agw.Migrations.Postgres/
COPY src/server/Agw.Migrations.Sqlite/Agw.Migrations.Sqlite.csproj ./src/server/Agw.Migrations.Sqlite/
COPY src/server/Agw.Projects.Contracts/Agw.Projects.Contracts.csproj ./src/server/Agw.Projects.Contracts/
COPY src/server/Agw.Projects/Agw.Projects.csproj ./src/server/Agw.Projects/
COPY src/server/Agw.Providers/Agw.Providers.csproj ./src/server/Agw.Providers/
COPY src/server/Agw.Setup/Agw.Setup.csproj ./src/server/Agw.Setup/
COPY src/server/Agw.Shared/Agw.Shared.csproj ./src/server/Agw.Shared/
COPY src/server/Agw.Skills/Agw.Skills.csproj ./src/server/Agw.Skills/
COPY src/server/Agw.Standalone.Host/Agw.Standalone.Host.csproj ./src/server/Agw.Standalone.Host/
COPY src/server/Agw.Tools/Agw.Tools.csproj ./src/server/Agw.Tools/
RUN dotnet restore src/server/Agw.Standalone.Host/Agw.Standalone.Host.csproj
COPY src/server ./src/server
RUN dotnet publish src/server/Agw.Standalone.Host/Agw.Standalone.Host.csproj -c Release \
    -o /out/standalone --no-restore --self-contained false -p:Version="$APP_VERSION" && \
    dotnet publish src/server/Agw.ControlPlane.Host/Agw.ControlPlane.Host.csproj -c Release \
    -o /out/control-plane --no-restore --self-contained false -p:UseAppHost=true -p:Version="$APP_VERSION" && \
    dotnet publish src/server/Agw.DataPlane.Host/Agw.DataPlane.Host.csproj -c Release \
    -o /out/data-plane --no-restore --self-contained false -p:UseAppHost=true -p:Version="$APP_VERSION"

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime-base
WORKDIR /app
ENV ASPNETCORE_ENVIRONMENT=Production \
    ASPNETCORE_URLS=http://0.0.0.0:8080 \
    AGW_DATA_DIR=/data \
    DOTNET_RUNNING_IN_CONTAINER=true
EXPOSE 8080
RUN mkdir -p /data && chown -R $APP_UID:$APP_UID /app /data
USER $APP_UID

FROM runtime-base AS standalone
COPY --from=server-build --chown=$APP_UID:$APP_UID /out/standalone ./
COPY --from=web-build --chown=$APP_UID:$APP_UID /src/src/clients/web/out ./wwwroot
ENTRYPOINT ["./agw-server", "serve"]

FROM runtime-base AS control-plane
COPY --from=server-build --chown=$APP_UID:$APP_UID /out/control-plane ./
COPY --from=web-build --chown=$APP_UID:$APP_UID /src/src/clients/web/out ./wwwroot
ENTRYPOINT ["./agw-control-plane", "serve"]

FROM runtime-base AS data-plane
COPY --from=server-build --chown=$APP_UID:$APP_UID /out/data-plane ./
ENTRYPOINT ["./agw-data-plane", "serve"]

FROM standalone AS runtime
