using IncidentIQ.Persistence;
using IncidentIQ.Shared.Auth;

namespace IncidentIQ.Api;

/// <summary>
/// Copies the authenticated organization onto the ambient tenant context that
/// EF's global query filters read.
///
/// Authentication establishes the tenant on <see cref="HttpContext.Items"/>,
/// which is where request-scoped code and the rate limiter look for it.
/// Persistence has its own <c>ITenantContext</c>, and the two are deliberately
/// not the same type: the shared authentication library must not depend on the
/// persistence library, or every service that authenticates would drag in
/// Entity Framework.
///
/// This middleware is the one place that bridges them, and it lives in the API
/// because the API is the only host where both concerns meet.
///
/// Without it the filters compare against null, every query matches nothing,
/// and the failure looks like an empty database rather than a wiring mistake -
/// which is the safe direction to fail, but still a bug.
/// </summary>
public sealed class TenantScopeMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context)
    {
        // Resolved optionally, not injected. With no connection string
        // configured, persistence is never registered - and the service must
        // still start and serve /health so the readiness probe can report
        // *why* it is not ready. A hard dependency here would turn a
        // misconfiguration into a container that cannot even explain itself.
        var tenantContext = context.RequestServices.GetService<AmbientTenantContext>();
        var tenant = context.GetTenantContext();

        if (tenantContext is not null && tenant is not null)
        {
            tenantContext.SetOrganization(tenant.TenantId);
        }

        await next(context);
    }
}
