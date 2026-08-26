using IncidentIQ.Domain.Enums;
using IncidentIQ.EventProcessor.Detection;

namespace IncidentIQ.EventProcessor.Tests;

/// <summary>
/// The rules in isolation. They take counts and return a verdict, with no I/O,
/// so every boundary can be pinned without a database - which is the reason
/// they were written that way.
/// </summary>
public class DetectionRuleTests
{
    private static readonly DetectionOptions Options = new();

    private static DetectionInput Input(
        long windowCount = 0,
        long baselineCount = 0,
        long serverErrors = 0,
        bool isNew = false,
        TimeSpan? sinceDeployment = null) => new()
    {
        WindowCount = windowCount,
        BaselineCount = baselineCount,
        BaselineDuration = TimeSpan.FromMinutes(Options.BaselineMinutes),
        ServerErrorWindowCount = serverErrors,
        IsNewPattern = isNew,
        TimeSinceDeployment = sinceDeployment
    };

    // ---- Rule 1: absolute count ----

    [Fact]
    public void Does_not_open_below_the_count_threshold()
    {
        var verdict = DetectionRuleEngine.Evaluate(Input(windowCount: Options.CountThreshold - 1), Options);

        Assert.False(verdict.ShouldOpen);
    }

    [Fact]
    public void Opens_exactly_at_the_count_threshold()
    {
        // The boundary is the interesting case: off by one here is the
        // difference between paging on 25 and paging on 26.
        var verdict = DetectionRuleEngine.Evaluate(Input(windowCount: Options.CountThreshold), Options);

        Assert.True(verdict.ShouldOpen);
        Assert.Equal(DetectionRule.CountThreshold, verdict.Rule);
    }

    [Fact]
    public void Explains_itself_in_terms_a_human_can_argue_with()
    {
        var verdict = DetectionRuleEngine.Evaluate(Input(windowCount: 412), Options);

        // The reason has to name the observation and the threshold, or nobody
        // woken at 03:00 can tell whether the rule was right.
        Assert.Contains("412", verdict.Reason);
        Assert.Contains(Options.CountThreshold.ToString(), verdict.Reason);
    }

    [Theory]
    [InlineData(25, IncidentSeverity.Medium)]
    [InlineData(125, IncidentSeverity.High)]
    [InlineData(500, IncidentSeverity.Critical)]
    public void Severity_scales_with_how_far_past_the_threshold_it_is(long count, IncidentSeverity expected)
    {
        var verdict = DetectionRuleEngine.Evaluate(Input(windowCount: count), Options);

        Assert.Equal(expected, verdict.Severity);
    }

    // ---- Rule 2: rate spike against baseline ----

    [Fact]
    public void Opens_when_the_rate_far_exceeds_the_baseline()
    {
        // 20/min now against 0.1/min historically: a genuine regression that is
        // still well under the absolute threshold's radar over five minutes.
        var verdict = DetectionRuleEngine.Evaluate(
            Input(windowCount: 20, baselineCount: 6), Options);

        Assert.True(verdict.ShouldOpen);
        Assert.Equal(DetectionRule.RateSpike, verdict.Rule);
    }

    [Fact]
    public void Does_not_open_on_a_spike_below_the_minimum_count()
    {
        // Without a floor, 1 occurrence against a baseline of almost nothing is
        // a huge multiple, and the system opens an incident for anything that
        // happens twice.
        var verdict = DetectionRuleEngine.Evaluate(
            Input(windowCount: Options.RateSpikeMinimumCount - 1, baselineCount: 1), Options);

        Assert.False(verdict.ShouldOpen);
    }

    [Fact]
    public void Does_not_open_when_the_rate_is_merely_normal()
    {
        // 20 in the window against 240 over the baseline hour is the same rate,
        // not a spike.
        var verdict = DetectionRuleEngine.Evaluate(
            Input(windowCount: 20, baselineCount: 240), Options);

        Assert.False(verdict.ShouldOpen);
    }

    [Fact]
    public void A_pattern_with_no_baseline_at_all_is_new_not_spiking()
    {
        // Dividing by a zero baseline would make every first occurrence an
        // infinite spike.
        var verdict = DetectionRuleEngine.Evaluate(
            Input(windowCount: Options.RateSpikeMinimumCount, baselineCount: 0), Options);

        Assert.False(verdict.ShouldOpen);
    }

    // ---- Rule 3: server error spike ----

    [Fact]
    public void Opens_on_a_server_error_spike_even_when_no_single_pattern_qualifies()
    {
        // The case no per-pattern threshold can see: an outage showing up as
        // many different errors, none of them individually past its own bar.
        var verdict = DetectionRuleEngine.Evaluate(
            Input(windowCount: 3, serverErrors: Options.ServerErrorThreshold), Options);

        Assert.True(verdict.ShouldOpen);
        Assert.Equal(DetectionRule.ServerErrorSpike, verdict.Rule);
        Assert.Equal(IncidentSeverity.Critical, verdict.Severity);
    }

    [Fact]
    public void Does_not_open_below_the_server_error_threshold()
    {
        var verdict = DetectionRuleEngine.Evaluate(
            Input(windowCount: 3, serverErrors: Options.ServerErrorThreshold - 1), Options);

        Assert.False(verdict.ShouldOpen);
    }

    // ---- Rule 4: new error after deployment ----

    [Fact]
    public void Opens_for_a_new_error_shortly_after_a_deployment()
    {
        var verdict = DetectionRuleEngine.Evaluate(
            Input(windowCount: Options.PostDeploymentCountThreshold, isNew: true,
                  sinceDeployment: TimeSpan.FromMinutes(4)), Options);

        Assert.True(verdict.ShouldOpen);
        Assert.Equal(DetectionRule.NewErrorAfterDeployment, verdict.Rule);
        Assert.Equal(IncidentSeverity.Critical, verdict.Severity);
    }

    [Fact]
    public void The_post_deployment_rule_beats_the_generic_count_rule()
    {
        // Both would fire. The more specific reason is the more useful one to
        // put in front of whoever gets paged.
        var verdict = DetectionRuleEngine.Evaluate(
            Input(windowCount: 500, isNew: true, sinceDeployment: TimeSpan.FromMinutes(2)), Options);

        Assert.Equal(DetectionRule.NewErrorAfterDeployment, verdict.Rule);
        Assert.Contains("deployment", verdict.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void An_old_error_after_a_deployment_does_not_get_the_lower_bar()
    {
        // A pattern that has been happening for weeks is not evidence about
        // this release, so it must clear the ordinary threshold.
        var verdict = DetectionRuleEngine.Evaluate(
            Input(windowCount: Options.PostDeploymentCountThreshold, isNew: false,
                  sinceDeployment: TimeSpan.FromMinutes(2)), Options);

        Assert.False(verdict.ShouldOpen);
    }

    [Fact]
    public void A_new_error_long_after_a_deployment_does_not_get_the_lower_bar()
    {
        var verdict = DetectionRuleEngine.Evaluate(
            Input(windowCount: Options.PostDeploymentCountThreshold, isNew: true,
                  sinceDeployment: TimeSpan.FromHours(6)), Options);

        Assert.False(verdict.ShouldOpen);
    }

    [Fact]
    public void A_new_error_with_no_deployment_at_all_does_not_get_the_lower_bar()
    {
        var verdict = DetectionRuleEngine.Evaluate(
            Input(windowCount: Options.PostDeploymentCountThreshold, isNew: true, sinceDeployment: null), Options);

        Assert.False(verdict.ShouldOpen);
    }

    [Fact]
    public void A_quiet_pattern_opens_nothing()
    {
        Assert.False(DetectionRuleEngine.Evaluate(Input(windowCount: 1), Options).ShouldOpen);
    }
}
