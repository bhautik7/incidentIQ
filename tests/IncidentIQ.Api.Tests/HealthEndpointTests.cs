using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace IncidentIQ.Api.Tests;

/// <summary>
/// Foundation smoke tests: the host boots and the contract every other part of
/// the platform depends on (liveness, readiness, identity) behaves as designed.
/// </summary>
public class HealthEndpointTests(WebApplicationFactory<Program> factory)
    : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly HttpClient _client = factory.CreateClient();

    [Fact]
    public async Task Liveness_is_healthy_without_any_dependency()
    {
        var response = await _client.GetAsync("/health/live");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Healthy", body.GetProperty("status").GetString());
        Assert.Equal("incidentiq-api", body.GetProperty("service").GetString());
    }

    [Fact]
    public async Task Readiness_reports_postgres_when_it_is_not_configured()
    {
        var response = await _client.GetAsync("/health/ready");

        // No connection string in the test host, so readiness must fail loudly
        // and name the dependency rather than silently passing.
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var checkNames = body.GetProperty("checks")
            .EnumerateArray()
            .Select(check => check.GetProperty("name").GetString())
            .ToArray();

        Assert.Contains("postgres", checkNames);
    }

    [Fact]
    public async Task Root_endpoint_identifies_the_service()
    {
        var body = await _client.GetFromJsonAsync<JsonElement>("/");

        Assert.Equal("incidentiq-api", body.GetProperty("service").GetString());
        Assert.Equal("running", body.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Metrics_endpoint_is_exposed_for_prometheus()
    {
        var response = await _client.GetAsync("/metrics");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("process_", await response.Content.ReadAsStringAsync());
    }
}
