using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using IncidentIQ.Contracts;
using IncidentIQ.Contracts.Payloads;
using IncidentIQ.Ingestion.Api;

namespace IncidentIQ.Ingestion.Tests;

/// <summary>
/// Integration tests for POST /api/v1/logs/batch, exercising the full HTTP
/// pipeline: authentication, rate limiting, model binding, validation and
/// publishing. Kafka is faked; the broker leg has its own test.
/// </summary>
public class LogBatchEndpointTests : IClassFixture<IngestionApiFactory>
{
    private readonly IngestionApiFactory _factory;

    public LogBatchEndpointTests(IngestionApiFactory factory) => _factory = factory;

    private static object ValidEvent(string service = "payments-api", Guid? eventId = null) => new
    {
        eventId = eventId ?? Guid.CreateVersion7(),
        service,
        environment = "production",
        timestamp = DateTimeOffset.UtcNow,
        severity = "Error",
        message = "The connection pool has been exhausted",
        exceptionType = "Npgsql.NpgsqlException",
        traceId = "abc123",
        spanId = "def456",
        host = "payments-api-7d9f",
        metadata = new Dictionary<string, string> { ["deploymentVersion"] = "2.31.0" }
    };

    [Fact]
    public async Task Accepts_a_valid_batch_and_returns_202()
    {
        var client = _factory.CreateApiClient();

        var response = await client.PostAsJsonAsync("/api/v1/logs/batch", new
        {
            events = new[] { ValidEvent(), ValidEvent(), ValidEvent() }
        });

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(3, body.GetProperty("accepted").GetInt32());
        Assert.Equal(0, body.GetProperty("rejected").GetInt32());
    }

    [Fact]
    public async Task Rejects_a_request_with_no_api_key()
    {
        var client = _factory.CreateApiClient(apiKey: null);

        var response = await client.PostAsJsonAsync("/api/v1/logs/batch", new { events = new[] { ValidEvent() } });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Rejects_a_request_with_an_unknown_api_key()
    {
        var client = _factory.CreateApiClient("iiq_not_a_real_key");

        var response = await client.PostAsJsonAsync("/api/v1/logs/batch", new { events = new[] { ValidEvent() } });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);

        // The response must not reveal whether the key exists but is disabled,
        // or never existed at all.
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("disabled", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Rejects_an_empty_batch()
    {
        var client = _factory.CreateApiClient();

        var response = await client.PostAsJsonAsync("/api/v1/logs/batch", new { events = Array.Empty<object>() });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Rejects_a_batch_over_the_maximum_size()
    {
        var client = _factory.CreateApiClient();
        var events = Enumerable.Range(0, 501).Select(_ => ValidEvent()).ToArray();

        var response = await client.PostAsJsonAsync("/api/v1/logs/batch", new { events });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync();
        // The error must say what the limit is and what was sent, so a client
        // can fix it without reading our source.
        Assert.Contains("500", body);
        Assert.Contains("501", body);
    }

    [Fact]
    public async Task Accepts_valid_events_and_reports_the_invalid_ones()
    {
        var client = _factory.CreateApiClient();

        var response = await client.PostAsJsonAsync("/api/v1/logs/batch", new
        {
            events = new object[]
            {
                ValidEvent(),
                new { service = (string?)null, environment = "production", timestamp = DateTimeOffset.UtcNow, severity = "Error", message = "no service" },
                ValidEvent(),
                new { service = "orders-api", environment = "production", timestamp = DateTimeOffset.UtcNow, severity = "nonsense", message = "bad severity" }
            }
        });

        // Partial success, not all-or-nothing: one malformed event must not
        // cost a client the other 499 in the batch.
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(2, body.GetProperty("accepted").GetInt32());
        Assert.Equal(2, body.GetProperty("rejected").GetInt32());

        var errors = body.GetProperty("errors").EnumerateArray().ToList();
        Assert.Equal(2, errors.Count);

        // Errors are identified by index so the client knows which events to fix.
        Assert.Equal(1, errors[0].GetProperty("index").GetInt32());
        Assert.Equal("service", errors[0].GetProperty("field").GetString());
        Assert.Equal(3, errors[1].GetProperty("index").GetInt32());
        Assert.Equal("severity", errors[1].GetProperty("field").GetString());
    }

    [Fact]
    public async Task Returns_400_when_every_event_is_invalid()
    {
        var client = _factory.CreateApiClient();

        var response = await client.PostAsJsonAsync("/api/v1/logs/batch", new
        {
            events = new[]
            {
                new { service = "", environment = "production", timestamp = DateTimeOffset.UtcNow, severity = "Error", message = "x" }
            }
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Echoes_a_supplied_correlation_id()
    {
        var client = _factory.CreateApiClient();
        var correlationId = Guid.CreateVersion7();

        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/logs/batch")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(new { events = new[] { ValidEvent() } }),
                Encoding.UTF8, "application/json")
        };
        request.Headers.Add(LogIngestionEndpoints.CorrelationIdHeader, correlationId.ToString());

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Equal(correlationId.ToString(), response.Headers.GetValues(LogIngestionEndpoints.CorrelationIdHeader).Single());

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(correlationId, body.GetProperty("correlationId").GetGuid());
    }

    [Fact]
    public async Task Generates_a_correlation_id_when_the_client_supplies_a_malformed_one()
    {
        var client = _factory.CreateApiClient();

        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/logs/batch")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(new { events = new[] { ValidEvent() } }),
                Encoding.UTF8, "application/json")
        };
        request.Headers.Add(LogIngestionEndpoints.CorrelationIdHeader, "not-a-guid");

        var response = await client.SendAsync(request);

        // A bad trace header must not cost the client its log batch.
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.NotEqual(Guid.Empty, body.GetProperty("correlationId").GetGuid());
    }

    [Fact]
    public async Task Stamps_tenant_and_correlation_id_onto_every_published_envelope()
    {
        var factory = new IngestionApiFactory();
        var client = factory.CreateApiClient();
        var correlationId = Guid.CreateVersion7();

        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/logs/batch")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(new { events = new[] { ValidEvent(), ValidEvent() } }),
                Encoding.UTF8, "application/json")
        };
        request.Headers.Add(LogIngestionEndpoints.CorrelationIdHeader, correlationId.ToString());
        request.Headers.Add(Auth.ApiKeyAuthenticationMiddleware.ApiKeyHeader, IngestionApiFactory.ValidApiKey);

        await client.SendAsync(request);

        var envelopes = factory.Producer.EnvelopesOf<LogReceived>().ToList();
        Assert.Equal(2, envelopes.Count);

        Assert.All(envelopes, e =>
        {
            Assert.Equal(IngestionApiFactory.TenantId, e.TenantId);
            Assert.Equal(correlationId, e.CorrelationId);
            Assert.Equal(EventTypes.LogReceived, e.EventType);
            Assert.Equal(1, e.EventVersion);
        });
    }

    [Fact]
    public async Task Uses_tenant_and_service_as_the_partition_key()
    {
        var factory = new IngestionApiFactory();
        var client = factory.CreateApiClient();

        await client.PostAsJsonAsync("/api/v1/logs/batch", new
        {
            events = new[] { ValidEvent("payments-api"), ValidEvent("orders-api"), ValidEvent("payments-api") }
        });

        var keys = factory.Producer.Published.Select(p => p.Key).ToList();

        // Same service, same key: that is what keeps a service's events on one
        // partition and therefore on one consumer, in order.
        Assert.Equal(2, keys.Count(k => k == $"{IngestionApiFactory.TenantId:D}:payments-api"));
        Assert.Equal(1, keys.Count(k => k == $"{IngestionApiFactory.TenantId:D}:orders-api"));
        Assert.All(factory.Producer.Published, p => Assert.Equal(Topics.LogsRaw, p.Topic));
    }

    [Fact]
    public async Task Preserves_a_client_supplied_event_id_for_idempotency()
    {
        var factory = new IngestionApiFactory();
        var client = factory.CreateApiClient();
        var eventId = Guid.CreateVersion7();

        await client.PostAsJsonAsync("/api/v1/logs/batch", new { events = new[] { ValidEvent(eventId: eventId) } });

        var envelope = Assert.Single(factory.Producer.EnvelopesOf<LogReceived>());
        Assert.Equal(eventId, envelope.Payload.LogEventId);
    }

    [Fact]
    public async Task Returns_503_when_kafka_is_unavailable()
    {
        var factory = new IngestionApiFactory();
        factory.Producer.ThrowOnPublish = new InvalidOperationException("broker unreachable");
        var client = factory.CreateApiClient();

        var response = await client.PostAsJsonAsync("/api/v1/logs/batch", new { events = new[] { ValidEvent() } });

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);

        // The client is told to retry with the same ids, which is what makes
        // the retry safe rather than duplicative.
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("same event ids", body);
    }

    [Fact]
    public async Task Returns_429_with_retry_after_when_the_tenant_exceeds_its_rate_limit()
    {
        var factory = new IngestionApiFactory();
        factory.Settings["Ingestion:RateLimitBucketCapacity"] = "3";
        factory.Settings["Ingestion:RateLimitTokensPerPeriod"] = "1";
        factory.Settings["Ingestion:RateLimitPeriodSeconds"] = "60";

        var client = factory.CreateApiClient();
        var payload = new { events = new[] { ValidEvent() } };

        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 6; i++)
        {
            var response = await client.PostAsJsonAsync("/api/v1/logs/batch", payload);
            statuses.Add(response.StatusCode);

            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                Assert.NotNull(response.Headers.RetryAfter);
            }
        }

        Assert.Contains(HttpStatusCode.Accepted, statuses);
        Assert.Contains(HttpStatusCode.TooManyRequests, statuses);
    }
}
