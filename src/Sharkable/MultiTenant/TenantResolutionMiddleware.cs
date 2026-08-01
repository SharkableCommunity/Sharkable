namespace Sharkable;

internal sealed class TenantResolutionMiddleware
{
    private readonly RequestDelegate _next;
    private readonly TenantOptions _options;

    public TenantResolutionMiddleware(RequestDelegate next)
    {
        _next = next;
        _options = Shark.SharkOption.TenantOptions!;
    }

    public async Task InvokeAsync(HttpContext context, ITenant tenant)
    {
        if (_options.ResolveTenant != null)
        {
            // Normalize: empty/whitespace tenant ids are indistinguishable from
            // "no tenant" — treat them as unresolved so tenant-guarded features
            // (e.g. AutoCrud tenant filter) reject the request instead of
            // silently querying without a tenant scope.
            var resolved = _options.ResolveTenant(context);
            tenant.TenantId = string.IsNullOrWhiteSpace(resolved) ? null : resolved;
        }
        await _next(context);
    }
}
