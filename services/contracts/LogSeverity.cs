namespace IncidentIQ.Contracts;

/// <summary>
/// The severity vocabulary carried in <c>LogReceived.Level</c>.
///
/// Lives in Contracts, not Domain, because it is part of the wire format: the
/// .NET services and the Python worker must agree on these exact strings. The
/// domain has its own <c>LogEventLevel</c> enum for persistence, and the
/// processor maps between them at the single point where the boundary is
/// crossed. A test asserts the two sets stay identical.
/// </summary>
public static class LogSeverity
{
    public const string Trace = "Trace";
    public const string Debug = "Debug";
    public const string Information = "Information";
    public const string Warning = "Warning";
    public const string Error = "Error";
    public const string Fatal = "Fatal";

    public static readonly IReadOnlyList<string> All =
        [Trace, Debug, Information, Warning, Error, Fatal];

    /// <summary>
    /// Spellings real agents emit, mapped to the canonical value.
    ///
    /// Rejecting "WARN" because the canonical name is "Warning" would be
    /// technically correct and practically useless - Serilog, log4net, Python
    /// logging and OpenTelemetry all disagree about the spelling, and none of
    /// them is going to change for us.
    /// </summary>
    private static readonly Dictionary<string, string> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["trace"] = Trace,
        ["verbose"] = Trace,
        ["debug"] = Debug,
        ["info"] = Information,
        ["information"] = Information,
        ["notice"] = Information,
        ["warn"] = Warning,
        ["warning"] = Warning,
        ["err"] = Error,
        ["error"] = Error,
        ["fatal"] = Fatal,
        ["critical"] = Fatal,
        ["crit"] = Fatal
    };

    /// <summary>Normalises any accepted spelling to its canonical form.</summary>
    public static bool TryNormalize(string? value, out string canonical)
    {
        if (!string.IsNullOrWhiteSpace(value) && Aliases.TryGetValue(value.Trim(), out var match))
        {
            canonical = match;
            return true;
        }

        canonical = string.Empty;
        return false;
    }
}
