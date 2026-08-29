#!/usr/bin/env bash
#
# Shows what the pipeline made of the logs it has seen: patterns, incidents,
# and the AI analysis.
#
#   ./scripts/show-analysis.sh           # everything
#   ./scripts/show-analysis.sh --watch   # refresh until an incident appears
#   ./scripts/show-analysis.sh --reset   # clear all pipeline data and start over
set -euo pipefail

cd "$(dirname "$0")/.."

COMPOSE="docker compose -f infrastructure/docker/docker-compose.yml"
ENV_FILE="infrastructure/docker/.env"
[[ -f "$ENV_FILE" ]] && { set -a; . "$ENV_FILE"; set +a; }

psql_do() {
    $COMPOSE exec -T postgres psql -U "${POSTGRES_USER}" -d "${POSTGRES_DB}" "$@"
}

reset_pipeline() {
    # Leaves organizations, services, environments and deployments in place -
    # only the data the pipeline produces is cleared.
    # raw_log_events belongs in this list. Left out, a reset clears the
    # patterns while keeping every raw line that produced them, so the log
    # explorer goes on showing lines whose pattern no longer exists and whose
    # fingerprint filter matches nothing.
    psql_do -q -c "TRUNCATE log_events, raw_log_events, log_patterns, log_pattern_metrics,
                            processed_events, incidents, incident_events,
                            outbox_messages, ai_analyses RESTART IDENTITY CASCADE;" >/dev/null 2>&1
    echo "Pipeline data cleared. Services, environments and deployments kept."
}

show() {
    echo "════════════════════════════════════════════════════════════════"
    echo " PATTERNS — what the raw lines collapsed into"
    echo "════════════════════════════════════════════════════════════════"
    psql_do -q -c "
        SELECT s.key AS service,
               left(p.fingerprint, 10) AS fingerprint,
               p.occurrence_count AS occurrences,
               left(p.message_template, 68) AS normalized_template
        FROM log_patterns p
        JOIN monitored_services s ON s.id = p.monitored_service_id
        ORDER BY p.occurrence_count DESC;"

    echo "════════════════════════════════════════════════════════════════"
    echo " INCIDENTS — patterns that crossed a detection rule"
    echo "════════════════════════════════════════════════════════════════"
    psql_do -q -c "
        SELECT i.status, i.severity, i.detection_rule AS opened_by,
               i.occurrence_count AS occurrences,
               d.version AS suspect_release,
               left(i.title, 50) AS title
        FROM incidents i
        LEFT JOIN deployments d ON d.id = i.suspected_deployment_id
        ORDER BY i.created_at DESC;"

    local analyses
    analyses=$(psql_do -t -A -c "SELECT count(*) FROM ai_analyses;" 2>/dev/null | tr -d '[:space:]')

    if [[ "${analyses:-0}" == "0" ]]; then
        echo "No analysis yet. It normally lands a few seconds after the incident."
        return
    fi

    echo "════════════════════════════════════════════════════════════════"
    echo " ANALYSIS — evidence assembled, then explained"
    echo "════════════════════════════════════════════════════════════════"
    psql_do -t -A -F'|' -c "
        SELECT a.model_provider, a.model_name, a.confidence, a.latency_ms,
               a.summary, coalesce(a.probable_cause, ''), coalesce(a.suggested_actions::text, '[]')
        FROM ai_analyses a
        ORDER BY a.created_at DESC;" | python3 -c "
import json, sys, textwrap

def wrap(text, indent='    '):
    return '\n'.join(textwrap.fill(text, 74, initial_indent=indent,
                                   subsequent_indent=indent) for text in text.split('\n'))

for line in sys.stdin:
    line = line.rstrip('\n')
    if not line.strip():
        continue
    provider, model, confidence, latency, summary, cause, actions = line.split('|', 6)

    written_by = 'model-written' if provider == 'anthropic' else 'template (no LLM)'
    print(f'  {written_by}: {model}   confidence {float(confidence):.2f}   {latency}ms')
    print()
    print('  SUMMARY'); print(wrap(summary)); print()
    if cause:
        print('  PROBABLE CAUSE'); print(wrap(cause)); print()
    try:
        parsed = json.loads(actions)
    except Exception:
        parsed = []
    if parsed:
        print('  SUGGESTED ACTIONS')
        for n, action in enumerate(parsed, 1):
            print(wrap(action, indent='      ').replace('      ', f'   {n}. ', 1))
    print()
"
}

case "${1:-}" in
    --reset)
        reset_pipeline
        ;;
    --watch)
        # Poll until an analysis appears, so there is no guessing about when
        # the asynchronous pipeline has caught up.
        for _ in $(seq 1 40); do
            count=$(psql_do -t -A -c "SELECT count(*) FROM ai_analyses;" 2>/dev/null | tr -d '[:space:]')
            [[ "${count:-0}" != "0" ]] && break
            printf '.'
            sleep 2
        done
        echo
        show
        ;;
    --help|-h)
        sed -n '2,10p' "$0" | sed 's/^# \{0,1\}//'
        ;;
    *)
        show
        ;;
esac
