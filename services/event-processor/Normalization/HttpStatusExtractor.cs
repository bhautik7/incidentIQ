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

        // Named phrasings first: "status: 500" says what it is.
        if (TryMatch(ExplicitStatusPattern(), message, out var explicitStatus))
        {
            return explicitStatus;
        }

        // Then the access-log shape, where the status is named by position
        // rather than by a word: "GET /api/instruments 500 184ms".
        //
        // This is the form most request logging actually emits - ASP.NET Core,
        // nginx and Apache all produce a variant of it - and until it was read
        // here the server-error spike rule could not see the majority of the
        // 5xx responses in a typical estate. It counted only the ones whose
        // message happened to spell out the word "status".
        return TryMatch(AccessLogPattern(), message, out var positional)
            ? positional
            : null;
    }

    private static bool TryMatch(Regex pattern, string message, out int status)
    {
        var match = pattern.Match(message);

        if (match.Success
            && int.TryParse(match.Groups["code"].Value, out var parsed)
            && IsHttpStatus(parsed))
        {
            status = parsed;
            return true;
        }

        status = 0;
        return false;
    }

    public static bool IsServerError(int? status) => status is >= 500 and <= 599;

    private static bool IsHttpStatus(int value) => value is >= 100 and <= 599;

    [GeneratedRegex(
        @"(?:status(?:\s*code)?|http)\s*[:=]?\s*(?<code>[1-5]\d{2})\b",
        RegexOptions.IgnoreCase)]
    private static partial Regex ExplicitStatusPattern();

    /// <summary>
    /// A request line followed by its status: the shape of an access log.
    ///
    /// Anchored on an HTTP method followed by a path, because that pair is
    /// what makes a nearby three-digit number a status rather than a count, a
    /// port, or a duration. A bare 500 in prose is far more often a limit than
    /// a response code, and a false positive here opens an incident that never
    /// happened - so the anchor is required and the number is guarded on both
    /// sides.
    ///
    /// The guards, each earning its place against a real shape:
    ///
    /// - The path is consumed greedily, so "GET /api/orders/500" leaves no
    ///   status behind. The 500 in a path is a resource id.
    /// - The lookbehind demands a separator before the number. Every real
    ///   format puts whitespace or a quote there, and requiring it is what
    ///   stops the path being given back character by character until a query
    ///   parameter is read as a status: "GET /api/x?limit=500" backtracks to
    ///   leave "500" preceded by "=", and an exclusion list has to remember
    ///   "=" while a separator list simply never allows it.
    /// - The lookahead refuses a duration. "GET /api/x 184ms" would otherwise
    ///   read 184 as a status, and every slow request would look like a 1xx.
    /// - Bounded noise between path and code absorbs the connectors these
    ///   formats differ by - `HTTP/1.1"`, "responded", "->" - without letting
    ///   an unrelated number several clauses later be adopted.
    /// </summary>
    [GeneratedRegex(
        """
        \b(?:GET|POST|PUT|PATCH|DELETE|HEAD|OPTIONS|TRACE|CONNECT)\s+
        (?:https?://[^\s"']+|/[^\s"']*)
        [^\n]{0,40}?
        (?<=[\s"'>])(?<code>[1-5]\d{2})\b
        (?!\s*(?:ms|s|sec|secs|seconds|millis|milliseconds|bytes|kb|mb)\b)
        (?!\.\d)(?![\d/-])
        """,
        RegexOptions.IgnoreCase | RegexOptions.IgnorePatternWhitespace)]
    private static partial Regex AccessLogPattern();
}
