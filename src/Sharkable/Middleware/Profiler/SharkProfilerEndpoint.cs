using Microsoft.Extensions.DependencyInjection;

namespace Sharkable;

internal static class SharkProfilerEndpoint
{
    /// <summary>
    /// Upper bound on the <c>top-N</c> slow requests surfaceable from the
    /// profiler endpoint. Caps accidental or hostile large <c>top</c> values.
    /// </summary>
    private const int MaxTopAllowed = 50;

    internal static void MapProfilerEndpoint(this IEndpointRouteBuilder app)
    {
        var endpointPath = Shark.SharkOption.ProfilerOptions?.Endpoint ?? "/_sharkable/profiler";
        // Strip leading slash — MapGet pattern is relative to group
        var pattern = endpointPath.TrimStart('/');

        // BUG-139: reuse the shared ApiKeyValidator (cached constant-time
        // hashes, hot-reload aware) instead of re-hashing keys per request.
        var apiKeyValidator = app.ServiceProvider?.GetService<ApiKeyValidator>();

        app.MapGet(pattern, async (HttpContext context) =>
        {
            // SHARK-SEC-015: gate on API key by default. Fail-closed — if no
            // keys are configured, the endpoint reports 404 so its existence
            // is not leaked to unauthenticated probes.
            if (Shark.SharkOption.ProfilerRequireApiKey && !IsApiKeyAuthorized(context, apiKeyValidator))
            {
                context.Response.StatusCode = 404;
                return;
            }

            var uptime = DateTimeOffset.UtcNow - ProfilerStore.StartedAt;
            var avgMs = ProfilerStore.RequestCount > 0
                ? ProfilerStore.TotalElapsedMs / (double)ProfilerStore.RequestCount
                : 0;
            var configuredTop = Shark.SharkOption.ProfilerOptions?.TopSlowRequests ?? 20;
            var top = Math.Clamp(configuredTop, 1, MaxTopAllowed);
            var slow = ProfilerStore.SnapTopSlow(top);

            // AOT: direct HttpContext write with an explicit JsonTypeInfo —
            // returning an IResult (or anonymous type) cannot be compiled by
            // the request delegate factory under NativeAOT.
            await context.Response.WriteAsJsonAsync(
                new ProfilerSnapshot(
                    $"{(int)uptime.TotalHours:D2}:{uptime.Minutes:D2}:{uptime.Seconds:D2}",
                    ProfilerStore.RequestCount,
                    Math.Round(avgMs, 1),
                    slow.Select(e => new ProfilerSlowEntry(
                        e.Method, e.Path, e.StatusCode, e.ElapsedMs, e.MemoryDelta,
                        e.Timestamp.ToString("O")))),
                UnifiedResultSourceContext.Default.ProfilerSnapshot);
        }).ExcludeFromDescription();
    }

    /// <summary>AOT-safe profiler snapshot payload.</summary>
    internal sealed record ProfilerSnapshot(string Uptime, long TotalRequests, double AvgLatencyMs, IEnumerable<ProfilerSlowEntry> TopSlow);

    /// <summary>A single slow-request entry in the profiler snapshot.</summary>
    internal sealed record ProfilerSlowEntry(string Method, string Path, int StatusCode, long ElapsedMs, long MemoryDeltaBytes, string At);

    /// <summary>
    /// Returns <c>true</c> when the request carries a configured API key.
    /// Delegates to the shared <see cref="ApiKeyValidator"/> (constant-time
    /// SHA-256 comparison with cached hashes, SHARK-SEC-008); falls back to a
    /// direct comparison when the validator is unavailable. Returns
    /// <c>false</c> — and the caller maps that to <c>404</c> — when no keys
    /// are configured at all so the endpoint's existence is not advertised.
    /// </summary>
    private static bool IsApiKeyAuthorized(HttpContext context, ApiKeyValidator? validator)
    {
        var keys = Shark.SharkOption.ApiKeys;
        if (keys == null || keys.Length == 0)
            return false;

        if (!context.Request.Headers.TryGetValue(Shark.SharkOption.ApiKeyHeaderName, out var provided))
            return false;

        if (validator != null)
            return validator.Validate(provided.ToString());

        var candidateHash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(provided.ToString()));
        var matched = false;
        for (var i = 0; i < keys.Length; i++)
        {
            var stored = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(keys[i]));
            if (System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(candidateHash, stored))
                matched = true;
        }
        return matched;
    }
}
