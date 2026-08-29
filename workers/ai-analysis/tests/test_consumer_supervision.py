"""A consumer that dies must say so, and then come back.

This covers the failure the project had twice and never explained: the group
showed no active members, lag stopped moving, the process went on answering
health checks, and nothing in the logs said why.

The reason nothing said why is here rather than in Kafka. The consume loop ran
as a task nobody awaited, so an exception escaping it completed the task with a
result nobody retrieved - and an unretrieved exception is reported by the
interpreter at some arbitrary later collection, if at all, without saying which
consumer it belonged to. The evidence was being discarded by design.
"""

import asyncio

import pytest

from app.config import Settings
from app.messaging.kafka import FatalConsumerError


def settings(**overrides) -> Settings:
    defaults = dict(
        POSTGRES_DSN="postgresql://x/y",
        KAFKA_BOOTSTRAP_SERVERS="localhost:9092",
        ANTHROPIC_API_KEY="sk-test-not-a-real-key",
    )
    return Settings(**{**defaults, **overrides})


class FakeConsumer:
    """A consume loop that fails a set number of times, then blocks."""

    def __init__(self, failures: int, error: Exception):
        self.failures = failures
        self.error = error
        self.runs = 0
        self.stopped = False

    async def run(self):
        self.runs += 1

        if self.runs <= self.failures:
            raise self.error

        # Survived: wait to be stopped, like a healthy loop polling.
        while not self.stopped:
            await asyncio.sleep(0.01)

    def stop(self):
        self.stopped = True


class Worker:
    """The supervision logic from AnalysisWorker.run, with the pipeline removed.

    Exercised in isolation because the real worker's constructor needs a
    database, an embedding model and an Anthropic client, none of which this
    behaviour touches.
    """

    def __init__(self, consumers: list[FakeConsumer], delays=None):
        self._queue = list(consumers)
        self._consumer = self._queue.pop(0)
        self._stopping = False
        self._delays = delays or [0, 0, 0, 0]
        self.restarts = 0
        self.crashes: list[Exception] = []

    def _build_consumer(self):
        return self._queue.pop(0) if self._queue else self._consumer

    async def run(self):
        restarts = 0

        while not self._stopping:
            try:
                await self._consumer.run()
                return
            except asyncio.CancelledError:
                raise
            except Exception as error:  # noqa: BLE001
                if self._stopping:
                    return

                self.crashes.append(error)
                delay = self._delays[min(restarts, len(self._delays) - 1)]
                restarts += 1
                self.restarts = restarts
                self._consumer = self._build_consumer()
                await asyncio.sleep(delay)

    def stop(self):
        self._stopping = True
        self._consumer.stop()


@pytest.mark.asyncio
async def test_a_crashed_loop_is_replaced_not_abandoned():
    """The old behaviour was to stop consuming, permanently and silently."""
    crashing = FakeConsumer(failures=1, error=RuntimeError("producer queue full"))
    healthy = FakeConsumer(failures=0, error=RuntimeError("unused"))

    worker = Worker([crashing, healthy])
    task = asyncio.create_task(worker.run())
    await asyncio.sleep(0.05)

    assert worker.restarts == 1
    assert healthy.runs == 1, "a fresh consumer took over"

    worker.stop()
    await asyncio.wait_for(task, timeout=1)


@pytest.mark.asyncio
async def test_the_client_is_rebuilt_rather_than_reused():
    """A fatally failed librdkafka client never recovers.

    Restarting the loop around the same client would spin on a corpse: poll()
    returns nothing forever while the group has no members, which is precisely
    what "no active members, lag unchanged, health green" looked like.
    """
    dead = FakeConsumer(failures=1, error=FatalConsumerError("Local: Broker transport failure"))
    replacement = FakeConsumer(failures=0, error=RuntimeError("unused"))

    worker = Worker([dead, replacement])
    task = asyncio.create_task(worker.run())
    await asyncio.sleep(0.05)

    assert replacement.runs == 1
    assert worker._consumer is replacement, "the failed client was discarded"

    worker.stop()
    await asyncio.wait_for(task, timeout=1)


@pytest.mark.asyncio
async def test_every_crash_is_captured_rather_than_swallowed():
    """Three crashes, three recorded reasons.

    The next occurrence has to leave evidence. A restart that hides why it
    restarted converts a diagnosable fault into a mystery that repeats.
    """
    consumers = [
        FakeConsumer(failures=1, error=RuntimeError(f"crash {i}")) for i in range(3)
    ] + [FakeConsumer(failures=0, error=RuntimeError("unused"))]

    worker = Worker(consumers)
    task = asyncio.create_task(worker.run())
    await asyncio.sleep(0.1)

    assert [str(e) for e in worker.crashes] == ["crash 0", "crash 1", "crash 2"]

    worker.stop()
    await asyncio.wait_for(task, timeout=1)


@pytest.mark.asyncio
async def test_stopping_does_not_restart():
    """Shutdown is not a crash. Restarting through it would hang the container."""
    consumer = FakeConsumer(failures=0, error=RuntimeError("unused"))
    worker = Worker([consumer])

    task = asyncio.create_task(worker.run())
    await asyncio.sleep(0.02)

    worker.stop()
    await asyncio.wait_for(task, timeout=1)

    assert worker.restarts == 0
    assert consumer.runs == 1


@pytest.mark.asyncio
async def test_a_dying_task_reports_its_own_exception():
    """The done-callback is what turns a silent death into a log line.

    Without it the exception sits on a task nobody awaits, and the process
    keeps serving health checks as though nothing happened.
    """
    reported: list[BaseException] = []

    def report(task: asyncio.Task) -> None:
        if task.cancelled():
            return
        error = task.exception()
        if error is not None:
            reported.append(error)

    async def dies():
        raise RuntimeError("assignment lost")

    task = asyncio.create_task(dies())
    task.add_done_callback(report)

    await asyncio.sleep(0.01)

    assert [str(e) for e in reported] == ["assignment lost"]
