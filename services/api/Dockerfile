# syntax=docker/dockerfile:1
#
# IncidentIQ query/dashboard API.
# Build context is the repository root, because this project references
# services/shared. Build with:
#   docker build -f services/api/Dockerfile .

# ---------- build ----------
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Restore first, on project files only, so the layer is reused whenever
# source changes but dependencies do not.
COPY Directory.Build.props ./
COPY services/shared/IncidentIQ.Shared.csproj services/shared/
COPY services/api/IncidentIQ.Api.csproj services/api/
RUN dotnet restore services/api/IncidentIQ.Api.csproj

COPY services/shared/ services/shared/
COPY services/api/ services/api/
RUN dotnet publish services/api/IncidentIQ.Api.csproj \
        --configuration Release \
        --no-restore \
        --output /app/publish \
        -p:UseAppHost=false

# ---------- runtime ----------
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime

# curl is only here so the container HEALTHCHECK below has something to call.
RUN apt-get update \
    && apt-get install --yes --no-install-recommends curl \
    && rm -rf /var/lib/apt/lists/*

WORKDIR /app
COPY --from=build /app/publish ./

ENV ASPNETCORE_URLS=http://+:8080 \
    DOTNET_gcServer=1

EXPOSE 8080

# Non-root user provided by the base image.
USER $APP_UID

# Liveness only: readiness depends on PostgreSQL/Kafka and must not restart
# this container when a dependency is briefly unavailable.
HEALTHCHECK --interval=15s --timeout=5s --start-period=25s --retries=5 \
    CMD curl --fail --silent --show-error http://localhost:8080/health/live || exit 1

ENTRYPOINT ["dotnet", "IncidentIQ.Api.dll"]
