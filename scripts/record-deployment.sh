#!/usr/bin/env bash
# Tell IncidentIQ that something shipped.
#
# This is the step that makes deployment correlation work. Without a row in
# `deployments`, the rule that opens an incident for a new error just after a
# release can never fire, and the analysis of every incident begins with "no
# deployment correlates". Measured: recording the release moved one incident
# from 20% confidence and "the evidence does not identify a cause" to 35% and a
# named version; a richer one from 40% to 60%.
#
# Meant to be the last line of a deploy job:
#
#   ./scripts/record-deployment.sh --service payments-api --version 2.8.4
#   ./scripts/record-deployment.sh -s payments-api -v "$GIT_TAG" -c "$GIT_SHA" -e staging
#
# Exits non-zero on refusal so a pipeline notices, but a deploy job should
# usually not fail because the notification did - see --soft-fail.
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
ENV_FILE="$REPO_ROOT/infrastructure/docker/.env"

service=""
version=""
environment="production"
commit_sha="${GIT_COMMIT:-${GITHUB_SHA:-}}"
deployed_by="${USER:-ci}"
deployed_at=""
status="Succeeded"
soft_fail=0

usage() {
    sed -n '2,20p' "${BASH_SOURCE[0]}" | sed 's/^# \{0,1\}//'
    cat <<'USAGE'

Options:
  -s, --service      Service that was deployed (required)
  -v, --version      Version or tag that shipped (required)
  -e, --environment  Default: production
  -c, --commit       Commit SHA
  -b, --by           Who or what deployed it. Default: $USER
  -t, --at           ISO-8601 timestamp. Default: now
      --status       Succeeded | Failed | RolledBack | InProgress
      --soft-fail    Warn and exit 0 if the API refuses or is unreachable
USAGE
}

while [ $# -gt 0 ]; do
    case "$1" in
        -s|--service)     service="$2"; shift 2 ;;
        -v|--version)     version="$2"; shift 2 ;;
        -e|--environment) environment="$2"; shift 2 ;;
        -c|--commit)      commit_sha="$2"; shift 2 ;;
        -b|--by)          deployed_by="$2"; shift 2 ;;
        -t|--at)          deployed_at="$2"; shift 2 ;;
        --status)         status="$2"; shift 2 ;;
        --soft-fail)      soft_fail=1; shift ;;
        -h|--help)        usage; exit 0 ;;
        *) echo "Unknown option: $1" >&2; usage >&2; exit 2 ;;
    esac
done

if [ -z "$service" ] || [ -z "$version" ]; then
    echo "Both --service and --version are required." >&2
    usage >&2
    exit 2
fi

# Same resolution order the other scripts use: environment first, then the
# local .env, then the development default.
if [ -f "$ENV_FILE" ]; then
    api_key="${INGESTION_API_KEY:-$(grep -E '^INGESTION_API_KEY=' "$ENV_FILE" | cut -d= -f2- || true)}"
    api_port="${API_HOST_PORT:-$(grep -E '^API_HOST_PORT=' "$ENV_FILE" | cut -d= -f2- || true)}"
else
    api_key="${INGESTION_API_KEY:-}"
    api_port="${API_HOST_PORT:-}"
fi

api_key="${api_key:-iiq_dev_0123456789abcdef}"
api_port="${api_port:-5080}"
url="${INCIDENTIQ_API_URL:-http://localhost:$api_port}/api/v1/deployments"

payload=$(python3 - "$service" "$environment" "$version" "$commit_sha" "$deployed_by" "$deployed_at" "$status" <<'PY'
import json, sys
service, environment, version, commit, by, at, status = sys.argv[1:8]
body = {"service": service, "environment": environment, "version": version, "status": status}
if commit: body["commitSha"] = commit
if by:     body["deployedBy"] = by
if at:     body["deployedAt"] = at
print(json.dumps(body))
PY
)

response=$(curl -sS -w '\n%{http_code}' -X POST "$url" \
    -H 'Content-Type: application/json' -H "X-Api-Key: $api_key" \
    -d "$payload" 2>&1) || {
    echo "Could not reach IncidentIQ at $url" >&2
    [ "$soft_fail" -eq 1 ] && exit 0 || exit 1
}

code=$(printf '%s' "$response" | tail -n1)
body=$(printf '%s' "$response" | sed '$d')

if [ "$code" != "201" ]; then
    echo "IncidentIQ refused the deployment ($code): $body" >&2
    [ "$soft_fail" -eq 1 ] && exit 0 || exit 1
fi

python3 - "$body" <<'PY'
import json, sys
d = json.loads(sys.argv[1])
print(f"Recorded {d['service']} {d['version']} in {d['environment']} at {d['deployedAt']}.")
ids = d.get("correlatedIncidentIds") or []
if ids:
    # Worth saying loudly: the release that just shipped is now the suspect in
    # incidents that are already open.
    print(f"\n  This release is now the suspected cause of {len(ids)} open incident(s):")
    for i in ids:
        print(f"    {i}")
    print("\n  Check them before moving on.")
PY
