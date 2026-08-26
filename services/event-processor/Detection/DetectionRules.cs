using IncidentIQ.Domain.Enums;

namespace IncidentIQ.EventProcessor.Detection;

/// <summary>
/// What the rules are given: counts already gathered from the database, so the
/// rules themselves do no I/O and can be tested exhaustively without one.
/// </summary>
public sealed record DetectionInput
{
    /// <summary>Occurrences of this fingerprint inside the detection window.</summary>
    public required long WindowCount { get; init; }

    /// <summary>Occurrences over the baseline period, ending where the window begins.</summary>
    public required long BaselineCount { get; init; }

    /// <summary>Length of the baseline period actually covered by data.</summary>
    public required TimeSpan BaselineDuration { get; init; }

    /// <summary>5xx responses for this service and environment inside the window, across all fingerprints.</summary>
    public required long ServerErrorWindowCount { get; init; }

    /// <summary>True when this fingerprint has never been seen before this batch.</summary>
    public required bool IsNewPattern { get; init; }

    /// <summary>How long ago the most recent deployment of this service went out, if any.</summary>
    public required TimeSpan? TimeSinceDeployment { get; init; }
}

public sealed record DetectionVerdict(bool ShouldOpen, DetectionRule Rule, IncidentSeverity Severity, string Reason)
{
    public static readonly DetectionVerdict None =
        new(false, DetectionRule.CountThreshold, IncidentSeverity.Low, "No rule matched.");
}

/// <summary>
/// The deterministic rules that decide whether a pattern deserves an incident.
///
/// Deliberately rules rather than a model. Three reasons, in order of how much
/// they matter:
///
/// <list type="number">
/// <item>An on-call engineer woken at 03:00 can be told exactly why - "412
/// occurrences in 5 minutes, threshold is 25" - and can argue with it. "The
/// model scored it 0.87" is not something anyone can act on or correct.</item>
/// <item>Rules can be tuned the moment they are wrong. A model needs labelled
/// data that this system does not have yet, because nobody has resolved any
/// incidents in it.</item>
/// <item>Rules are testable with no infrastructure and no training set, so
/// their behaviour at every boundary is pinned by tests rather than hoped for.</item>
/// </list>
///
/// Anomaly detection earns its place later, on top of these, once there is
/// history to learn a normal from and resolved incidents to learn what mattered.
/// </summary>
public static class DetectionRuleEngine
{
    public static DetectionVerdict Evaluate(DetectionInput input, DetectionOptions options)
    {
        // Order matters: the most specific and most confident rule wins, so the
        // recorded reason is the most useful explanation rather than merely the
        // first one that happened to match.

        // ---- Rule 4: a brand-new error moments after a release ----
        // The strongest signal in the system. A fingerprint that has never
        // occurred before, appearing right after a deploy, is a regression
        // until proven otherwise - so it gets a much lower bar.
        if (input.IsNewPattern
            && input.TimeSinceDeployment is { } since
            && since <= TimeSpan.FromMinutes(options.PostDeploymentWindowMinutes)
            && input.WindowCount >= options.PostDeploymentCountThreshold)
        {
            return new DetectionVerdict(
                true,
                DetectionRule.NewErrorAfterDeployment,
                IncidentSeverity.Critical,
                $"New error first seen {since.TotalMinutes:N0} minute(s) after a deployment, "
                + $"{input.WindowCount} occurrence(s) (threshold {options.PostDeploymentCountThreshold}).");
        }

        // ---- Rule 3: a burst of 5xx across the whole service ----
        // Spans fingerprints, so it catches an outage that manifests as many
        // different errors at once - which no per-pattern rule would see.
        if (input.ServerErrorWindowCount >= options.ServerErrorThreshold)
        {
            return new DetectionVerdict(
                true,
                DetectionRule.ServerErrorSpike,
                IncidentSeverity.Critical,
                $"{input.ServerErrorWindowCount} server error(s) in {options.WindowMinutes} minute(s) "
                + $"(threshold {options.ServerErrorThreshold}).");
        }

        // ---- Rule 1: absolute count ----
        if (input.WindowCount >= options.CountThreshold)
        {
            return new DetectionVerdict(
                true,
                DetectionRule.CountThreshold,
                SeverityForCount(input.WindowCount, options.CountThreshold),
                $"{input.WindowCount} occurrence(s) in {options.WindowMinutes} minute(s) "
                + $"(threshold {options.CountThreshold}).");
        }

        // ---- Rule 2: rate far above this pattern's own baseline ----
        // Runs last because it is the easiest to fool. The minimum-count floor
        // is what stops it firing on a pattern that went from almost never to
        // merely rarely.
        if (input.WindowCount >= options.RateSpikeMinimumCount && input.BaselineDuration > TimeSpan.Zero)
        {
            var windowRate = input.WindowCount / (double)options.WindowMinutes;
            var baselineRate = input.BaselineCount / input.BaselineDuration.TotalMinutes;

            // A pattern with no history at all is new, not spiking. Rule 4 owns
            // that case when a deployment explains it; otherwise the absolute
            // threshold above will catch it if it matters.
            if (baselineRate > 0 && windowRate >= baselineRate * options.RateSpikeMultiplier)
            {
                return new DetectionVerdict(
                    true,
                    DetectionRule.RateSpike,
                    IncidentSeverity.High,
                    $"Rate {windowRate:N1}/min is {windowRate / baselineRate:N1}x the baseline "
                    + $"{baselineRate:N2}/min (multiplier {options.RateSpikeMultiplier:N0}x).");
            }
        }

        return DetectionVerdict.None;
    }

    /// <summary>
    /// Severity from how far past the threshold the count is. Crude on purpose:
    /// an order of magnitude over the line is a different situation from one
    /// occurrence over it, and nothing subtler than that is defensible yet.
    /// </summary>
    private static IncidentSeverity SeverityForCount(long count, int threshold) => count switch
    {
        _ when count >= threshold * 20 => IncidentSeverity.Critical,
        _ when count >= threshold * 5 => IncidentSeverity.High,
        _ => IncidentSeverity.Medium
    };
}
