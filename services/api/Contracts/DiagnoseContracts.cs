using System.Text.Json.Serialization;

namespace IncidentIQ.Api.Contracts;

/// <summary>
/// "I have uploaded a log for this service; tell me what is wrong with it."
///
/// Deliberately carries no log lines. The upload itself goes to ingestion
/// through the same public contract every other client uses, so the pasted log
/// takes exactly the path a production agent's logs take - normalisation,
/// fingerprinting, patterns - and nothing about this page is a special case
/// downstream. All this request has to do is name the window that upload
/// landed in.
/// </summary>
public sealed record DiagnoseRequest
{
    [JsonPropertyName("service")]
    public string? Service { get; init; }

    [JsonPropertyName("environment")]
    public string? Environment { get; init; }

    /// <summary>
    /// Only patterns still occurring at or after this instant are considered -
    /// the client sends the moment it started uploading.
    ///
    /// Without it, "the dominant error pattern" would mean the loudest error
    /// this service has ever produced, and someone diagnosing a fresh log would
    /// be handed last Tuesday's outage.
    /// </summary>
    [JsonPropertyName("since")]
    public DateTimeOffset? Since { get; init; }
}

/// <summary>The state of the diagnosis, which the client polls until it is not <c>pending</c>.</summary>
public static class DiagnoseStatuses
{
    /// <summary>The upload has not finished being processed. Ask again.</summary>
    public const string Pending = "pending";

    /// <summary>An incident was opened for the dominant pattern.</summary>
    public const string Opened = "opened";

    /// <summary>An incident for that pattern was already open; the user is sent to it.</summary>
    public const string Existing = "existing";
}

public sealed record DiagnoseResult
{
    [JsonPropertyName("status")]
    public required string Status { get; init; }

    [JsonPropertyName("incidentId")]
    public Guid? IncidentId { get; init; }

    /// <summary>The dominant pattern's fingerprint, so the UI can link to its raw lines.</summary>
    [JsonPropertyName("fingerprint")]
    public string? Fingerprint { get; init; }

    [JsonPropertyName("title")]
    public string? Title { get; init; }

    /// <summary>Occurrences of the dominant pattern inside the window, not its lifetime total.</summary>
    [JsonPropertyName("occurrenceCount")]
    public long OccurrenceCount { get; init; }

    /// <summary>How many distinct error patterns the upload produced, dominant one included.</summary>
    [JsonPropertyName("patternsFound")]
    public int PatternsFound { get; init; }

    /// <summary>Written to be shown to the person waiting, especially while pending.</summary>
    [JsonPropertyName("message")]
    public required string Message { get; init; }
}
