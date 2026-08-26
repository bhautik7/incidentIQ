"""Kafka consumer and producer for the analysis worker.

Mirrors the .NET consumer's guarantees deliberately, because the two have to
behave the same way under the same failures:

- Offsets are committed manually, after the work. Auto-commit acknowledges on a
  timer and silently loses in-flight work on a crash.
- A permanent failure is dead-lettered rather than retried, because retrying a
  malformed message burns the partition forever.
- Shutdown finishes the message in hand, commits, and closes the group cleanly.
"""

import asyncio
import json
from collections.abc import Awaitable, Callable

import structlog
from confluent_kafka import Consumer, KafkaError, KafkaException, Producer

from app.config import Settings

logger = structlog.get_logger(__name__)


class PermanentMessageError(Exception):
    """A message that can never succeed: malformed, unknown version, absent tenant."""


class EventProducer:
    def __init__(self, settings: Settings) -> None:
        self._producer = Producer(
            {
                "bootstrap.servers": settings.kafka_bootstrap_servers,
                "client.id": f"{settings.service_name}-producer",
                # Exactly-once into the broker: librdkafka's own retries cannot
                # produce a duplicate or reorder.
                "enable.idempotence": True,
                "acks": "all",
                "compression.type": "lz4",
                "linger.ms": 20,
            }
        )

    def publish(self, topic: str, key: str, payload: str, headers: dict[str, str] | None = None) -> None:
        self._producer.produce(
            topic=topic,
            key=key.encode("utf-8"),
            value=payload.encode("utf-8"),
            headers=[(name, value.encode("utf-8")) for name, value in (headers or {}).items()],
        )
        # Serve delivery callbacks without blocking; flush() at shutdown is what
        # actually guarantees delivery.
        self._producer.poll(0)

    def flush(self, timeout: float = 10.0) -> int:
        return self._producer.flush(timeout)


class EventConsumer:
    """Consumes one topic, handing each message to an async handler."""

    def __init__(
        self,
        settings: Settings,
        *,
        topic: str,
        handler: Callable[[dict, dict[str, str]], Awaitable[None]],
        dead_letter_topic: str | None = None,
        producer: EventProducer | None = None,
    ) -> None:
        self._settings = settings
        self._topic = topic
        self._handler = handler
        self._dead_letter_topic = dead_letter_topic
        self._producer = producer
        self._stopping = asyncio.Event()

        self._consumer = Consumer(
            {
                "bootstrap.servers": settings.kafka_bootstrap_servers,
                "group.id": settings.kafka_consumer_group,
                "client.id": f"{settings.service_name}-consumer",
                # Both off. Auto-commit acknowledges on a timer; auto-store
                # marks a message done before the handler has run.
                "enable.auto.commit": False,
                "enable.auto.offset.store": False,
                "auto.offset.reset": "earliest",
                # Analysis is slow by nature - an embedding plus several
                # queries - so the broker must be told to wait rather than
                # assume the consumer died.
                "max.poll.interval.ms": 600_000,
                "session.timeout.ms": 45_000,
            }
        )

    async def run(self) -> None:
        self._consumer.subscribe([self._topic])
        logger.info(
            "consumer_started",
            topic=self._topic,
            group=self._settings.kafka_consumer_group,
        )

        try:
            while not self._stopping.is_set():
                # poll() blocks, so it runs on a worker thread to keep the
                # event loop free for FastAPI's health endpoints.
                message = await asyncio.to_thread(self._consumer.poll, 0.5)

                if message is None:
                    continue

                if message.error():
                    if message.error().code() == KafkaError._PARTITION_EOF:
                        continue
                    logger.warning("consumer_error", error=str(message.error()))
                    continue

                await self._handle(message)
        finally:
            # Leave the group deliberately rather than waiting out the session
            # timeout, so a replacement picks up these partitions in seconds.
            await asyncio.to_thread(self._consumer.close)
            logger.info("consumer_closed", topic=self._topic)

    async def _handle(self, message) -> None:
        headers = {
            name: value.decode("utf-8") if isinstance(value, bytes) else str(value)
            for name, value in (message.headers() or [])
        }

        try:
            envelope = json.loads(message.value())
        except json.JSONDecodeError as error:
            await self._dead_letter(message, f"Malformed JSON: {error}")
            self._store_and_commit(message)
            return

        try:
            await self._handler(envelope, headers)
        except PermanentMessageError as error:
            logger.error(
                "message_permanently_failed",
                topic=message.topic(),
                partition=message.partition(),
                offset=message.offset(),
                reason=str(error),
            )
            await self._dead_letter(message, str(error))
        except Exception as error:  # noqa: BLE001
            # Transient. Do NOT store the offset: leaving it unstored is what
            # makes Kafka redeliver, which is the retry.
            logger.error(
                "message_failed_will_retry",
                topic=message.topic(),
                partition=message.partition(),
                offset=message.offset(),
                error=str(error),
                exc_info=True,
            )
            return

        self._store_and_commit(message)

    def _store_and_commit(self, message) -> None:
        try:
            self._consumer.store_offsets(message)
            self._consumer.commit(asynchronous=True)
        except KafkaException:
            # Offsets stay stored and the next commit picks them up; worst case
            # a message is reprocessed, which the handler is idempotent about.
            logger.warning("offset_commit_failed", exc_info=True)

    async def _dead_letter(self, message, reason: str) -> None:
        if self._dead_letter_topic is None or self._producer is None:
            logger.critical("no_dead_letter_topic_configured", topic=message.topic())
            return

        original = message.value().decode("utf-8", errors="replace")

        self._producer.publish(
            self._dead_letter_topic,
            key=(message.key() or b"").decode("utf-8") or "unknown",
            payload=json.dumps(
                {
                    "sourceTopic": message.topic(),
                    "sourcePartition": message.partition(),
                    "sourceOffset": message.offset(),
                    "reason": reason,
                    "originalPayload": original,
                }
            ),
        )

    def stop(self) -> None:
        self._stopping.set()
