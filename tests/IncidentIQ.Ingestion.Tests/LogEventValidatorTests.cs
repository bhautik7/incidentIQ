using IncidentIQ.Contracts;
using IncidentIQ.Ingestion;
using IncidentIQ.Ingestion.Api;
using Microsoft.Extensions.Options;

namespace IncidentIQ.Ingestion.Tests;

/// <summary>
/// Pure unit tests. The validator does no I/O and takes its clock as a
/// parameter, so every rule is exercised here with no infrastructure at all -
/// which is the point of keeping it that way.
/// </summary>
public class LogEventValidatorTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 24, 12, 0, 0, TimeSpan.Zero);

    private static LogEventValidator CreateValidator(IngestionOptions? options = null) =>
        new(Options.Create(options ?? new IngestionOptions()));

    private static LogEventRequest ValidEvent() => new()
    {
        Service = "payments-api",
        Environment = "production",
        Timestamp = Now.AddSeconds(-5),
        Severity = "Error",
        Message = "The connection pool has been exhausted"
    };

    [Fact]
    public void Accepts_a_well_formed_event()
    {
        var outcome = CreateValidator().Validate(ValidEvent(), Now);

        Assert.True(outcome.IsValid);
        Assert.Equal(LogSeverity.Error, outcome.Severity);
    }

    [Theory]
    [InlineData("warn", LogSeverity.Warning)]
    [InlineData("WARN", LogSeverity.Warning)]
    [InlineData("Warning", LogSeverity.Warning)]
    [InlineData("info", LogSeverity.Information)]
    [InlineData("critical", LogSeverity.Fatal)]
    [InlineData("ERR", LogSeverity.Error)]
    [InlineData("verbose", LogSeverity.Trace)]
    public void Normalises_severity_spellings_real_agents_emit(string input, string expected)
    {
        var outcome = CreateValidator().Validate(ValidEvent() with { Severity = input }, Now);

        Assert.True(outcome.IsValid);
        Assert.Equal(expected, outcome.Severity);
    }

    [Fact]
    public void Rejects_unknown_severity_and_lists_the_accepted_values()
    {
        var outcome = CreateValidator().Validate(ValidEvent() with { Severity = "catastrophe" }, Now);

        Assert.False(outcome.IsValid);
        Assert.Equal("severity", outcome.Field);
        Assert.Contains("Information", outcome.Message);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Rejects_missing_service(string? service)
    {
        var outcome = CreateValidator().Validate(ValidEvent() with { Service = service }, Now);

        Assert.False(outcome.IsValid);
        Assert.Equal("service", outcome.Field);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Rejects_missing_message(string? message)
    {
        var outcome = CreateValidator().Validate(ValidEvent() with { Message = message }, Now);

        Assert.False(outcome.IsValid);
        Assert.Equal("message", outcome.Field);
    }

    [Fact]
    public void Rejects_missing_timestamp()
    {
        var outcome = CreateValidator().Validate(ValidEvent() with { Timestamp = null }, Now);

        Assert.False(outcome.IsValid);
        Assert.Equal("timestamp", outcome.Field);
    }

    [Fact]
    public void Accepts_a_timestamp_inside_the_allowed_clock_skew()
    {
        // Client clocks drift. Rejecting anything at all in the future would
        // drop legitimate logs from machines running a few minutes fast.
        var outcome = CreateValidator().Validate(
            ValidEvent() with { Timestamp = Now.AddMinutes(30) }, Now);

        Assert.True(outcome.IsValid);
    }

    [Fact]
    public void Rejects_a_timestamp_beyond_the_allowed_clock_skew()
    {
        var outcome = CreateValidator().Validate(
            ValidEvent() with { Timestamp = Now.AddHours(3) }, Now);

        Assert.False(outcome.IsValid);
        Assert.Equal("timestamp", outcome.Field);
        Assert.Contains("future", outcome.Message);
    }

    [Fact]
    public void Rejects_an_event_older_than_the_replay_window()
    {
        var outcome = CreateValidator().Validate(
            ValidEvent() with { Timestamp = Now.AddDays(-30) }, Now);

        Assert.False(outcome.IsValid);
        Assert.Equal("timestamp", outcome.Field);
    }

    [Fact]
    public void Rejects_an_oversized_message()
    {
        var options = new IngestionOptions { MaxMessageLength = 100 };
        var outcome = CreateValidator(options).Validate(
            ValidEvent() with { Message = new string('x', 101) }, Now);

        Assert.False(outcome.IsValid);
        Assert.Equal("message", outcome.Field);
    }

    [Fact]
    public void Rejects_too_many_metadata_entries()
    {
        var options = new IngestionOptions { MaxMetadataEntries = 3 };
        var metadata = Enumerable.Range(0, 4).ToDictionary(i => $"key{i}", i => $"value{i}");

        var outcome = CreateValidator(options).Validate(ValidEvent() with { Metadata = metadata }, Now);

        Assert.False(outcome.IsValid);
        Assert.Equal("metadata", outcome.Field);
    }

    [Fact]
    public void Rejects_an_oversized_metadata_value()
    {
        var options = new IngestionOptions { MaxMetadataValueLength = 10 };
        var metadata = new Dictionary<string, string> { ["pod"] = new('x', 11) };

        var outcome = CreateValidator(options).Validate(ValidEvent() with { Metadata = metadata }, Now);

        Assert.False(outcome.IsValid);
        Assert.Contains("pod", outcome.Message);
    }

    [Fact]
    public void Rejects_an_oversized_trace_id()
    {
        var outcome = CreateValidator().Validate(
            ValidEvent() with { TraceId = new string('a', 65) }, Now);

        Assert.False(outcome.IsValid);
        Assert.Equal("traceId", outcome.Field);
    }

    [Fact]
    public void Accepts_an_event_with_every_optional_field_omitted()
    {
        var outcome = CreateValidator().Validate(new LogEventRequest
        {
            Service = "orders-api",
            Environment = "staging",
            Timestamp = Now,
            Severity = "info",
            Message = "started"
        }, Now);

        Assert.True(outcome.IsValid);
    }
}
