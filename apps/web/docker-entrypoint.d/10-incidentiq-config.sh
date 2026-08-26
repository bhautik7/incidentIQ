#!/bin/sh
# Regenerates /config.js from environment variables at container start.
# This is what lets one immutable image be promoted from dev to production:
# the bundle is built once, the endpoints are injected per environment.
set -eu

CONFIG_FILE=/usr/share/nginx/html/config.js

cat > "$CONFIG_FILE" <<JS
window.__INCIDENTIQ_CONFIG__ = {
  apiBaseUrl: "${WEB_API_BASE_URL:-http://localhost:5080}",
  apiKey: "${WEB_API_KEY:-}",
  ingestionBaseUrl: "${WEB_INGESTION_BASE_URL:-http://localhost:5081}",
  eventProcessorBaseUrl: "${WEB_EVENT_PROCESSOR_BASE_URL:-http://localhost:5082}",
  aiAnalysisBaseUrl: "${WEB_AI_ANALYSIS_BASE_URL:-http://localhost:5083}",
};
JS

echo "incidentiq: wrote runtime config to $CONFIG_FILE"
