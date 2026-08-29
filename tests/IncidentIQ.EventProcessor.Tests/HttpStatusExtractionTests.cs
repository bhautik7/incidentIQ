using IncidentIQ.EventProcessor.Normalization;

namespace IncidentIQ.EventProcessor.Tests;

/// <summary>
/// Reading a status code off a log line.
///
/// The asymmetry here decides the shape of the regex. Missing a status costs
/// the server-error spike rule some of its evidence, and the incident is still
/// found by the count and rate rules. Inventing one puts a 5xx on a service
/// that returned nothing of the sort, and the rule that exists to notice a
/// service falling over then fires because a request took 184ms.
///
/// So the negative cases below are not edge cases being tidied up. They are
/// the reason the pattern is anchored on a request line rather than looking
/// for any three digits.
/// </summary>
public class HttpStatusExtractionTests
{
    [Theory]
    // The shape this was written for: ASP.NET Core, nginx and Apache all emit
    // a variant of it, and none of them use the word "status".
    [InlineData("GET /api/instruments 500 184ms", 500)]
    [InlineData("POST /api/orders 201 12ms", 201)]
    [InlineData("DELETE /api/orders/8801 204 8ms", 204)]
    [InlineData("HTTP GET /api/instruments responded 503 in 12.3456 ms", 503)]
    [InlineData("\"GET /api/instruments HTTP/1.1\" 502 1234", 502)]
    [InlineData("GET https://payments.internal/charge 504 2001ms", 504)]
    [InlineData("PATCH /api/v1/orders/8801 -> 422", 422)]
    [InlineData("get /api/lower 500 10ms", 500)]
    public void Reads_the_status_from_a_request_line(string message, int expected)
    {
        Assert.Equal(expected, HttpStatusExtractor.Extract(null, message));
    }

    [Theory]
    // A duration is the trap. Every one of these numbers is three digits in
    // the 100-599 range sitting immediately after a request line.
    [InlineData("GET /api/instruments 184ms")]
    [InlineData("GET /api/instruments took 250 ms")]
    [InlineData("GET /api/instruments completed in 320 milliseconds")]
    // A resource id inside the path is not a status.
    [InlineData("GET /api/orders/500")]
    [InlineData("GET /api/orders/404/items")]
    // No request line at all: these numbers are counts and limits.
    [InlineData("Retry budget exhausted for order 8801 after 3 attempts")]
    [InlineData("Connection pool exhausted, MaxPoolSize (currently 500)")]
    [InlineData("Processed 404 records in batch")]
    // A number glued to something else.
    [InlineData("GET /api/x?limit=500")]
    public void Refuses_a_number_that_is_not_a_status(string message)
    {
        Assert.Null(HttpStatusExtractor.Extract(null, message));
    }

    [Fact]
    public void Prefers_the_status_over_a_duration_on_the_same_line()
    {
        // Both numbers qualify on shape; only position tells them apart.
        Assert.Equal(200, HttpStatusExtractor.Extract(null, "GET /api/health 200 1500ms"));
    }

    [Fact]
    public void Structured_properties_still_win_over_the_message()
    {
        // A property named statusCode means what it says; a number in prose is
        // an inference. When they disagree, the property is the fact.
        var properties = new Dictionary<string, string> { ["statusCode"] = "503" };

        Assert.Equal(503, HttpStatusExtractor.Extract(properties, "GET /api/x 200 12ms"));
    }

    [Fact]
    public void Explicit_phrasing_still_works()
    {
        Assert.Equal(500, HttpStatusExtractor.Extract(null, "Request failed with status code 500"));
    }

    [Theory]
    [InlineData(500, true)]
    [InlineData(503, true)]
    [InlineData(499, false)]
    [InlineData(404, false)]
    [InlineData(null, false)]
    public void Server_errors_are_the_five_hundreds(int? status, bool expected)
    {
        Assert.Equal(expected, HttpStatusExtractor.IsServerError(status));
    }
}
