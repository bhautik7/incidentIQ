using IncidentIQ.Contracts;
using Microsoft.Extensions.Options;

namespace IncidentIQ.Ingestion.Api;

public readonly record struct ValidationOutcome(bool IsValid, string Field, string Message, string Severity)
{
    public static ValidationOutcome Valid(string severity) => new(true, string.Empty, string.Empty, severity);
    public static ValidationOutcome Invalid(string field, string message) => new(false, field, message, string.Empty);
}

/// <summary>
/// Validates one submitted event.
///
/// Pure and synchronous: no I/O, no clock reads beyond the one passed in, no
/// dependency on Kafka. That is what makes the rules exhaustively unit-testable
/// without any infrastructure, which matters because these rules are the only
/// thing standing between a client bug and a corrupted incident timeline.
/// </summary>
public sealed class LogEventValidator(IOptions<IngestionOptions> options)
{
    private readonly IngestionOptions _options = options.Value;

    public ValidationOutcome Validate(LogEventRequest request, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(request.Service))
        {
            return ValidationOutcome.Invalid("service", "Service is required.");
        }

        if (request.Service.Length > _options.MaxServiceNameLength)
        {
            return ValidationOutcome.Invalid("service",
                $"Service must be {_options.MaxServiceNameLength} characters or fewer.");
        }

        if (string.IsNullOrWhiteSpace(request.Environment))
        {
            return ValidationOutcome.Invalid("environment", "Environment is required.");
        }

        if (request.Environment.Length > _options.MaxEnvironmentNameLength)
        {
            return ValidationOutcome.Invalid("environment",
                $"Environment must be {_options.MaxEnvironmentNameLength} characters or fewer.");
        }

        if (string.IsNullOrWhiteSpace(request.Message))
        {
            return ValidationOutcome.Invalid("message", "Message is required.");
        }

        if (request.Message.Length > _options.MaxMessageLength)
        {
            return ValidationOutcome.Invalid("message",
                $"Message must be {_options.MaxMessageLength} characters or fewer.");
        }

        if (request.Timestamp is not { } timestamp)
        {
            return ValidationOutcome.Invalid("timestamp", "Timestamp is required.");
        }

        // Bounded on both sides. A wildly future timestamp puts an event at the
        // top of every dashboard forever; a wildly old one lands outside the
        // replay window and silently distorts incident timelines.
        if (timestamp > now + _options.MaxClockSkewAhead)
        {
            return ValidationOutcome.Invalid("timestamp",
                $"Timestamp is more than {_options.MaxClockSkewAhead.TotalMinutes:N0} minutes in the future.");
        }

        if (timestamp < now - _options.MaxEventAge)
        {
            return ValidationOutcome.Invalid("timestamp",
                $"Timestamp is older than {_options.MaxEventAge.TotalDays:N0} days.");
        }

        if (string.IsNullOrWhiteSpace(request.Severity))
        {
            return ValidationOutcome.Invalid("severity", "Severity is required.");
        }

        if (!LogSeverity.TryNormalize(request.Severity, out var severity))
        {
            return ValidationOutcome.Invalid("severity",
                $"Unknown severity '{request.Severity}'. Expected one of: {string.Join(", ", LogSeverity.All)}.");
        }

        if (request.ExceptionType is { Length: > 0 } exceptionType
            && exceptionType.Length > _options.MaxExceptionTypeLength)
        {
            return ValidationOutcome.Invalid("exceptionType",
                $"ExceptionType must be {_options.MaxExceptionTypeLength} characters or fewer.");
        }

        if (request.StackTrace is { Length: > 0 } stackTrace
            && stackTrace.Length > _options.MaxStackTraceLength)
        {
            return ValidationOutcome.Invalid("stackTrace",
                $"StackTrace must be {_options.MaxStackTraceLength} characters or fewer.");
        }

        if (request.TraceId is { Length: > 0 } traceId && traceId.Length > _options.MaxTraceIdLength)
        {
            return ValidationOutcome.Invalid("traceId",
                $"TraceId must be {_options.MaxTraceIdLength} characters or fewer.");
        }

        if (request.SpanId is { Length: > 0 } spanId && spanId.Length > _options.MaxSpanIdLength)
        {
            return ValidationOutcome.Invalid("spanId",
                $"SpanId must be {_options.MaxSpanIdLength} characters or fewer.");
        }

        if (request.Host is { Length: > 0 } host && host.Length > _options.MaxHostLength)
        {
            return ValidationOutcome.Invalid("host",
                $"Host must be {_options.MaxHostLength} characters or fewer.");
        }

        if (request.Metadata is { Count: > 0 } metadata)
        {
            if (metadata.Count > _options.MaxMetadataEntries)
            {
                return ValidationOutcome.Invalid("metadata",
                    $"Metadata must contain {_options.MaxMetadataEntries} entries or fewer.");
            }

            foreach (var (key, value) in metadata)
            {
                if (string.IsNullOrWhiteSpace(key) || key.Length > _options.MaxMetadataKeyLength)
                {
                    return ValidationOutcome.Invalid("metadata",
                        $"Metadata keys must be non-empty and {_options.MaxMetadataKeyLength} characters or fewer.");
                }

                if (value is not null && value.Length > _options.MaxMetadataValueLength)
                {
                    return ValidationOutcome.Invalid("metadata",
                        $"Metadata value for '{key}' exceeds {_options.MaxMetadataValueLength} characters.");
                }
            }
        }

        return ValidationOutcome.Valid(severity);
    }
}
