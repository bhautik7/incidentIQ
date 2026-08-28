using IncidentIQ.Messaging;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace IncidentIQ.Messaging.Tests;

/// <summary>
/// The registry decides whether a container is taken out of rotation, so both
/// directions matter: it has to catch a consumer that has stopped, and it has
/// to stay quiet through the things that merely look like stopping - an idle
/// topic, a slow batch, a rebalance, a deliberate shutdown.
///
/// A check that cries wolf during every deployment gets disabled, and then the
/// bug it was written for goes back to being invisible.
/// </summary>
public sealed class ConsumerLivenessTests
{
    private const string Topic = "logs.normalized";
    private const string Group = "incident-detector";

    /// <summary>A clock the test moves by hand; nothing here should need real waiting.</summary>
    private sealed class TestClock(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now = _now.Add(by);
    }

    private static (ConsumerLivenessRegistry Registry, TestClock Clock) Build()
    {
        var clock = new TestClock(new DateTimeOffset(2026, 8, 28, 12, 0, 0, TimeSpan.Zero));
        return (new ConsumerLivenessRegistry(clock), clock);
    }

    // ---------------- Healthy shapes ----------------

    [Fact]
    public async Task A_process_with_no_consumers_is_healthy()
    {
        // The API registers none. Reporting a fault there would make a service
        // that is working perfectly refuse traffic.
        var (registry, _) = Build();

        var result = await new KafkaConsumerHealthCheck(registry)
            .CheckHealthAsync(new HealthCheckContext());

        Assert.Equal(HealthStatus.Healthy, result.Status);
    }

    [Fact]
    public void A_consumer_polling_an_idle_topic_is_not_a_fault()
    {
        // The whole point of reporting before the poll rather than after: an
        // idle topic returns nothing for hours and is entirely healthy.
        var (registry, clock) = Build();
        registry.Register(Topic, Group);

        for (var i = 0; i < 200; i++)
        {
            registry.ReportPoll(Topic, Group, partitionCount: 3);
            clock.Advance(TimeSpan.FromMilliseconds(500));
        }

        Assert.Empty(registry.Faults());
    }

    [Fact]
    public void A_slow_batch_within_the_poll_interval_is_not_a_fault()
    {
        // A handler is allowed to take a long time. Kafka's own limit is what
        // decides when that becomes death, so anything under it must pass.
        var (registry, clock) = Build();
        registry.Register(Topic, Group);
        registry.ReportPoll(Topic, Group, partitionCount: 3);

        clock.Advance(registry.PollTimeout - TimeSpan.FromSeconds(1));

        Assert.Empty(registry.Faults());
    }

    [Fact]
    public void A_brief_rebalance_is_not_a_fault()
    {
        // Partitions are revoked for a few seconds on every deployment. If that
        // failed readiness, a rolling restart would never converge.
        var (registry, clock) = Build();
        registry.Register(Topic, Group);
        registry.ReportPoll(Topic, Group, partitionCount: 3);

        registry.ReportPoll(Topic, Group, partitionCount: 0);
        clock.Advance(TimeSpan.FromSeconds(10));
        registry.ReportPoll(Topic, Group, partitionCount: 0);

        Assert.Empty(registry.Faults());
    }

    [Fact]
    public void A_consumer_that_deregistered_on_shutdown_is_not_a_fault()
    {
        var (registry, clock) = Build();
        registry.Register(Topic, Group);
        registry.ReportPoll(Topic, Group, partitionCount: 3);

        registry.Deregister(Topic, Group);
        clock.Advance(TimeSpan.FromHours(1));

        Assert.Empty(registry.Faults());
        Assert.Empty(registry.Snapshot());
    }

    // ---------------- The failures this exists for ----------------

    [Fact]
    public async Task A_loop_that_stopped_polling_fails_readiness()
    {
        var (registry, clock) = Build();
        registry.Register(Topic, Group);
        registry.ReportPoll(Topic, Group, partitionCount: 3);

        clock.Advance(registry.PollTimeout + TimeSpan.FromSeconds(1));

        var fault = Assert.Single(registry.Faults());
        Assert.Contains("has not polled", fault);
        Assert.Contains(Group, fault);

        var result = await new KafkaConsumerHealthCheck(registry)
            .CheckHealthAsync(new HealthCheckContext());

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
    }

    [Fact]
    public async Task A_consumer_polling_with_no_partitions_fails_readiness()
    {
        // This is the observed bug, exactly: the loop is alive and polling
        // briskly, the broker is reachable, and the consumer is no longer a
        // member of its group. Every other probe stays green.
        var (registry, clock) = Build();
        registry.Register(Topic, Group);
        registry.ReportPoll(Topic, Group, partitionCount: 3);

        registry.ReportPoll(Topic, Group, partitionCount: 0);
        clock.Advance(registry.EmptyAssignmentGrace + TimeSpan.FromSeconds(1));
        registry.ReportPoll(Topic, Group, partitionCount: 0);

        var fault = Assert.Single(registry.Faults());
        Assert.Contains("no partitions", fault);

        var result = await new KafkaConsumerHealthCheck(registry)
            .CheckHealthAsync(new HealthCheckContext());

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
    }

    [Fact]
    public void Regaining_partitions_clears_the_fault()
    {
        var (registry, clock) = Build();
        registry.Register(Topic, Group);

        registry.ReportPoll(Topic, Group, partitionCount: 0);
        clock.Advance(registry.EmptyAssignmentGrace + TimeSpan.FromSeconds(1));
        registry.ReportPoll(Topic, Group, partitionCount: 0);
        Assert.NotEmpty(registry.Faults());

        registry.ReportPoll(Topic, Group, partitionCount: 3);

        Assert.Empty(registry.Faults());
    }

    [Fact]
    public void One_dead_consumer_is_reported_even_when_its_neighbour_is_fine()
    {
        // The sharpest form of the observed failure: two consumers in the same
        // container, one working and one not. A per-process check that only
        // asked "is anything consuming" would have called this healthy.
        var (registry, clock) = Build();

        registry.Register("logs.raw", "incident-processor");
        registry.Register(Topic, Group);

        registry.ReportPoll("logs.raw", "incident-processor", partitionCount: 3);
        registry.ReportPoll(Topic, Group, partitionCount: 3);

        // The detector stops; the processor keeps going.
        for (var i = 0; i < 20; i++)
        {
            clock.Advance(TimeSpan.FromSeconds(30));
            registry.ReportPoll("logs.raw", "incident-processor", partitionCount: 3);
        }

        var fault = Assert.Single(registry.Faults());
        Assert.Contains(Group, fault);
        Assert.DoesNotContain("incident-processor", fault);
    }

    [Fact]
    public void The_poll_timeout_widens_to_the_most_patient_consumer()
    {
        // One registry serves every consumer in the process and they need not
        // agree. Narrowing to the strictest would fail a consumer that Kafka
        // itself is still happily waiting for.
        var (registry, _) = Build();

        registry.AllowPollInterval(TimeSpan.FromMinutes(10));
        registry.AllowPollInterval(TimeSpan.FromMinutes(2));

        Assert.Equal(TimeSpan.FromMinutes(10), registry.PollTimeout);
    }

    [Fact]
    public void The_empty_assignment_clock_measures_the_gap_not_the_last_poll()
    {
        // A regression guard for the obvious wrong implementation: resetting
        // AssignmentChangedAt on every poll would mean an assignment that is
        // empty forever never trips the grace period.
        var (registry, clock) = Build();
        registry.Register(Topic, Group);
        registry.ReportPoll(Topic, Group, partitionCount: 0);

        for (var i = 0; i < 30; i++)
        {
            clock.Advance(TimeSpan.FromSeconds(5));
            registry.ReportPoll(Topic, Group, partitionCount: 0);
        }

        Assert.NotEmpty(registry.Faults());
    }
}
