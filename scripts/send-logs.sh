#!/usr/bin/env bash
#
# Sends synthetic log events through the real ingestion endpoint.
#
# This is the front door of the pipeline - the same endpoint a real agent would
# POST to - so anything sent here exercises every stage: validation, Kafka,
# normalisation, fingerprinting, detection, and analysis.
#
#   ./scripts/send-logs.sh --count 60 --message "Connection timeout for user {i}"
#   ./scripts/send-logs.sh --count 5 --severity Warning --service orders-api
#   ./scripts/send-logs.sh --help
set -euo pipefail

cd "$(dirname "$0")/.."

ENV_FILE="infrastructure/docker/.env"
[[ -f "$ENV_FILE" ]] && { set -a; . "$ENV_FILE"; set +a; }

API_KEY="${INGESTION_API_KEY:-iiq_dev_0123456789abcdef}"
PORT="${INGESTION_HOST_PORT:-5081}"

# Defaults produce a burst that crosses the detection threshold (25 in 5 min).
COUNT=60
MESSAGE="Connection timeout for user {i}"
SERVICE="payments-api"
ENVIRONMENT="production"
SEVERITY="Error"
EXCEPTION="System.TimeoutException"
STATUS=""
SPREAD=120
QUIET=false

usage() {
    cat <<'USAGE'
Send synthetic log events to IncidentIQ.

Options:
  --count N          How many events (default 60). 25+ crosses the detection threshold.
  --message TEXT     Message template. "{i}" is replaced with the event index,
                     which is what makes each line distinct but the *pattern* shared.
  --service NAME     Service name (default payments-api)
  --environment NAME Environment (default production)
  --severity LEVEL   Trace|Debug|Information|Warning|Error|Fatal (default Error)
  --exception TYPE   Exception type, or "" for none (default System.TimeoutException)
  --status CODE      HTTP status to attach, e.g. 503. Needed for the 5xx-spike rule.
  --spread SECONDS   Spread events over this many seconds (default 120).
                     Keep under 300 or events fall outside the detection window.
  --quiet            Print only the response
  --help             This message

Examples:
  # A burst that opens an incident
  ./scripts/send-logs.sh --count 60

  # Below the threshold: counted, but no incident
  ./scripts/send-logs.sh --count 10

  # A different failure, so a second pattern appears
  ./scripts/send-logs.sh --count 40 --service orders-api \
      --message "Downstream call failed for order {i}" \
      --exception System.Net.Http.HttpRequestException --status 502

  # Prove masking: every line differs, one pattern results
  ./scripts/send-logs.sh --count 30 \
      --message "Payment {i} failed for user{i}@acme.com from 10.0.0.{i} in 45ms"
USAGE
}

while [[ $# -gt 0 ]]; do
    case "$1" in
        --count)       COUNT="$2"; shift 2 ;;
        --message)     MESSAGE="$2"; shift 2 ;;
        --service)     SERVICE="$2"; shift 2 ;;
        --environment) ENVIRONMENT="$2"; shift 2 ;;
        --severity)    SEVERITY="$2"; shift 2 ;;
        --exception)   EXCEPTION="$2"; shift 2 ;;
        --status)      STATUS="$2"; shift 2 ;;
        --spread)      SPREAD="$2"; shift 2 ;;
        --quiet)       QUIET=true; shift ;;
        --help|-h)     usage; exit 0 ;;
        *) echo "Unknown option: $1" >&2; usage; exit 1 ;;
    esac
done

PAYLOAD=$(
    COUNT="$COUNT" MESSAGE="$MESSAGE" SERVICE="$SERVICE" ENVIRONMENT="$ENVIRONMENT" \
    SEVERITY="$SEVERITY" EXCEPTION="$EXCEPTION" STATUS="$STATUS" SPREAD="$SPREAD" \
    python3 - <<'PY'
import datetime, json, os, uuid

count = int(os.environ["COUNT"])
spread = max(int(os.environ["SPREAD"]), 1)
now = datetime.datetime.now(datetime.timezone.utc)

metadata_base = {"generatedBy": "send-logs.sh"}
if os.environ["STATUS"]:
    metadata_base["statusCode"] = os.environ["STATUS"]

events = []
for i in range(count):
    # Spread backwards over the window so the whole burst lands inside the
    # detector's five-minute view.
    offset = (i / max(count - 1, 1)) * spread
    event = {
        # A client-generated id is what makes an HTTP retry idempotent rather
        # than duplicative.
        "eventId": str(uuid.uuid4()),
        "service": os.environ["SERVICE"],
        "environment": os.environ["ENVIRONMENT"],
        "timestamp": (now - datetime.timedelta(seconds=offset)).isoformat(),
        "severity": os.environ["SEVERITY"],
        "message": os.environ["MESSAGE"].replace("{i}", str(1000 + i)),
        "metadata": dict(metadata_base),
    }
    if os.environ["EXCEPTION"]:
        event["exceptionType"] = os.environ["EXCEPTION"]
        event["stackTrace"] = (
            "at Payments.Charge(Order o) in /src/Payments.cs:line 42\nat Api.Post()"
        )
    events.append(event)

print(json.dumps({"events": events}))
PY
)

if [[ "$QUIET" == false ]]; then
    echo "Sending ${COUNT} × \"${MESSAGE}\""
    echo "  service=${SERVICE} environment=${ENVIRONMENT} severity=${SEVERITY}"
    echo
fi

curl -s -X POST "http://localhost:${PORT}/api/v1/logs/batch" \
    -H "Content-Type: application/json" \
    -H "X-Api-Key: ${API_KEY}" \
    --data-binary "$PAYLOAD"

echo
[[ "$QUIET" == false ]] && echo && echo "Now run: ./scripts/show-analysis.sh" || true
