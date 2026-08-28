"""Whether this worker's Kafka consumer is actually consuming.

Mirrors ``ConsumerLivenessRegistry`` in the .NET messaging library, and exists
for the same observed failure: this worker's consumer dropped out of its group
after a session timeout and never rejoined. The group had no members, lag sat
unchanged, and ``/health/ready`` stayed green the whole time.

The existing Kafka readiness probe cannot catch that, and never could - it asks
whether a broker answers, and in every observed case the broker was perfectly
fine. Reachability is a property of the cluster; consuming is a property of
this process, and only this process can report it.
"""

from __future__ import annotations

import threading
import time
from dataclasses import dataclass, field, replace

# Kafka's own max.poll.interval.ms for this consumer. Past this the broker
# concludes the consumer is dead and takes its partitions away, so it is the
# honest point at which to stop claiming readiness - sooner would call a slow
# analysis a failure, later would keep claiming health after Kafka had given up.
DEFAULT_POLL_TIMEOUT_SECONDS = 600.0

# A rebalance legitimately holds zero partitions for a few seconds. Anything
# aggressive here would fail readiness on every deployment, and a check that
# cries wolf gets turned off.
DEFAULT_EMPTY_ASSIGNMENT_GRACE_SECONDS = 90.0


@dataclass(slots=True)
class ConsumerState:
    topic: str
    group: str
    last_poll_at: float
    partition_count: int
    assignment_changed_at: float


@dataclass(slots=True)
class ConsumerLiveness:
    """Where the consume loop reports in, and the readiness probe reads back.

    The poll loop runs on a worker thread while FastAPI serves health on the
    event loop, so every access is behind a lock.
    """

    poll_timeout_seconds: float = DEFAULT_POLL_TIMEOUT_SECONDS
    empty_assignment_grace_seconds: float = DEFAULT_EMPTY_ASSIGNMENT_GRACE_SECONDS

    _state: ConsumerState | None = field(default=None, init=False)
    _lock: threading.Lock = field(default_factory=threading.Lock, init=False)

    def register(self, topic: str, group: str) -> None:
        """Called once before the first poll, so a loop that dies immediately
        still reads as a consumer that stopped rather than one that never was."""
        now = time.monotonic()
        with self._lock:
            self._state = ConsumerState(
                topic=topic,
                group=group,
                last_poll_at=now,
                partition_count=0,
                assignment_changed_at=now,
            )

    def report_poll(self, partition_count: int) -> None:
        """Called on every pass of the consume loop, idle or not."""
        now = time.monotonic()
        with self._lock:
            if self._state is None:
                return

            # Moved only when the count actually changes, so the grace period
            # measures how long the assignment has been empty rather than how
            # long ago the last poll was. Resetting it on every poll would mean
            # an assignment that is empty forever never trips.
            if self._state.partition_count != partition_count:
                self._state.assignment_changed_at = now
                self._state.partition_count = partition_count

            self._state.last_poll_at = now

    def deregister(self) -> None:
        """Called on clean shutdown, so a stopping worker is not a fault."""
        with self._lock:
            self._state = None

    def snapshot(self) -> ConsumerState | None:
        """A copy, so a caller reading it cannot see it change mid-read."""
        with self._lock:
            return None if self._state is None else replace(self._state)

    def fault(self) -> str | None:
        """Why the consumer is not doing its job, in words worth reading off a
        failing probe. ``None`` when it is fine."""
        now = time.monotonic()

        with self._lock:
            state = self._state

            if state is None:
                # Nothing registered yet. During startup that is ordinary, and
                # claiming a fault would make the container flap on boot.
                return None

            since_poll = now - state.last_poll_at

            if since_poll > self.poll_timeout_seconds:
                return (
                    f"{state.group} has not polled {state.topic} for {since_poll:.0f}s "
                    f"(limit {self.poll_timeout_seconds:.0f}s)."
                )

            # Reported separately from a stalled loop: a consumer polling
            # briskly while holding nothing has fallen out of its group, which
            # looks completely different in the logs.
            since_change = now - state.assignment_changed_at

            if state.partition_count == 0 and since_change > self.empty_assignment_grace_seconds:
                return (
                    f"{state.group} has held no partitions of {state.topic} for "
                    f"{since_change:.0f}s; it is polling but not a member of its group."
                )

            return None


# One consumer per process, so a module-level instance is the whole registry.
consumer_liveness = ConsumerLiveness()
