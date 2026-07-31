using System.Security.Claims;

namespace Sharkable;

/// <summary>
/// Static helper methods for resolving a tenant identifier from common sources.
/// Use with <see cref="TenantOptions.ResolveTenant"/>.
/// </summary>
public static class TenantResolver
{
    /// <summary>
    /// Resolves tenant from the first subdomain segment of the host.
    /// Example: <c>tenant1.myapp.com</c> → <c>"tenant1"</c>.
    /// Returns <c>null</c> if the host has no subdomain (apex domains such as
    /// <c>myapp.com</c> are never treated as tenants — BUG-127).
    /// <para>
    /// SHARK-SEC-L007: when <see cref="TenantOptions.AllowedHosts"/> is set,
    /// the inbound <c>Host</c> header is validated against the allowlist.
    /// Mismatched hosts return <c>null</c> so an attacker cannot spoof the
    /// tenant by sending <c>Host: victim-tenant.app.example</c>.
    /// </para>
    /// </summary>
    public static string? FromHost(HttpContext context)
    {
        var host = context.Request.Host.Host;
        if (string.IsNullOrEmpty(host))
            return null;

        var allowed = Shark.SharkOption.TenantOptions?.AllowedHosts;
        if (allowed is { Length: > 0 })
        {
            var matched = false;
            for (var i = 0; i < allowed.Length; i++)
            {
                if (string.Equals(allowed[i], host, StringComparison.OrdinalIgnoreCase))
                {
                    matched = true;
                    break;
                }
            }
            if (!matched) return null;
        }

        // BUG-127: require a real subdomain — a bare apex host such as
        // "myapp.com" must not be treated as the tenant "myapp" (which would
        // route apex requests into tenant-scoped services with a fabricated
        // tenant id). A host with at least two dots (e.g. "t1.myapp.com")
        // yields the first label.
        var dotIndex = host.IndexOf('.');
        if (dotIndex <= 0)
            return null;
        var secondDot = host.IndexOf('.', dotIndex + 1);
        return secondDot > 0 ? host[..dotIndex] : null;
    }

    /// <summary>
    /// Resolves tenant from a JWT claim on the current user.
    /// Returns <c>null</c> if the claim is not present or the user is unauthenticated.
    /// </summary>
    /// <param name="context">The current HTTP context.</param>
    /// <param name="claimType">The claim type to read. Default is <c>"tenant_id"</c>.</param>
    public static string? FromClaim(HttpContext context, string claimType = "tenant_id")
    {
        return context.User.FindFirstValue(claimType);
    }
}
