using System.Reflection;
using System.Text.Json;
using IncidentIQ.Shared.HealthChecks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Prometheus;
using Serilog;
using Serilog.Events;
using Serilog.Formatting.Compact;

namespace IncidentIQ.Shared;

/// <summary>
/// Opt-in dependency probes. A service only declares the infrastructure it
/// actually talks to, so its readiness endpoint means "I can do my job",
/// not "the whole platform is up".
/// </summary>
public sealed class IncidentIqDefaultsOptions
{
    public bool CheckPostgres { get; set; }
    public bool CheckKafka { get; set; }
}

/// <summary>
/// Cross-cutting host setup shared by every IncidentIQ .NET process:
/// environment-variable configuration, structured logging, health endpoints
/// and Prometheus metrics. Keeping this in one place is what makes the
/// three services behave identically in production.
/// </summary>
public static class ServiceDefaults
{
    private const string LiveTag = "live";
    private const string ReadyTag = "ready";
    private const string CorsPolicyName = "incidentiq-web";

    private static readonly JsonSerializerOptions HealthJsonOptions = new()
    {
        WriteIndented = false
    };

    public static WebApplicationBuilder AddIncidentIqDefaults(
        this WebApplicationBuilder builder,
        string serviceName,
        Action<IncidentIqDefaultsOptions>? configure = null)
    {
        var options = new IncidentIqDefaultsOptions();
        configure?.Invoke(options);

        var version = Assembly.GetEntryAssembly()
                          ?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                      ?? "0.0.0";
        var environment = builder.Environment.EnvironmentName;

        builder.Services.AddSingleton(new ServiceInfo(serviceName, version, environment));

        ConfigureLogging(builder, serviceName, version, environment);
        ConfigureHealthChecks(builder, options);
        ConfigureCors(builder);

        return builder;
    }

    public static WebApplication MapIncidentIqDefaults(this WebApplication app)
    {
        app.UseSerilogRequestLogging(o =>
        {
            // Probes and scrapes run every few seconds; logging them at Information
            // would drown out everything that matters.
            o.GetLevel = (ctx, _, ex) =>
                ex is not null || ctx.Response.StatusCode >= 500 ? LogEventLevel.Error
                : IsNoiseEndpoint(ctx.Request.Path) ? LogEventLevel.Verbose
                : LogEventLevel.Information;
        });

        // Only enabled when Cors__AllowedOrigins__0.. is set. A service with no
        // configured origins sends no CORS headers at all, which is the safe default.
        if (app.Services.GetRequiredService<CorsState>().IsEnabled)
        {
            app.UseCors(CorsPolicyName);
        }

        app.UseHttpMetrics();

        app.MapGet("/", (ServiceInfo info) => Results.Ok(new
        {
            service = info.Name,
            version = info.Version,
            environment = info.Environment,
            status = "running"
        }));

        app.MapHealthChecks("/health/live", new HealthCheckOptions
        {
            Predicate = registration => registration.Tags.Contains(LiveTag),
            ResponseWriter = WriteHealthResponseAsync
        });

        app.MapHealthChecks("/health/ready", new HealthCheckOptions
        {
            Predicate = registration => registration.Tags.Contains(ReadyTag),
            ResponseWriter = WriteHealthResponseAsync
        });

        app.MapHealthChecks("/health", new HealthCheckOptions
        {
            ResponseWriter = WriteHealthResponseAsync
        });

        app.MapMetrics();

        return app;
    }

    private static void ConfigureLogging(
        WebApplicationBuilder builder,
        string serviceName,
        string version,
        string environment)
    {
        // Containers get single-line JSON so a log shipper can parse it;
        // a developer running "dotnet run" gets something readable.
        var format = builder.Configuration["IncidentIQ:LogFormat"]
                     ?? (builder.Environment.IsDevelopment() ? "text" : "json");

        builder.Services.AddSerilog(loggerConfiguration =>
        {
            loggerConfiguration
                .MinimumLevel.Information()
                .MinimumLevel.Override("Microsoft.AspNetCore", LogEventLevel.Warning)
                .MinimumLevel.Override("Microsoft.Hosting.Lifetime", LogEventLevel.Information)
                .Enrich.FromLogContext()
                .Enrich.WithProperty("service", serviceName)
                .Enrich.WithProperty("version", version)
                .Enrich.WithProperty("environment", environment)
                // Anything under the "Serilog" configuration section (and therefore any
                // Serilog__* environment variable) overrides the defaults above.
                .ReadFrom.Configuration(builder.Configuration);

            if (string.Equals(format, "json", StringComparison.OrdinalIgnoreCase))
            {
                loggerConfiguration.WriteTo.Console(new CompactJsonFormatter());
            }
            else
            {
                loggerConfiguration.WriteTo.Console(
                    outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {service}: {Message:lj}{NewLine}{Exception}");
            }
        });
    }

    private static void ConfigureHealthChecks(WebApplicationBuilder builder, IncidentIqDefaultsOptions options)
    {
        var healthChecks = builder.Services.AddHealthChecks();

        // Liveness answers "is the process wedged?" and must never touch a
        // dependency - otherwise a database blip restarts every container.
        healthChecks.AddCheck("self", () => HealthCheckResult.Healthy("Process is running."), tags: [LiveTag]);

        if (options.CheckPostgres)
        {
            var connectionString = builder.Configuration.GetConnectionString("Postgres");
            healthChecks.Add(new HealthCheckRegistration(
                "postgres",
                _ => string.IsNullOrWhiteSpace(connectionString)
                    ? new NotConfiguredHealthCheck("ConnectionStrings__Postgres")
                    : new PostgresHealthCheck(connectionString),
                HealthStatus.Unhealthy,
                [ReadyTag],
                TimeSpan.FromSeconds(10)));
        }

        if (options.CheckKafka)
        {
            var bootstrapServers = builder.Configuration["Kafka:BootstrapServers"];
            healthChecks.Add(new HealthCheckRegistration(
                "kafka",
                _ => string.IsNullOrWhiteSpace(bootstrapServers)
                    ? new NotConfiguredHealthCheck("Kafka__BootstrapServers")
                    : new KafkaHealthCheck(bootstrapServers),
                HealthStatus.Unhealthy,
                [ReadyTag],
                TimeSpan.FromSeconds(10)));
        }
    }

    private static void ConfigureCors(WebApplicationBuilder builder)
    {
        var allowedOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [];
        builder.Services.AddSingleton(new CorsState(allowedOrigins.Length > 0));

        if (allowedOrigins.Length == 0)
        {
            return;
        }

        builder.Services.AddCors(corsOptions => corsOptions.AddPolicy(CorsPolicyName, policy => policy
            .WithOrigins(allowedOrigins)
            .AllowAnyHeader()
            .AllowAnyMethod()));
    }

    private static bool IsNoiseEndpoint(PathString path) =>
        path.StartsWithSegments("/health") || path.StartsWithSegments("/metrics");

    private static Task WriteHealthResponseAsync(HttpContext context, HealthReport report)
    {
        context.Response.ContentType = "application/json; charset=utf-8";

        var info = context.RequestServices.GetRequiredService<ServiceInfo>();
        var payload = new
        {
            status = report.Status.ToString(),
            service = info.Name,
            version = info.Version,
            environment = info.Environment,
            totalDurationMs = Math.Round(report.TotalDuration.TotalMilliseconds, 1),
            checks = report.Entries.Select(entry => new
            {
                name = entry.Key,
                status = entry.Value.Status.ToString(),
                description = entry.Value.Description,
                error = entry.Value.Exception?.Message,
                durationMs = Math.Round(entry.Value.Duration.TotalMilliseconds, 1)
            })
        };

        return context.Response.WriteAsync(JsonSerializer.Serialize(payload, HealthJsonOptions));
    }
}

/// <summary>
/// Whether any CORS origin was configured, so the pipeline can skip the
/// middleware entirely rather than registering an empty policy.
/// </summary>
internal sealed record CorsState(bool IsEnabled);

/// <summary>
/// Reports a missing setting as unhealthy instead of throwing at startup, so a
/// misconfigured container fails its readiness probe with a message that names
/// the environment variable to set.
/// </summary>
internal sealed class NotConfiguredHealthCheck(string settingName) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(HealthCheckResult.Unhealthy($"Not configured: set the '{settingName}' environment variable."));
}
