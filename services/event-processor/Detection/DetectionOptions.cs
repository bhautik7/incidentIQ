namespace IncidentIQ.EventProcessor.Detection;

/// <summary>
/// Every number a detection rule depends on, in one place and configurable.
///
/// Detection thresholds are the settings most likely to be wrong on the first
/// try, and wrong in both directions: too low and every deploy pages someone,
/// too high and real outages go unnoticed. They are configuration rather than
/// constants so they can be tuned without a release.
/// </summary>
public sealed class DetectionOptions
{
    public const string SectionName = "Detection";

    // ---- Rule 1: absolute count in a window ----

    /// <summary>
    /// Occurrences of one fingerprint within <see cref="WindowMinutes"/> before
    /// an incident opens.
    ///
    /// The single most important number here. A blunt instrument, and
    /// deliberately the first rule: it catches the case that matters most - a
    /// failure that is suddenly happening a lot - with no baseline, no history
    /// and no way to be clever and wrong.
    /// </summary>
    public int CountThreshold { get; set; } = 25;

    /// <summary>The window every count-based rule looks at.</summary>
    public int WindowMinutes { get; set; } = 5;

    // ---- Rule 2: rate spike against the pattern's own baseline ----

    /// <summary>
    /// How many times the baseline rate the current rate must reach.
    ///
    /// Catches what an absolute threshold misses: a pattern that normally
    /// occurs twice an hour and is now occurring twice a minute is a genuine
    /// regression long before it reaches 25 in five minutes.
    /// </summary>
    public double RateSpikeMultiplier { get; set; } = 10.0;

    /// <summary>
    /// The baseline period, ending where the current window begins.
    ///
    /// Must be long enough that normal variation averages out. Too short and
    /// every quiet minute makes the next minute look like a spike.
    /// </summary>
    public int BaselineMinutes { get; set; } = 60;

    /// <summary>
    /// Minimum occurrences in the window before the spike rule may fire at all.
    ///
    /// Without a floor, 1 occurrence against a baseline of 0.05 is a 20x spike,
    /// and the system opens an incident every time anything happens twice.
    /// </summary>
    public int RateSpikeMinimumCount { get; set; } = 10;

    // ---- Rule 3: server error spike, across fingerprints ----

    /// <summary>5xx responses for one service and environment in the window.</summary>
    public int ServerErrorThreshold { get; set; } = 50;

    // ---- Rule 4: new error shortly after a deployment ----

    /// <summary>
    /// How long after a release a brand-new fingerprint is treated as suspicious.
    ///
    /// Most regressions surface within minutes of the deploy that caused them,
    /// so a novel error inside this window earns a much lower threshold than
    /// the same error would on a quiet Tuesday.
    /// </summary>
    public int PostDeploymentWindowMinutes { get; set; } = 30;

    /// <summary>Occurrences needed for a new post-deployment error to open an incident.</summary>
    public int PostDeploymentCountThreshold { get; set; } = 5;

    // ---- Correlation ----

    /// <summary>
    /// How far back to look for a deployment that might explain an incident.
    ///
    /// Wider than the detection window above, because correlation is a hint
    /// shown to a human rather than a trigger - a slightly stale suspect is
    /// still useful context, whereas a missed one is a lost lead.
    /// </summary>
    public int DeploymentCorrelationMinutes { get; set; } = 60;

    // ---- Duplicate suppression and lifecycle ----

    /// <summary>
    /// After an incident is resolved, a recurrence within this window reopens it
    /// rather than opening a new one.
    ///
    /// Without a cooldown, a flapping error produces a fresh incident every few
    /// minutes and the list becomes unusable - which is the same failure the
    /// product exists to prevent, one level up.
    /// </summary>
    public int ReopenCooldownMinutes { get; set; } = 30;

    /// <summary>How long minute buckets are kept. Must exceed the baseline period.</summary>
    public int MetricRetentionHours { get; set; } = 72;

    /// <summary>Set false to run the processor without detection, e.g. while replaying.</summary>
    public bool Enabled { get; set; } = true;
}
