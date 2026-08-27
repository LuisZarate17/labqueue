# syntax=docker/dockerfile:1

# ---------------------------------------------------------------- build
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Restore ahead of the source copy so editing a .cs file does not invalidate the
# package layer. Restoring the API project rather than labqueue.slnx keeps the test
# project — and Testcontainers with it — out of the image entirely.
COPY global.json ./
COPY src/LabQueue.Api/LabQueue.Api.csproj   src/LabQueue.Api/
COPY src/LabQueue.Core/LabQueue.Core.csproj src/LabQueue.Core/
RUN dotnet restore src/LabQueue.Api/LabQueue.Api.csproj

COPY src/ src/

# UseAppHost=false drops the native launcher; the entrypoint invokes `dotnet` directly.
# No trimming or AOT: EF Core resolves too much by reflection for either to be safe here.
RUN dotnet publish src/LabQueue.Api/LabQueue.Api.csproj \
      -c Release \
      -o /app/publish \
      --no-restore \
      /p:UseAppHost=false

# ---------------------------------------------------------------- runtime
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime

# The runtime image ships no HTTP client, and HEALTHCHECK needs one. This has to happen
# while we are still root — hence the ordering: install, then drop privileges, and never
# the reverse. --no-install-recommends plus the apt-lists cleanup keeps it to a few MB.
RUN apt-get update \
 && apt-get install -y --no-install-recommends curl \
 && rm -rf /var/lib/apt/lists/*

WORKDIR /app

# Copied as root and left world-readable. The app only ever reads from /app, so the
# non-root user below needs no ownership of it.
COPY --from=build /app/publish ./

ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080

HEALTHCHECK --interval=30s --timeout=3s --start-period=20s --retries=3 \
  CMD curl -fsS http://localhost:8080/health || exit 1

# APP_UID (1654) is defined by the base image. Last instruction before the entrypoint:
# everything above needed root, everything below must not have it.
USER $APP_UID

ENTRYPOINT ["dotnet", "LabQueue.Api.dll"]
