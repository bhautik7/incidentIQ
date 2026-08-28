"""Readiness must catch a consumer that stopped, and stay quiet through the
things that merely look like stopping.

Both directions matter equally. A check that fires during every rebalance gets
disabled, and then the bug it was written for goes back to being invisible.
"""

import pytest

from app.messaging.liveness import ConsumerLiveness

TOPIC = "incidents.analysis.requested"
GROUP = "ai-enricher"


class _Harness:
    """A registry plus the hand-cranked clock it reads; nothing here waits."""

    def __init__(self, registry: ConsumerLiveness, now: dict):
        self.registry = registry
        self._now = now

    def advance(self, seconds: float) -> None:
        self._now["t"] += seconds

    # Delegated so the tests read as if they were driving the registry itself.
    def register(self, topic: str, group: str) -> None:
        self.registry.register(topic, group)

    def report_poll(self, partitions: int) -> None:
        self.registry.report_poll(partitions)

    def deregister(self) -> None:
        self.registry.deregister()

    def fault(self) -> str | None:
        return self.registry.fault()

    def snapshot(self):
        return self.registry.snapshot()

    @property
    def poll_timeout_seconds(self) -> float:
        return self.registry.poll_timeout_seconds

    @property
    def empty_assignment_grace_seconds(self) -> float:
        return self.registry.empty_assignment_grace_seconds


@pytest.fixture
def liveness(monkeypatch):
    now = {"t": 1_000.0}
    monkeypatch.setattr("app.messaging.liveness.time.monotonic", lambda: now["t"])
    return _Harness(ConsumerLiveness(), now)


# ---------------- Healthy shapes ----------------


def test_nothing_registered_is_not_a_fault(liveness):
    # Ordinary during startup. Failing here would flap the container on boot.
    assert liveness.fault() is None
    assert liveness.snapshot() is None


def test_polling_an_idle_topic_is_not_a_fault(liveness):
    # The reason report_poll is called before poll() rather than after: an idle
    # topic returns nothing for hours and is entirely healthy.
    liveness.register(TOPIC, GROUP)

    for _ in range(200):
        liveness.report_poll(3)
        liveness.advance(0.5)

    assert liveness.fault() is None


def test_a_slow_analysis_within_the_poll_interval_is_not_a_fault(liveness):
    # An embedding plus several queries is legitimately slow. Kafka's own limit
    # decides when slow becomes dead, so anything under it must pass.
    liveness.register(TOPIC, GROUP)
    liveness.report_poll(3)

    liveness.advance(liveness.poll_timeout_seconds - 1)

    assert liveness.fault() is None


def test_a_brief_rebalance_is_not_a_fault(liveness):
    liveness.register(TOPIC, GROUP)
    liveness.report_poll(3)

    liveness.report_poll(0)
    liveness.advance(10)
    liveness.report_poll(0)

    assert liveness.fault() is None


def test_deregistering_on_shutdown_is_not_a_fault(liveness):
    liveness.register(TOPIC, GROUP)
    liveness.report_poll(3)

    liveness.deregister()
    liveness.advance(3600)

    assert liveness.fault() is None


# ---------------- The failures this exists for ----------------


def test_a_loop_that_stopped_polling_is_a_fault(liveness):
    liveness.register(TOPIC, GROUP)
    liveness.report_poll(3)

    liveness.advance(liveness.poll_timeout_seconds + 1)

    fault = liveness.fault()
    assert fault is not None
    assert "has not polled" in fault
    assert GROUP in fault


def test_polling_with_no_partitions_is_a_fault(liveness):
    # The observed bug exactly: the loop is alive and polling, the broker is
    # reachable, and the consumer is no longer a member of its group.
    liveness.register(TOPIC, GROUP)
    liveness.report_poll(3)

    liveness.report_poll(0)
    liveness.advance(liveness.empty_assignment_grace_seconds + 1)
    liveness.report_poll(0)

    fault = liveness.fault()
    assert fault is not None
    assert "no partitions" in fault


def test_regaining_partitions_clears_the_fault(liveness):
    liveness.register(TOPIC, GROUP)
    liveness.report_poll(0)
    liveness.advance(liveness.empty_assignment_grace_seconds + 1)
    liveness.report_poll(0)
    assert liveness.fault() is not None

    liveness.report_poll(3)

    assert liveness.fault() is None


def test_the_empty_assignment_clock_measures_the_gap_not_the_last_poll(liveness):
    # A regression guard for the obvious wrong implementation: resetting
    # assignment_changed_at on every poll would mean an assignment that is
    # empty forever never trips the grace period.
    liveness.register(TOPIC, GROUP)
    liveness.report_poll(0)

    for _ in range(30):
        liveness.advance(5)
        liveness.report_poll(0)

    assert liveness.fault() is not None
