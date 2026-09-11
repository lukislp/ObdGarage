# syntax=docker/dockerfile:1.7
# Build context is the repo root (multi-project solution) - see .github/workflows/ci.yml.


# ---------- Build ----------
FROM mcr.microsoft.com/dotnet/sdk:10.0@sha256:2fa828c68761b1b8c23d7662dc134421b9d3b59fe1425fdbc80804e390cdb24d AS build
WORKDIR /src

# Project files first, for Docker layer caching on unchanged dependencies.
COPY ["src/ObdGarage.Server/ObdGarage.Server.csproj", "src/ObdGarage.Server/"]
COPY ["src/ObdGarage.Core/ObdGarage.Core.csproj", "src/ObdGarage.Core/"]
COPY ["src/ObdGarage.Data/ObdGarage.Data.csproj", "src/ObdGarage.Data/"]
COPY ["src/ObdGarage.Shared/ObdGarage.Shared.csproj", "src/ObdGarage.Shared/"]

RUN dotnet restore "src/ObdGarage.Server/ObdGarage.Server.csproj"

COPY src/ObdGarage.Server/ src/ObdGarage.Server/
COPY src/ObdGarage.Core/ src/ObdGarage.Core/
COPY src/ObdGarage.Data/ src/ObdGarage.Data/
COPY src/ObdGarage.Shared/ src/ObdGarage.Shared/

RUN dotnet publish "src/ObdGarage.Server/ObdGarage.Server.csproj" \
    -c Release \
    -o /app/publish \
    --no-restore \
    /p:UseAppHost=false

# ---------- Runtime ----------
FROM mcr.microsoft.com/dotnet/aspnet:10.0@sha256:6a94333d37514e385650a3c81a55e5350b67253dbe136e9cf17e499c35606a8c AS runtime
WORKDIR /app

RUN getent group app || groupadd --system app \
 && id -u app 2>/dev/null || useradd --system --gid app --home-dir /app --shell /usr/sbin/nologin app \
 && mkdir -p /data \
 && chown -R app:app /app /data

ENV ASPNETCORE_ENVIRONMENT=Production \
    ASPNETCORE_URLS=http://+:5299 \
    DataDir=/data \
    DOTNET_RUNNING_IN_CONTAINER=true \
    DOTNET_NOLOGO=true \
    DOTNET_CLI_TELEMETRY_OPTOUT=true

COPY --from=build --chown=app:app /app/publish ./

USER app
VOLUME ["/data"]
EXPOSE 5299

ENTRYPOINT ["dotnet", "ObdGarage.Server.dll"]
