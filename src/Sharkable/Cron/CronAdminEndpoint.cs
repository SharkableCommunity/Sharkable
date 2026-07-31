using Microsoft.AspNetCore.Http;

namespace Sharkable;

internal static class CronAdminEndpoint
{
    private const int MaxLastErrorChars = 100;

    internal static void Map(IEndpointRouteBuilder app)
    {
        // AOT: write the response directly through HttpContext instead of
        // returning an IResult — the request delegate factory cannot compile
        // Task<IResult>/Results<,> handlers under NativeAOT. The explicit
        // JsonTypeInfo keeps serialization source-generated (AOT-safe).
        app.MapGet("/_sharkable/jobs", async (HttpContext context, ICronScheduler scheduler, ApiKeyValidator validator) =>
        {
            if (Shark.SharkOption.CronAdminRequireApiKey && !IsApiKeyAuthorized(context, validator))
            {
                context.Response.StatusCode = 404;
                return;
            }

            var list = await scheduler.ListAsync();
            await context.Response.WriteAsJsonAsync(
                list.Select(CronJobView.From),
                UnifiedResultSourceContext.Default.IEnumerableCronJobView);
        }).ExcludeFromDescription();
    }

    /// <summary>AOT-safe named projection of <see cref="CronJobState"/>.</summary>
    internal sealed record CronJobView(
        string Name,
        string? Description,
        string Cron,
        bool IsRunning,
        DateTimeOffset? NextRun,
        DateTimeOffset? LastRun,
        long? LastDurationMs,
        string? LastError,
        long RunCount,
        bool Paused)
    {
        public static CronJobView From(CronJobState s) => new(
            s.Name,
            s.Description,
            s.Cron,
            s.IsRunning,
            s.NextRun,
            s.LastRun,
            s.LastDurationMs,
            TruncateLastError(s.LastError),
            s.RunCount,
            s.Paused);
    }

    private static string? TruncateLastError(string? value)
    {
        if (value == null) return null;
        if (value.Length <= MaxLastErrorChars) return value;

        var cut = MaxLastErrorChars;
        if (char.IsHighSurrogate(value[cut - 1])) cut--;
        return value.Substring(0, cut) + "...";
    }

    private static bool IsApiKeyAuthorized(HttpContext context, ApiKeyValidator validator)
    {
        if (!validator.HasConfiguredKeys)
            return false;

        if (!context.Request.Headers.TryGetValue(Shark.SharkOption.ApiKeyHeaderName, out var provided))
            return false;

        return validator.Validate(provided.ToString());
    }
}
