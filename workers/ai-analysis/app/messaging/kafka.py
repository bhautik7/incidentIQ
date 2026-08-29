"""Kafka consumer and producer for the analysis worker.

Mirrors the .NET consumer's guarantees deliberately, because the two have to
behave the same way under the same failures:

- Offsets are committed manually, after the work. Auto-commit acknowledges on a
  timer and silently loses in-flight work on a crash.
- A permanent failure is dead-lettered rather than retried, because retrying a
  malformed message burns the partition forever.
- A transient failure is retried by redelivery, but only so many times. An
  unbounded retry is the same partition burn wearing a different name: the
  failure that will never succeed and does not announce itself as permanent is
  the common case, not the rare one.
- Shutdown finishes the message in hand, commits, and closes the group cleanly.
"""

import asyncio
import json
from collections.abc import Awaitable, Callable

import structlog
from confluent_kafka import Consumer, KafkaError, KafkaException, Producer

from app.config import Settings
from app.messaging.liveness import consumer_liveness

logger = structlog.get_logger(__name__)


class PermanentMessageError(Exception):
    """A message that can never succeed: malformed, unknown version, absent tenant."""


class FatalConsumerError(Exception):
    """The Kafka client is beyond recovery and has to be replaced."""


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

        # Redelivery counts, keyed by the message's position in the log.
        #
        # Only failing offsets are ever in here, and an entry is removed as
        # soon as its message succeeds or is dead-lettered, so this holds a
        # handful of entries in the worst case rather than growing with the
        # topic.
        self._attempts: dict[tuple[str, int, int], int] = {}

        #: Set from librdkafka's thread when the client is beyond recovery.
        self._fatal_error = None

        self._consumer = Consumer(
            {
                # Errors librdkafka raises on its own thread, which never reach
                # the poll loop. A consumer that has been evicted says so here
                # and nowhere else.
                "error_cb": self._on_client_error,
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

    def _on_client_error(self, error) -> None:
        """librdkafka's own view of the connection.

        A fatal error is terminal for this client: poll() goes on returning
        nothing forever, so from inside the loop everything looks fine while
        the group has no members. Logged at critical because that is the line
        that explains a consumer which has quietly stopped.
        """
        if error.fatal():
            logger.critical(
                "kafka_client_fatal_error",
                topic=self._topic,
                group=self._settings.kafka_consumer_group,
                code=str(error.code()),
                reason=error.str(),
            )
            self._fatal_error = error
            return

        logger.warning(
            "kafka_client_error",
            topic=self._topic,
            code=str(error.code()),
            reason=error.str(),
        )

    def _on_assign(self, _consumer, partitions) -> None:
        logger.info(
            "partitions_assigned",
            topic=self._topic,
            group=self._settings.kafka_consumer_group,
            partitions=[p.partition for p in partitions],
        )

    def _on_revoke(self, _consumer, partitions) -> None:
        # Recorded with offsets, because a revoke handing back partitions at
        # the offsets they were assigned at is a consumer that did no work
        # between two rebalances - the shape of a poll-interval eviction rather
        # than a deployment.
        logger.warning(
            "partitions_revoked",
            topic=self._topic,
            group=self._settings.kafka_consumer_group,
            partitions=[f"{p.partition}@{p.offset}" for p in partitions],
        )

    async def run(self) -> None:
        self._consumer.subscribe(
            [self._topic], on_assign=self._on_assign, on_revoke=self._on_revoke
        )

        # Registered before the first poll, so a loop that dies immediately
        # still reads as a consumer that stopped rather than one that never was.
        consumer_liveness.register(self._topic, self._settings.kafka_consumer_group)

        logger.info(
            "consumer_started",
            topic=self._topic,
            group=self._settings.kafka_consumer_group,
        )

        try:
            while not self._stopping.is_set():
                # Reported before the poll, not after: poll() blocking for its
                # full timeout is normal, and waiting for it to return would
                # make an idle topic look like a stall.
                consumer_liveness.report_poll(len(self._consumer.assignment()))

                # poll() blocks, so it runs on a worker thread to keep the
                # event loop free for FastAPI's health endpoints.
                if self._fatal_error is not None:
                    # Nothing this loop can do; poll() will return nothing for
                    # as long as it runs. Raised so the supervisor above sees a
                    # reason and rebuilds, rather than spinning on a dead client.
                    raise FatalConsumerError(str(self._fatal_error))

                message = await asyncio.to_thread(self._consumer.poll, 0.5)

                if message is None:
                    continue

                if message.error():
                    if message.error().code() == KafkaError._PARTITION_EOF:
                        continue
                    logger.warning("consumer_error", error=str(message.error()))
                    continue

                try:
                    await self._handle(message)
                except Exception:  # noqa: BLE001
                    # _handle deals with handler failures itself, so reaching
                    # here means the failure was in the machinery around them -
                    # a dead-letter publish onto a full producer queue, a
                    # commit against a client that has gone away. Before this,
                    # such a failure escaped the loop and killed the task, and
                    # the task was created fire-and-forget, so the traceback
                    # went with it. That is why this consumer stopped twice
                    # without anyone learning why.
                    logger.exception(
                        "consumer_loop_error",
                        topic=self._topic,
                        partition=message.partition(),
                        offset=message.offset(),
                    )
        finally:
            # Leave the group deliberately rather than waiting out the session
            # timeout, so a replacement picks up these partitions in seconds.
            await asyncio.to_thread(self._consumer.close)

            # Deregistered so a worker that is deliberately shutting down does
            # not report its own stopped consumer as a fault.
            consumer_liveness.deregister()

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
            self._attempts.pop(
                (message.topic(), message.partition(), message.offset()), None
            )
            await self._dead_letter(message, str(error))
        except Exception as error:  # noqa: BLE001
            position = (message.topic(), message.partition(), message.offset())
            attempt = self._attempts.get(position, 0) + 1
            self._attempts[position] = attempt

            if attempt >= self._settings.kafka_max_delivery_attempts:
                # Treated as permanent now, whatever it claimed to be. A
                # transient failure that has not cleared in this many
                # redeliveries is not going to, and every further retry is one
                # more time this partition delivers nothing else - which on the
                # analysis queue means every incident behind it goes unexplained
                # while the same message fails again.
                logger.error(
                    "message_failed_permanently_after_retries",
                    topic=message.topic(),
                    partition=message.partition(),
                    offset=message.offset(),
                    attempts=attempt,
                    error=str(error),
                    exc_info=True,
                )

                self._attempts.pop(position, None)
                await self._dead_letter(message, f"Failed {attempt} time(s): {error}")
                self._store_and_commit(message)
                return

            # Do NOT store the offset: leaving it unstored is what makes Kafka
            # redeliver, which is the retry.
            logger.error(
                "message_failed_will_retry",
                topic=message.topic(),
                partition=message.partition(),
                offset=message.offset(),
                attempt=attempt,
                max_attempts=self._settings.kafka_max_delivery_attempts,
                error=str(error),
                exc_info=True,
            )
            return

        self._attempts.pop((message.topic(), message.partition(), message.offset()), None)
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
