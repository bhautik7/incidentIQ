using System.Text.RegularExpressions;

namespace IncidentIQ.EventProcessor.Normalization;

/// <summary>
/// Replaces the parts of a log message that vary between occurrences with
/// placeholders, leaving the part that identifies the *kind* of failure.
///
///   "Connection timeout for user 18273"  ->  "Connection timeout for user {NUM}"
///   "Connection timeout for user 94822"  ->  "Connection timeout for user {NUM}"
///
/// Both messages now share one template, so 4,200 log lines become one pattern
/// and one incident instead of 4,200.
///
/// Placeholders name what was *recognised*, not what it means. "{NUM}" rather
/// than "{ID}" or "{USER_ID}", because deciding that 18273 is a user id
/// requires guessing intent from surrounding words, and that guess is wrong
/// often enough to split one pattern into several - which is the exact failure
/// this class exists to prevent.
///
/// The two ways to get this wrong are not symmetrical:
/// - Masking too little splits one incident into thousands. The product stops working.
/// - Masking too much merges unrelated failures. The product lies.
/// Both are bad, so every rule here matches a shape that is unambiguously an
/// identifier or a value, and never a word.
/// </summary>
public static partial class LogMessageNormalizer
{
    public const string Uuid = "{UUID}";
    public const string Timestamp = "{TIMESTAMP}";
    public const string Ip = "{IP}";
    public const string Email = "{EMAIL}";
    public const string Url = "{URL}";
    public const string Path = "{PATH}";
    public const string Hex = "{HEX}";
    public const string Number = "{NUM}";

    /// <summary>Longest sensible message to normalise; beyond this the tail is dropped.</summary>
    private const int MaxLength = 4_000;

    public static string Normalize(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return string.Empty;
        }

        var text = message.Length > MaxLength ? message[..MaxLength] : message;

        // Order is load-bearing. Every rule below would also match a fragment of
        // the ones above it: a UUID contains digits, a timestamp contains
        // numbers and colons, an IP is four numbers. Broad rules must run last
        // or they will shred the values the specific rules were going to catch.
        text = UuidPattern().Replace(text, Uuid);
        text = TimestampPattern().Replace(text, Timestamp);
        text = EmailPattern().Replace(text, Email);
        text = UrlPattern().Replace(text, Url);
        text = IpPattern().Replace(text, Ip);
        text = PathPattern().Replace(text, Path);
        text = HexPattern().Replace(text, Hex);
        text = NumberPattern().Replace(text, Number);

        // Collapse whitespace last: stack traces and wrapped messages differ in
        // indentation between runs, and that difference is not information.
        return WhitespacePattern().Replace(text, " ").Trim();
    }

    [GeneratedRegex(@"\b[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}\b")]
    private static partial Regex UuidPattern();

    /// <summary>ISO-8601-ish timestamps, with or without zone and fractional seconds.</summary>
    [GeneratedRegex(@"\d{4}-\d{2}-\d{2}([T ]\d{2}:\d{2}:\d{2}(\.\d+)?(Z|[+-]\d{2}:?\d{2})?)?")]
    private static partial Regex TimestampPattern();

    [GeneratedRegex(@"\b[\w.%+-]+@[\w.-]+\.[A-Za-z]{2,}\b")]
    private static partial Regex EmailPattern();

    [GeneratedRegex(@"\b[a-zA-Z][a-zA-Z0-9+.-]*://[^\s""']+")]
    private static partial Regex UrlPattern();

    [GeneratedRegex(@"\b\d{1,3}\.\d{1,3}\.\d{1,3}\.\d{1,3}\b(:\d{1,5})?")]
    private static partial Regex IpPattern();

    /// <summary>
    /// Absolute file paths, Unix and Windows. Build agents and containers put
    /// the same source file at different paths, so an unmasked path splits one
    /// exception into one pattern per machine.
    /// </summary>
    [GeneratedRegex(@"(?:[A-Za-z]:\\|/)(?:[\w .-]+[/\\])+[\w .-]+")]
    private static partial Regex PathPattern();

    /// <summary>
    /// Long hex runs: correlation ids, hashes, object addresses, trace ids.
    /// Eight characters is the shortest run that is far more likely to be an
    /// identifier than an English word.
    /// </summary>
    [GeneratedRegex(@"\b(0x)?[0-9a-fA-F]{8,}\b")]
    private static partial Regex HexPattern();

    /// <summary>
    /// Any remaining number: decimals, negatives, thousands separators, and
    /// dotted version numbers.
    ///
    /// There is a word boundary before the number but deliberately not after
    /// it, so a unit suffix does not defeat the match. "250ms" and "500ms" are
    /// the same failure and must share a template; requiring a trailing
    /// boundary leaves both unmasked and produces one pattern per timeout
    /// value. The leading boundary still prevents matching the "123" inside an
    /// identifier like "worker123".
    ///
    /// Deliberately last: every more specific rule above would also match some
    /// of this.
    /// </summary>
    [GeneratedRegex(@"-?\b\d[\d,]*(\.\d+)*")]
    private static partial Regex NumberPattern();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespacePattern();
}
