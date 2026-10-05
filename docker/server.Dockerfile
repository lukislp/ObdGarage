# syntax=docker/dockerfile:1.7
# Build context is the repo root (multi-project solution) - see .github/workflows/ci.yml.


# ---------- Build ----------
FROM mcr.microsoft.com/dotnet/sdk:10.0@sha256:e70cdb7f80b0348f5cb85f19a8f670fca061f033d57eed12fa003d58b0e06317 AS build
WORKDIR /src

# Project files first, for Docker layer caching on unchanged dependencies.
COPY ["Directory.Build.props", "./"]
COPY ["src/ObdGarage.Server/ObdGarage.Server.csproj", "src/ObdGarage.Server/packages.lock.json", "src/ObdGarage.Server/"]
COPY ["src/ObdGarage.Core/ObdGarage.Core.csproj", "src/ObdGarage.Core/packages.lock.json", "src/ObdGarage.Core/"]
COPY ["src/ObdGarage.Data/ObdGarage.Data.csproj", "src/ObdGarage.Data/packages.lock.json", "src/ObdGarage.Data/"]
COPY ["src/ObdGarage.Shared/ObdGarage.Shared.csproj", "src/ObdGarage.Shared/packages.lock.json", "src/ObdGarage.Shared/"]

# --locked-mode: the restore must match the committed packages.lock.json files exactly.
RUN dotnet restore "src/ObdGarage.Server/ObdGarage.Server.csproj" --locked-mode

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
FROM mcr.microsoft.com/dotnet/aspnet:10.0@sha256:222759b391a1aaf241166672c8f99b2d4ada452e7b5319f3c6e8f265a37b5ad4 AS runtime
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
