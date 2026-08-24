#!/usr/bin/env bash
#
# Probes every published endpoint in the local stack and prints a summary.
# Exits non-zero if anything is not reachable or not healthy.
set -uo pipefail

ENV_FILE="$(dirname "$0")/../infrastructure/docker/.env"
if [[ -f "$ENV_FILE" ]]; then
    set -a; . "$ENV_FILE"; set +a
fi

API_HOST_PORT=${API_HOST_PORT:-5080}
INGESTION_HOST_PORT=${INGESTION_HOST_PORT:-5081}
EVENT_PROCESSOR_HOST_PORT=${EVENT_PROCESSOR_HOST_PORT:-5082}
AI_ANALYSIS_HOST_PORT=${AI_ANALYSIS_HOST_PORT:-5083}
WEB_HOST_PORT=${WEB_HOST_PORT:-3000}
PROMETHEUS_HOST_PORT=${PROMETHEUS_HOST_PORT:-9090}
GRAFANA_HOST_PORT=${GRAFANA_HOST_PORT:-3001}
MINIO_API_HOST_PORT=${MINIO_API_HOST_PORT:-9000}

failures=0

probe () {
    local label="$1" url="$2"
    local status
    status=$(curl --silent --max-time 5 --output /dev/null --write-out '%{http_code}' "$url" 2>/dev/null)

    if [[ "$status" == "200" ]]; then
        printf '  %-34s %-6s %s\n' "$label" "OK" "$url"
    else
        printf '  %-34s %-6s %s  (HTTP %s)\n' "$label" "FAIL" "$url" "${status:-000}"
        failures=$((failures + 1))
    fi
}

echo "Application services"
probe "api /health/live"             "http://localhost:${API_HOST_PORT}/health/live"
probe "api /health/ready"            "http://localhost:${API_HOST_PORT}/health/ready"
probe "ingestion /health/live"       "http://localhost:${INGESTION_HOST_PORT}/health/live"
probe "ingestion /health/ready"      "http://localhost:${INGESTION_HOST_PORT}/health/ready"
probe "event-processor /health/live"  "http://localhost:${EVENT_PROCESSOR_HOST_PORT}/health/live"
probe "event-processor /health/ready" "http://localhost:${EVENT_PROCESSOR_HOST_PORT}/health/ready"
probe "ai-analysis /health/live"     "http://localhost:${AI_ANALYSIS_HOST_PORT}/health/live"
probe "ai-analysis /health/ready"    "http://localhost:${AI_ANALYSIS_HOST_PORT}/health/ready"
probe "web /healthz"                 "http://localhost:${WEB_HOST_PORT}/healthz"

echo
echo "Metrics endpoints"
probe "api /metrics"                 "http://localhost:${API_HOST_PORT}/metrics"
probe "ingestion /metrics"           "http://localhost:${INGESTION_HOST_PORT}/metrics"
probe "event-processor /metrics"     "http://localhost:${EVENT_PROCESSOR_HOST_PORT}/metrics"
probe "ai-analysis /metrics"         "http://localhost:${AI_ANALYSIS_HOST_PORT}/metrics"

echo
echo "Infrastructure"
probe "prometheus"                   "http://localhost:${PROMETHEUS_HOST_PORT}/-/healthy"
probe "grafana"                      "http://localhost:${GRAFANA_HOST_PORT}/api/health"
probe "minio"                        "http://localhost:${MINIO_API_HOST_PORT}/minio/health/live"

echo
if (( failures == 0 )); then
    echo "All endpoints healthy."
else
    echo "${failures} endpoint(s) failed."
fi
exit $(( failures > 0 ))
