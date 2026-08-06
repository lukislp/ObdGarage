# syntax=docker/dockerfile:1.7
# Build context is the repo root (multi-project solution) - see .github/workflows/ci.yml.

ARG DOTNET_VERSION=10.0

# ---------- Build ----------
FROM mcr.microsoft.com/dotnet/sdk:${DOTNET_VERSION} AS build
WORKDIR /src

# Project files first, for Docker layer caching on unchanged dependencies.
COPY ["src/CarApp.Server/CarApp.Server.csproj", "src/CarApp.Server/"]
COPY ["src/CarApp.Core/CarApp.Core.csproj", "src/CarApp.Core/"]
COPY ["src/CarApp.Data/CarApp.Data.csproj", "src/CarApp.Data/"]
COPY ["src/CarApp.Shared/CarApp.Shared.csproj", "src/CarApp.Shared/"]

RUN dotnet restore "src/CarApp.Server/CarApp.Server.csproj"

COPY src/CarApp.Server/ src/CarApp.Server/
COPY src/CarApp.Core/ src/CarApp.Core/
COPY src/CarApp.Data/ src/CarApp.Data/
COPY src/CarApp.Shared/ src/CarApp.Shared/

RUN dotnet publish "src/CarApp.Server/CarApp.Server.csproj" \
    -c Release \
    -o /app/publish \
    --no-restore \
    /p:UseAppHost=false

# ---------- Runtime ----------
FROM mcr.microsoft.com/dotnet/aspnet:${DOTNET_VERSION} AS runtime
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

ENTRYPOINT ["dotnet", "CarApp.Server.dll"]
