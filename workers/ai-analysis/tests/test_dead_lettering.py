"""What happens to a message the worker cannot process.

The analysis queue is ordered per partition, so a message that fails forever
does not fail alone: every incident behind it on that partition goes unexplained
for as long as the retry loop runs. "Retry until it works" is therefore only
correct for failures that eventually work, and the failure that never works and
does not announce itself as permanent - a bug in the handler, a row that cannot
be read, a response the parser rejects - is the common case rather than the rare
one.

So the retry is bounded, and what falls out the bottom goes to the incident
path's own dead-letter topic rather than being mixed in with dead log lines.
"""

import json

import pytest

from app.config import Settings
from app.messaging.kafka import EventConsumer, PermanentMessageError


def settings(**overrides) -> Settings:
    defaults = dict(
        POSTGRES_DSN="postgresql://x/y",
        KAFKA_BOOTSTRAP_SERVERS="localhost:9092",
        ANTHROPIC_API_KEY="sk-test-not-a-real-key",
    )
    return Settings(**{**defaults, **overrides})


class FakeMessage:
    """One Kafka message, at a fixed position in the log."""

    def __init__(self, payload: dict | str = None, *, offset: int = 41):
        self._value = json.dumps(payload if payload is not None else {"eventId": "e1"})
        if isinstance(payload, str):
            self._value = payload
        self._offset = offset

    def value(self):
        return self._value.encode()

    def key(self):
        return b"tenant:incident"

    def headers(self):
        return []

    def topic(self):
        return "incidents.analysis.requested"

    def partition(self):
        return 2

    def offset(self):
        return self._offset


class FakeProducer:
    def __init__(self):
        self.published: list[tuple[str, str, str]] = []

    def publish(self, topic, key, payload, headers=None):
        self.published.append((topic, key, payload))


class RecordingConsumer:
    """Stands in for confluent_kafka's Consumer; records what was committed."""

    def __init__(self):
        self.stored: list[int] = []

    def store_offsets(self, message):
        self.stored.append(message.offset())

    def commit(self, asynchronous=True):
        pass

    def subscribe(self, topics):
        pass


def build(handler, *, max_attempts=3, dead_letter_topic="incidents.failed"):
    producer = FakeProducer()

    consumer = EventConsumer(
        settings(KAFKA_MAX_DELIVERY_ATTEMPTS=max_attempts),
        topic="incidents.analysis.requested",
        handler=handler,
        dead_letter_topic=dead_letter_topic,
        producer=producer,
    )

    # The real Consumer opens sockets in __init__; the loop under test only
    # ever calls store_offsets and commit on it.
    consumer._consumer = RecordingConsumer()

    return consumer, producer


@pytest.mark.asyncio
async def test_a_transient_failure_is_retried_without_committing():
    """Not committing is the retry: Kafka redelivers what was never acked."""
    async def always_fails(envelope, headers):
        raise RuntimeError("database is down")

    consumer, producer = build(always_fails)
    message = FakeMessage()

    await consumer._handle(message)

    assert consumer._consumer.stored == [], "committing here would drop the message"
    assert producer.published == [], "one failure is not yet a dead letter"


@pytest.mark.asyncio
async def test_a_failure_that_never_clears_stops_burning_the_partition():
    """The bound is the whole point.

    Before it, a message like this was redelivered forever and every incident
    behind it waited behind a failure that was never going to resolve.
    """
    async def always_fails(envelope, headers):
        raise RuntimeError("still down")

    consumer, producer = build(always_fails, max_attempts=3)
    message = FakeMessage()

    for _ in range(3):
        await consumer._handle(message)

    assert len(producer.published) == 1, "dead-lettered exactly once"
    assert consumer._consumer.stored == [41], "and committed, so the partition moves on"

    topic, _, payload = producer.published[0]
    assert topic == "incidents.failed"

    body = json.loads(payload)
    assert body["sourceTopic"] == "incidents.analysis.requested"
    assert body["sourcePartition"] == 2
    assert body["sourceOffset"] == 41
    # The reason has to survive: a dead letter nobody can diagnose is a dropped
    # message with extra steps.
    assert "still down" in body["reason"]


@pytest.mark.asyncio
async def test_a_recovered_message_forgets_its_failures():
    """A flaky dependency must not accumulate credit towards a dead letter.

    Two failures then a success is a healthy outcome, and the next unrelated
    failure on that offset should start counting from zero.
    """
    calls = {"n": 0}

    async def fails_twice(envelope, headers):
        calls["n"] += 1
        if calls["n"] <= 2:
            raise RuntimeError("transient")

    consumer, producer = build(fails_twice, max_attempts=3)
    message = FakeMessage()

    await consumer._handle(message)
    await consumer._handle(message)
    await consumer._handle(message)

    assert producer.published == [], "it succeeded on the third go"
    assert consumer._consumer.stored == [41]
    assert consumer._attempts == {}, "the count is cleared, not carried"


@pytest.mark.asyncio
async def test_a_permanent_failure_does_not_wait_for_the_retry_budget():
    """A message that can never succeed should not be tried three times."""
    async def permanently_bad(envelope, headers):
        raise PermanentMessageError("unknown schema version")

    consumer, producer = build(permanently_bad)

    await consumer._handle(FakeMessage())

    assert len(producer.published) == 1
    assert producer.published[0][0] == "incidents.failed"
    assert consumer._consumer.stored == [41]


@pytest.mark.asyncio
async def test_malformed_json_is_dead_lettered_immediately():
    async def never_called(envelope, headers):
        raise AssertionError("the handler must not see unparseable bytes")

    consumer, producer = build(never_called)

    await consumer._handle(FakeMessage("{not json"))

    assert len(producer.published) == 1
    assert "Malformed JSON" in json.loads(producer.published[0][2])["reason"]


@pytest.mark.asyncio
async def test_failures_are_counted_per_message_not_per_partition():
    """Two unrelated messages must not combine to trip the bound.

    Sharing a counter across a partition would dead-letter a perfectly good
    message because a different one failed twice before it.
    """
    async def always_fails(envelope, headers):
        raise RuntimeError("down")

    consumer, producer = build(always_fails, max_attempts=3)

    await consumer._handle(FakeMessage(offset=10))
    await consumer._handle(FakeMessage(offset=11))
    await consumer._handle(FakeMessage(offset=12))

    assert producer.published == [], "three different messages, one failure each"
