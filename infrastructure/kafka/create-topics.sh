#!/usr/bin/env bash
#
# Creates the Phase 1 topic set. Idempotent: --if-not-exists means re-running
# the init container after a restart is a no-op.
#
# Partition counts are deliberate, not defaults. Increasing them later rehashes
# keys and breaks per-key ordering for data already in the topic, so the main
# topics start at 3 - enough to scale each consumer group to three instances.
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
    local name="$1" partitions="$2" retention_ms="$3"

    kafka-topics --bootstrap-server "${BOOTSTRAP}" \
        --create --if-not-exists \
        --topic "${name}" \
        --partitions "${partitions}" \
        --replication-factor 1 \
        --config "retention.ms=${retention_ms}" \
        --config "compression.type=lz4"

    echo "  ok: ${name} (${partitions} partition(s), retention ${retention_ms}ms)"
}

# Ingested log events. Keyed by tenant:environment:service so every log line for
# one service lands on one partition, and therefore on one consumer.
create_topic "logs.raw"                3 "${RETENTION_7_DAYS}"

# Incident lifecycle events published by the transactional outbox.
# Keyed by incidentId so a single incident's events stay ordered.
create_topic "incidents.created"       3 "${RETENTION_7_DAYS}"

# Dead-letter topics. Single partition (a healthy system leaves these empty)
# and long retention, because DLQ triage is never same-day work.
create_topic "logs.raw.dlq"            1 "${RETENTION_30_DAYS}"
create_topic "incidents.created.dlq"   1 "${RETENTION_30_DAYS}"

echo
echo "Topics now present:"
kafka-topics --bootstrap-server "${BOOTSTRAP}" --list
