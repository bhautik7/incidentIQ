#!/usr/bin/env bash
#
# Creates the IncidentIQ topic set. Idempotent: --if-not-exists means re-running
# the init container after a restart is a no-op.
#
# Partition counts are deliberate, not defaults. Raising the count later
# rehashes keys and breaks per-key ordering for data already in the topic, so
# topics that will ever need parallel consumers start at 3.
set -euo pipefail

BOOTSTRAP="${KAFKA_BOOTSTRAP_SERVERS:-kafka:9092}"

RETENTION_7_DAYS=604800000
RETENTION_30_DAYS=2592000000

echo "Waiting for Kafka at ${BOOTSTRAP}..."
until kafka-broker-api-versions --bootstrap-server "${BOOTSTRAP}" >/dev/null 2>&1; do
    sleep 2
done
echo "Kafka is up."

create_topic () {
    local name="$1" partitions="$2" retention_ms="$3" purpose="$4"

    kafka-topics --bootstrap-server "${BOOTSTRAP}" \
        --create --if-not-exists \
        --topic "${name}" \
        --partitions "${partitions}" \
        --replication-factor 1 \
        --config "retention.ms=${retention_ms}" \
        --config "compression.type=lz4" \
        >/dev/null

    printf '  %-32s %s partition(s)  %s\n' "${name}" "${partitions}" "${purpose}"
}

echo
echo "Log pipeline  (key: tenantId:service)"
# 3 partitions: the ceiling on how many processor replicas can share the work.
create_topic "logs.raw"                     3 "${RETENTION_7_DAYS}"  "raw events as accepted from clients"
create_topic "logs.normalized"              3 "${RETENTION_7_DAYS}"  "masked and fingerprinted"
# Single partition and long retention: in a healthy system this is empty, and
# nobody triages a dead-letter queue the same day.
create_topic "logs.failed"                  1 "${RETENTION_30_DAYS}" "dead letters; never auto-replayed"

echo
echo "Deployments  (key: tenantId:service)"
# One partition is ample - a busy organization deploys tens of times a day, not
# thousands. Longer retention because deployment history stays useful.
create_topic "deployments.created"          1 "${RETENTION_30_DAYS}" "releases, for incident correlation"

echo
echo "Incident pipeline  (key: tenantId:incidentId)"
create_topic "incidents.detected"           3 "${RETENTION_7_DAYS}"  "published via the transactional outbox"
create_topic "incidents.analysis.requested" 3 "${RETENTION_7_DAYS}"  "work queue for the Python AI worker"
create_topic "incidents.analysis.completed" 3 "${RETENTION_7_DAYS}"  "analysis results announced"

echo
echo "Topics now present:"
kafka-topics --bootstrap-server "${BOOTSTRAP}" --list | sed 's/^/  /'
