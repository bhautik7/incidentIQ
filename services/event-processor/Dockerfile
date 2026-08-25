# syntax=docker/dockerfile:1
#
# IncidentIQ event processor worker.
# Build context is the repository root, because this project references the
# shared class libraries under services/. Build with:
#   docker build -f services/event-processor/Dockerfile .

# ---------- build ----------
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# The whole services/ tree is copied rather than an enumerated list of project
# files. Listing them individually caches the restore layer slightly better, but
# it silently breaks this build every time a new library is added - which has
# now happened twice. A NuGet cache mount buys back the speed without the
# maintenance hazard: package downloads survive across builds even though this
# layer is invalidated by any source change.
COPY Directory.Build.props ./
COPY services/ services/

# sharing=locked: compose builds these three images in parallel, and an
# unlocked cache mount lets two restores extract the same package at once,
# which corrupts it.
RUN --mount=type=cache,id=incidentiq-nuget,target=/root/.nuget/packages,sharing=locked \
    dotnet publish services/event-processor/IncidentIQ.EventProcessor.csproj \
        --configuration Release \
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

ENTRYPOINT ["dotnet", "IncidentIQ.EventProcessor.dll"]
