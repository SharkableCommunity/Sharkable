namespace Sharkable;

internal sealed class SharkCacheProfileFilter : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var result = await next(context);

        var endpoint = context.HttpContext.GetEndpoint();
        if (endpoint == null)
            return result;

        var profile = endpoint.Metadata.GetMetadata<SharkCacheProfileAttribute>();
        if (profile == null)
            return result;

        // BUG-114: mutating headers on an already-started response throws
        // InvalidOperationException (e.g. streamed/large responses) — skip.
        var response = context.HttpContext.Response;
        if (response.HasStarted)
            return result;

        var cacheControl = profile.PrivateOnly ? "private" : "public";
        cacheControl += $", max-age={profile.DurationSeconds}";

        if (profile.ExtraDirectives != null)
            cacheControl += $", {profile.ExtraDirectives}";

        // BUG-114: do not overwrite a Cache-Control the endpoint already set
        // (e.g. no-store on sensitive endpoints).
        if (!response.Headers.ContainsKey("Cache-Control"))
            response.Headers["Cache-Control"] = cacheControl;

        if (profile.VaryByHeader != null)
            response.Headers["Vary"] = profile.VaryByHeader;

        return result;
    }
}
