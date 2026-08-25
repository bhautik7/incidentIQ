using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace IncidentIQ.EventProcessor.Normalization;

/// <summary>
/// Turns a normalised log event into a stable 64-character identity for the
/// *kind* of failure it represents.
///
/// Determinism is the whole contract. The same failure must produce the same
/// fingerprint on every replica, after every restart, and after a redeploy -
/// otherwise a rolling deployment silently splits one incident in two, and a
/// Kafka replay creates a parallel universe of duplicates.
///
/// That rules out anything that varies between occurrences of the same failure:
/// timestamps, host names, trace ids, thread ids, process ids, and the raw
/// message. Only the normalised template and the structural facts go in.
///
/// It is scoped by organization so two tenants producing byte-identical errors
/// never share a pattern, and by environment so a staging failure never merges
/// into the production incident that woke someone up.
/// </summary>
public static partial class LogFingerprint
{
    /// <summary>
    /// How many stack frames participate. Deep enough to separate two different
    /// callers of the same helper, shallow enough that an unrelated change
    /// further down the stack does not fork the pattern.
    /// </summary>
    public const int StackFrameDepth = 3;

    /// <summary>
    /// ASCII unit separator. A character that cannot occur in a service name,
    /// an exception type or a log message, so no combination of field values
    /// can be arranged to collide with a different combination.
    /// </summary>
    private const char Separator = '\u001F';

    public static string Compute(
        Guid organizationId,
        string environment,
        string service,
        string? exceptionType,
        string normalizedMessage,
        string? stackTrace)
    {
        var canonical = new StringBuilder(512)
            .Append(organizationId.ToString("D")).Append(Separator)
            .Append(Canonicalise(environment)).Append(Separator)
            .Append(Canonicalise(service)).Append(Separator)
            .Append(Canonicalise(exceptionType)).Append(Separator)
            .Append(normalizedMessage).Append(Separator)
            .Append(NormalizeStackFrames(stackTrace))
            .ToString();

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    /// <summary>
    /// The top frames, stripped of everything that moves: line numbers, file
    /// paths, and the "at " prefix.
    ///
    /// Line numbers are the trap. Leaving them in means a one-line edit
    /// anywhere above the throw site produces a brand new fingerprint, a brand
    /// new incident, and a lost history for a failure that never changed.
    /// </summary>
    public static string NormalizeStackFrames(string? stackTrace)
    {
        if (string.IsNullOrWhiteSpace(stackTrace))
        {
            return string.Empty;
        }

        var frames = stackTrace
            .Split(['\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(line => line.Length > 0)
            .Take(StackFrameDepth)
            .Select(CleanFrame)
            .Where(frame => frame.Length > 0);

        return string.Join('\n', frames);
    }

    private static string CleanFrame(string line)
    {
        var frame = line;

        if (frame.StartsWith("at ", StringComparison.OrdinalIgnoreCase))
        {
            frame = frame[3..];
        }

        // " in /src/Foo.cs:line 42" and ":line 42" both carry a line number.
        frame = FileAndLinePattern().Replace(frame, string.Empty);
        frame = LineOnlyPattern().Replace(frame, string.Empty);

        return frame.Trim();
    }

    private static string Canonicalise(string? value) =>
        string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim().ToLowerInvariant();

    [GeneratedRegex(@"\s+in\s+.+?:line\s+\d+", RegexOptions.IgnoreCase)]
    private static partial Regex FileAndLinePattern();

    [GeneratedRegex(@":line\s+\d+", RegexOptions.IgnoreCase)]
    private static partial Regex LineOnlyPattern();
}
