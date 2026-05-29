# syntax=docker/dockerfile:1.7
#
# DocuPilot.Api — multi-stage Dockerfile
# -------------------------------------
# Build context is `./backend` (the solution root). All COPY paths are
# therefore relative to `code/backend/`.
#
#   docker build \
#     -t docupilot-api:dev \
#     -f backend/src/DocuPilot.Api/Dockerfile \
#     backend
#
# Stage 1 (build):   official .NET 10 SDK image — restore via the solution
#                    so Central Package Management (Directory.Packages.props)
#                    and Directory.Build.props both resolve correctly, then
#                    publish DocuPilot.Api in Release mode.
# Stage 2 (runtime): official ASP.NET Core 10 runtime image — minimal
#                    surface area; ships only the published output. Runs as
#                    the non-root `app` user that the base image creates
#                    (UID 1654, exposed via $APP_UID since the .NET 8 line).
#

# -----------------------------------------------------------------------------
# Stage 1: build
# -----------------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Copy solution-level configuration first so the restore layer can be cached
# across code-only changes. Touching a Program.cs no longer invalidates the
# (slow) NuGet restore step — only changes to *.csproj / *.props / global.json
# bust this layer.
COPY global.json ./
COPY Directory.Build.props ./
COPY Directory.Packages.props ./
COPY DocuPilot.sln ./

# Copy every project file (preserving the src/ and tests/ tree) so
# `dotnet restore <solution>` can resolve all ProjectReferences. The full
# source tree is copied in the next layer.
COPY src/DocuPilot.Api/DocuPilot.Api.csproj                       src/DocuPilot.Api/
COPY src/DocuPilot.Services/DocuPilot.Services.csproj             src/DocuPilot.Services/
COPY src/DocuPilot.Models/DocuPilot.Models.csproj                 src/DocuPilot.Models/
COPY src/DocuPilot.Repository/DocuPilot.Repository.csproj         src/DocuPilot.Repository/
COPY src/DocuPilot.Infrastructure/DocuPilot.Infrastructure.csproj src/DocuPilot.Infrastructure/
COPY src/DocuPilot.Worker/DocuPilot.Worker.csproj                 src/DocuPilot.Worker/
COPY tests/DocuPilot.UnitTests/DocuPilot.UnitTests.csproj         tests/DocuPilot.UnitTests/
COPY tests/DocuPilot.IntegrationTests/DocuPilot.IntegrationTests.csproj tests/DocuPilot.IntegrationTests/

# Restore once for the whole solution. Cached by Docker until any of the
# above files change.
RUN dotnet restore DocuPilot.sln

# Now bring in the rest of the source.
COPY src/ src/
# Tests are not published, but the solution-level restore above already saw
# their .csproj files; we deliberately skip copying tests/ source to keep the
# build context lean inside the image (`.dockerignore` excludes them too).

# Publish the API. --no-restore reuses the cached restore from above.
# /p:UseAppHost=false suppresses the native bootstrapper since we always run
# via `dotnet DocuPilot.Api.dll` from the runtime image's ENTRYPOINT.
RUN dotnet publish src/DocuPilot.Api/DocuPilot.Api.csproj \
    --configuration Release \
    --no-restore \
    --output /app/publish \
    /p:UseAppHost=false

# -----------------------------------------------------------------------------
# Stage 2: runtime
# -----------------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

# The aspnet base image since :8.0 ships with a non-root `app` user
# (UID exposed via $APP_UID). Use it — production containers must not run
# as root. Files copied from the build stage retain their default permissions
# (world-readable), so the `app` user can read its own DLLs.
USER $APP_UID

# Bind to all interfaces on 8080 inside the container. Compose / runtime can
# override at deploy time via `-e ASPNETCORE_URLS=...`.
ENV ASPNETCORE_URLS=http://+:8080 \
    DOTNET_RUNNING_IN_CONTAINER=true \
    DOTNET_USE_POLLING_FILE_WATCHER=false

# Document the exposed port. Compose maps the public-facing port (5010 by
# default per the project's .env contract) onto this internal 8080.
EXPOSE 8080

COPY --from=build /app/publish ./

ENTRYPOINT ["dotnet", "DocuPilot.Api.dll"]
