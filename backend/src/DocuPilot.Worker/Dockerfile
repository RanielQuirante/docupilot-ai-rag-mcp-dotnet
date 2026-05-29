# syntax=docker/dockerfile:1.7
#
# DocuPilot.Worker — multi-stage Dockerfile
# -----------------------------------------
# Background pipeline host (Microsoft.NET.Sdk.Worker → Generic Host). No
# listening ports, no ASP.NET stack — uses the smaller `runtime` base image
# rather than `aspnet`.
#
# Build context is `./backend` (the solution root):
#
#   docker build \
#     -t docupilot-worker:dev \
#     -f backend/src/DocuPilot.Worker/Dockerfile \
#     backend
#

# -----------------------------------------------------------------------------
# Stage 1: build
# -----------------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Solution-level configuration first — same layering strategy as the API
# Dockerfile so restore stays cached across code-only changes.
COPY global.json ./
COPY Directory.Build.props ./
COPY Directory.Packages.props ./
COPY DocuPilot.sln ./

COPY src/DocuPilot.Api/DocuPilot.Api.csproj                       src/DocuPilot.Api/
COPY src/DocuPilot.Services/DocuPilot.Services.csproj             src/DocuPilot.Services/
COPY src/DocuPilot.Models/DocuPilot.Models.csproj                 src/DocuPilot.Models/
COPY src/DocuPilot.Repository/DocuPilot.Repository.csproj         src/DocuPilot.Repository/
COPY src/DocuPilot.Infrastructure/DocuPilot.Infrastructure.csproj src/DocuPilot.Infrastructure/
COPY src/DocuPilot.Worker/DocuPilot.Worker.csproj                 src/DocuPilot.Worker/
COPY tests/DocuPilot.UnitTests/DocuPilot.UnitTests.csproj         tests/DocuPilot.UnitTests/
COPY tests/DocuPilot.IntegrationTests/DocuPilot.IntegrationTests.csproj tests/DocuPilot.IntegrationTests/

RUN dotnet restore DocuPilot.sln

COPY src/ src/

RUN dotnet publish src/DocuPilot.Worker/DocuPilot.Worker.csproj \
    --configuration Release \
    --no-restore \
    --output /app/publish \
    /p:UseAppHost=false

# -----------------------------------------------------------------------------
# Stage 2: runtime
# -----------------------------------------------------------------------------
# `runtime:10.0` (not `aspnet:10.0`) — the Worker has no HTTP surface and
# does not need Kestrel + ASP.NET Core. Smaller image, fewer attack-surface
# CVEs to track. The base image still ships a non-root `app` user
# (UID via $APP_UID) since the .NET 8 line.
FROM mcr.microsoft.com/dotnet/runtime:10.0 AS runtime
WORKDIR /app

USER $APP_UID

ENV DOTNET_RUNNING_IN_CONTAINER=true \
    DOTNET_USE_POLLING_FILE_WATCHER=false

# Deliberately no EXPOSE / ASPNETCORE_URLS — the Worker is a background
# host, not a web service.

COPY --from=build /app/publish ./

ENTRYPOINT ["dotnet", "DocuPilot.Worker.dll"]
