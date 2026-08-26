using System.Text.RegularExpressions;

namespace IncidentIQ.EventProcessor.Normalization;

/// <summary>
/// Finds an HTTP status code on a log event, if it has one.
///
/// The server-error spike rule counts 5xx responses across a whole service,
/// spanning fingerprints, so it needs the status as a number rather than as
/// text buried in a message that normalisation has already masked.
///
/// Structured properties are checked first and the message only as a fallback:
/// a property named "statusCode" means what it says, whereas a number in prose
/// might be a port, a count, or a millisecond figure.
/// </summary>
public static partial class HttpStatusExtractor
{
    private static readonly string[] PropertyNames =
    [
        "statusCode", "status_code", "httpStatusCode", "http_status_code",
        "http.status_code", "responseStatus", "status"
    ];

    public static int? Extract(IReadOnlyDictionary<string, string>? properties, string? message)
    {
        if (properties is { Count: > 0 })
        {
            foreach (var name in PropertyNames)
            {
                if (properties.TryGetValue(name, out var raw)
                    && int.TryParse(raw, out var value)
                    && IsHttpStatus(value))
                {
                    return value;
                }
            }
        }

        if (string.IsNullOrWhiteSpace(message))
        {
            return null;
        }

        // Only phrasings that name the status explicitly. A bare "500" in a
        // message is far more often a limit or a duration than a status code,
        // and a false positive here opens an incident that never happened.
        var match = ExplicitStatusPattern().Match(message);

        return match.Success
               && int.TryParse(match.Groups["code"].Value, out var parsed)
               && IsHttpStatus(parsed)
            ? parsed
            : null;
    }

    public static bool IsServerError(int? status) => status is >= 500 and <= 599;

    private static bool IsHttpStatus(int value) => value is >= 100 and <= 599;

    [GeneratedRegex(
        @"(?:status(?:\s*code)?|http)\s*[:=]?\s*(?<code>[1-5]\d{2})\b",
        RegexOptions.IgnoreCase)]
    private static partial Regex ExplicitStatusPattern();
}
