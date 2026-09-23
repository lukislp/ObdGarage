# syntax=docker/dockerfile:1.7
# Build context is the repo root (multi-project solution) - see .github/workflows/ci.yml.


# ---------- Build ----------
FROM mcr.microsoft.com/dotnet/sdk:10.0@sha256:35d40304542c8689331f8cab17c65926cdf48fe711e289321d71924b230a7d29 AS build
WORKDIR /src

# Project files first, for Docker layer caching on unchanged dependencies.
COPY ["Directory.Build.props", "./"]
COPY ["src/ObdGarage.Web/ObdGarage.Web.csproj", "src/ObdGarage.Web/packages.lock.json", "src/ObdGarage.Web/"]
COPY ["src/ObdGarage.UI/ObdGarage.UI.csproj", "src/ObdGarage.UI/packages.lock.json", "src/ObdGarage.UI/"]
COPY ["src/ObdGarage.Core/ObdGarage.Core.csproj", "src/ObdGarage.Core/packages.lock.json", "src/ObdGarage.Core/"]
COPY ["src/ObdGarage.Application/ObdGarage.Application.csproj", "src/ObdGarage.Application/packages.lock.json", "src/ObdGarage.Application/"]
COPY ["src/ObdGarage.Data/ObdGarage.Data.csproj", "src/ObdGarage.Data/packages.lock.json", "src/ObdGarage.Data/"]
COPY ["src/ObdGarage.Obd/ObdGarage.Obd.csproj", "src/ObdGarage.Obd/packages.lock.json", "src/ObdGarage.Obd/"]
COPY ["src/ObdGarage.Shared/ObdGarage.Shared.csproj", "src/ObdGarage.Shared/packages.lock.json", "src/ObdGarage.Shared/"]

# --locked-mode: the restore must match the committed packages.lock.json files exactly.
RUN dotnet restore "src/ObdGarage.Web/ObdGarage.Web.csproj" --locked-mode

COPY src/ObdGarage.Web/ src/ObdGarage.Web/
COPY src/ObdGarage.UI/ src/ObdGarage.UI/
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
FROM mcr.microsoft.com/dotnet/aspnet:10.0@sha256:6a94333d37514e385650a3c81a55e5350b67253dbe136e9cf17e499c35606a8c AS runtime
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
