using System.Text.Json.Serialization;
using IncidentIQ.Persistence;
using IncidentIQ.Shared.Auth;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace IncidentIQ.Api.Endpoints;

/// <summary>
/// Who the caller is, as far as this service is concerned.
///
/// Exists because the dashboard was asserting an identity instead of reading
/// one: the sidebar had an organization name and a person's name written into
/// the JSX. Both were wrong for anybody but the developer who typed them, and
/// the second was wrong for him too - actions are attributed to whichever user
/// the API key is bound to, so the page named one person while the timeline
/// recorded another.
///
/// There is no login, so this is not a session in the usual sense. It reports
/// what the key resolves to, which is the only identity the system actually
/// has, and says so plainly when the key is bound to no user at all - the case
/// where every action endpoint returns 403.
/// </summary>
public static class SessionEndpoints
{
    public static IEndpointRouteBuilder MapSessionEndpoints(this IEndpointRouteBuilder routes)
    {
        routes.MapGet("/api/v1/me", GetAsync).WithName("GetCurrentSession").WithTags("session");

        return routes;
    }

    private static async Task<IResult> GetAsync(
        [FromServices] IncidentIQDbContext db,
        HttpContext http,
        CancellationToken cancellationToken)
    {
        var tenant = http.GetTenantContext();

        if (tenant is null)
        {
            return Results.Problem(
                title: "Not authenticated",
                detail: "The request carried no usable API key.",
                statusCode: StatusCodes.Status401Unauthorized);
        }

        var organization = await db.Organizations.AsNoTracking()
            .Select(o => new OrganizationSummary { Id = o.Id, Name = o.Name, Slug = o.Slug })
            .FirstOrDefaultAsync(cancellationToken);

        // Null when the key is not bound to a user. Reported rather than
        // hidden: it is the difference between a dashboard that can act and
        // one that can only read, and the UI should be able to say which.
        var actor = tenant.ActorUserId is null
            ? null
            : await db.Users.AsNoTracking()
                .Where(u => u.Id == tenant.ActorUserId)
                .Select(u => new ActorSummary
                {
                    UserId = u.Id,
                    DisplayName = u.DisplayName,
                    Email = u.Email
                })
                .FirstOrDefaultAsync(cancellationToken);

        return Results.Ok(new CurrentSession
        {
            Organization = organization,
            Actor = actor,
            ApiKeyName = tenant.ApiKeyName
        });
    }
}

public sealed record OrganizationSummary
{
    [JsonPropertyName("id")]
    public required Guid Id { get; init; }

    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("slug")]
    public required string Slug { get; init; }
}

public sealed record ActorSummary
{
    [JsonPropertyName("userId")]
    public required Guid UserId { get; init; }

    [JsonPropertyName("displayName")]
    public required string DisplayName { get; init; }

    [JsonPropertyName("email")]
    public required string Email { get; init; }
}

public sealed record CurrentSession
{
    /// <summary>Null only if the tenant row has been deleted under a live key.</summary>
    [JsonPropertyName("organization")]
    public OrganizationSummary? Organization { get; init; }

    /// <summary>The person actions are recorded against. Null when the key is bound to none.</summary>
    [JsonPropertyName("actor")]
    public ActorSummary? Actor { get; init; }

    /// <summary>The key's own name, which is what identifies the caller when no user is bound.</summary>
    [JsonPropertyName("apiKeyName")]
    public required string ApiKeyName { get; init; }
}
