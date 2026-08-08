# syntax=docker/dockerfile:1.7
# Build context is the repo root (multi-project solution) - see .github/workflows/ci.yml.

ARG DOTNET_VERSION=10.0

# ---------- Build ----------
FROM mcr.microsoft.com/dotnet/sdk:${DOTNET_VERSION} AS build
WORKDIR /src

# Project files first, for Docker layer caching on unchanged dependencies.
COPY ["src/ObdGarage.Web/ObdGarage.Web.csproj", "src/ObdGarage.Web/"]
COPY ["src/ObdGarage.Core/ObdGarage.Core.csproj", "src/ObdGarage.Core/"]
COPY ["src/ObdGarage.Application/ObdGarage.Application.csproj", "src/ObdGarage.Application/"]
COPY ["src/ObdGarage.Data/ObdGarage.Data.csproj", "src/ObdGarage.Data/"]
COPY ["src/ObdGarage.Obd/ObdGarage.Obd.csproj", "src/ObdGarage.Obd/"]
COPY ["src/ObdGarage.Shared/ObdGarage.Shared.csproj", "src/ObdGarage.Shared/"]

RUN dotnet restore "src/ObdGarage.Web/ObdGarage.Web.csproj"

COPY src/ObdGarage.Web/ src/ObdGarage.Web/
COPY src/ObdGarage.Core/ src/ObdGarage.Core/
COPY src/ObdGarage.Application/ src/ObdGarage.Application/
COPY src/ObdGarage.Data/ src/ObdGarage.Data/
COPY src/ObdGarage.Obd/ src/ObdGarage.Obd/
COPY src/ObdGarage.Shared/ src/ObdGarage.Shared/

# No --no-restore: the restore above ran before wwwroot/static assets existed in the
# build context (copied in afterwards, for layer caching), so publishing against that
# stale manifest would silently drop static web assets, including Blazor's own framework
# scripts - breaking server-side interactivity at runtime. See ObdGarage.Web's own comments.
RUN dotnet publish "src/ObdGarage.Web/ObdGarage.Web.csproj" \
    -c Release \
    -o /app/publish \
    /p:UseAppHost=false \
    /p:StaticWebAssetsEnabled=true \
    /p:StaticWebAssetsCopyToOutput=true

# ---------- Runtime ----------
FROM mcr.microsoft.com/dotnet/aspnet:${DOTNET_VERSION} AS runtime
WORKDIR /app

RUN getent group app || groupadd --system app \
 && id -u app 2>/dev/null || useradd --system --gid app --home-dir /app --shell /usr/sbin/nologin app \
 && mkdir -p /app/data \
 && chown -R app:app /app

ENV ASPNETCORE_ENVIRONMENT=Production \
    ASPNETCORE_URLS=http://+:5199 \
    DOTNET_RUNNING_IN_CONTAINER=true \
    DOTNET_NOLOGO=true \
    DOTNET_CLI_TELEMETRY_OPTOUT=true

COPY --from=build --chown=app:app /app/publish ./

USER app
# Runtime data (photos, sync-auth.json, sync-state.json) lives under ContentRootPath/data,
# i.e. /app/data here - see ObdGarage.Web/Program.cs.
VOLUME ["/app/data"]
EXPOSE 5199

ENTRYPOINT ["dotnet", "ObdGarage.Web.dll"]
