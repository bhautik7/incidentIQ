using IncidentIQ.EventProcessor.Normalization;

namespace IncidentIQ.EventProcessor.Tests;

public class LogMessageNormalizerTests
{
    [Fact]
    public void Collapses_the_same_message_with_different_ids()
    {
        // The worked example: two log lines that differ only by a user id must
        // become one template, or they become two incidents.
        var first = LogMessageNormalizer.Normalize("Connection timeout for user 18273");
        var second = LogMessageNormalizer.Normalize("Connection timeout for user 94822");

        Assert.Equal(first, second);
        Assert.Equal("Connection timeout for user {NUM}", first);
    }

    [Fact]
    public void Keeps_genuinely_different_messages_apart()
    {
        // Over-masking is as damaging as under-masking: it merges unrelated
        // failures into one incident and the product starts lying.
        var timeout = LogMessageNormalizer.Normalize("Connection timeout for user 18273");
        var denied = LogMessageNormalizer.Normalize("Permission denied for user 18273");

        Assert.NotEqual(timeout, denied);
    }

    [Theory]
    [InlineData("Order 3f2a9c1e-1234-4567-89ab-cdef01234567 failed", "Order {UUID} failed")]
    [InlineData("Connection refused to 10.0.14.221", "Connection refused to {IP}")]
    [InlineData("Connection refused to 10.0.14.221:5432", "Connection refused to {IP}")]
    [InlineData("Cannot email alice.smith@example.com", "Cannot email {EMAIL}")]
    [InlineData("GET https://api.example.com/v1/orders?id=9 failed", "GET {URL} failed")]
    [InlineData("Retry 3 of 5 after 250ms", "Retry {NUM} of {NUM} after {NUM}ms")]
    [InlineData("Balance is -42.50 for account 99", "Balance is {NUM} for account {NUM}")]
    [InlineData("Processed 1,234,567 rows", "Processed {NUM} rows")]
    public void Masks_each_kind_of_variable_value(string input, string expected)
    {
        Assert.Equal(expected, LogMessageNormalizer.Normalize(input));
    }

    [Fact]
    public void Masks_file_paths_so_one_error_is_not_split_per_machine()
    {
        // Build agents and containers put the same source file at different
        // paths; leaving them in forks one exception into one pattern per host.
        var linux = LogMessageNormalizer.Normalize("Could not open /var/data/app/config.json");
        var windows = LogMessageNormalizer.Normalize(@"Could not open C:\data\app\config.json");

        Assert.Equal("Could not open {PATH}", linux);
        Assert.Equal("Could not open {PATH}", windows);
    }

    [Theory]
    [InlineData("Timed out after 250ms", "Timed out after {NUM}ms")]
    [InlineData("Timed out after 500ms", "Timed out after {NUM}ms")]
    [InlineData("Cache exceeded 512MB", "Cache exceeded {NUM}MB")]
    [InlineData("Released after 30s", "Released after {NUM}s")]
    public void Masks_numbers_that_carry_a_unit_suffix(string input, string expected)
    {
        // "250ms" and "500ms" are the same failure. Requiring a word boundary
        // after the digits leaves both unmasked and yields one pattern per
        // timeout value.
        Assert.Equal(expected, LogMessageNormalizer.Normalize(input));
    }

    [Fact]
    public void Does_not_mask_digits_inside_an_identifier()
    {
        // The leading word boundary is what keeps "worker123" intact.
        Assert.Equal("Starting worker123", LogMessageNormalizer.Normalize("Starting worker123"));
    }

    [Fact]
    public void Collapses_dotted_version_numbers_into_one_placeholder()
    {
        Assert.Equal("Deployed {NUM}", LogMessageNormalizer.Normalize("Deployed 2.31.0"));
    }

    [Fact]
    public void Masks_long_hex_runs()
    {
        Assert.Equal("Request {HEX} aborted", LogMessageNormalizer.Normalize("Request a1b2c3d4e5f6 aborted"));
    }

    [Fact]
    public void Masks_timestamps()
    {
        var first = LogMessageNormalizer.Normalize("Lease expired at 2026-08-24T21:00:00Z");
        var second = LogMessageNormalizer.Normalize("Lease expired at 2026-08-25T03:14:59Z");

        Assert.Equal(first, second);
        Assert.Equal("Lease expired at {TIMESTAMP}", first);
    }

    [Fact]
    public void Collapses_whitespace_so_indentation_is_not_information()
    {
        Assert.Equal(
            "Query failed after {NUM} retries",
            LogMessageNormalizer.Normalize("Query   failed\n  after 3    retries"));
    }

    [Fact]
    public void Handles_the_real_connection_pool_message()
    {
        var normalized = LogMessageNormalizer.Normalize(
            "The connection pool has been exhausted, either raise MaxPoolSize (currently 100) or Timeout (currently 15 seconds)");

        Assert.Equal(
            "The connection pool has been exhausted, either raise MaxPoolSize (currently {NUM}) or Timeout (currently {NUM} seconds)",
            normalized);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Returns_empty_for_a_blank_message(string? input)
    {
        Assert.Equal(string.Empty, LogMessageNormalizer.Normalize(input));
    }
}
