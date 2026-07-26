FROM node:24-bookworm-slim AS web-build
WORKDIR /src/src/clients
RUN corepack enable
COPY src/clients/package.json src/clients/pnpm-lock.yaml src/clients/pnpm-workspace.yaml ./
COPY src/clients/turbo.json src/clients/tsconfig.json src/clients/tsconfig.react.json ./
COPY src/clients/web ./web
COPY src/clients/packages ./packages
RUN pnpm install --frozen-lockfile --filter @agw/clients --filter @agw/web...
RUN NEXT_OUTPUT_MODE=export pnpm exec turbo run build --filter=@agw/web...

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS server-build
ARG APP_VERSION=0.1.0-local
WORKDIR /src
COPY src/server ./src/server
RUN dotnet publish src/server/Agw.Host/Agw.Host.csproj -c Release -o /out \
    --self-contained false -p:Version="$APP_VERSION"

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
ENV ASPNETCORE_ENVIRONMENT=Production \
    ASPNETCORE_URLS=http://0.0.0.0:8080 \
    AGW_DATA_DIR=/data \
    DOTNET_RUNNING_IN_CONTAINER=true
EXPOSE 8080
COPY --from=server-build /out ./
COPY --from=web-build /src/src/clients/web/out ./wwwroot
RUN mkdir -p /data && chown -R $APP_UID:$APP_UID /app /data
USER $APP_UID
ENTRYPOINT ["./agw-server", "serve"]
