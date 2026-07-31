using System.Threading;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Sharkable;

internal static class HealthCheckEndpoint
{
    /// <summary>
    /// SHARK-SEC-M015: hard timeout for the aggregate <see cref="HealthCheckService"/>
    /// call. A single hung check (DB driver, downstream HTTP call) would otherwise
    /// keep /healthz open indefinitely and fail k8s probes. 10s matches the
    /// typical Kubernetes probe timeoutSeconds default; raise in
    /// <c>HealthCheckTimeoutSeconds</c> for deployments with deliberately slow
    /// checks (e.g. cross-region DB failover probes).
    /// </summary>
    private static readonly TimeSpan HealthCheckTimeout = TimeSpan.FromSeconds(10);

    internal static void Map(WebApplication app)
    {
        var path = Shark.SharkOption.HealthCheckPath ?? "/healthz";
        // AOT: write the response directly through HttpContext instead of
        // returning an IResult — the request delegate factory cannot compile
        // Task<IResult>/Results<,> handlers under NativeAOT. The explicit
        // JsonTypeInfo keeps serialization source-generated (AOT-safe).
        // Same shape as MapLiveness (single HttpContext parameter).
        app.MapGet(path, (HttpContext ctx) => HandleHealthzAsync(ctx))
            .ExcludeFromDescription();
    }

    private static async Task HandleHealthzAsync(HttpContext ctx)
    {
        var healthCheck = ctx.RequestServices.GetRequiredService<HealthCheckService>();
        var (response, statusCode) = await CheckAsync(healthCheck, ctx.RequestAborted);
        ctx.Response.StatusCode = statusCode;
        await ctx.Response.WriteAsJsonAsync(response, UnifiedResultSourceContext.Default.HealthCheckResponse);
    }

    private static async Task<(HealthCheckResponse Response, int StatusCode)> CheckAsync(
        HealthCheckService healthCheck, CancellationToken cancellationToken)
    {
        if (!Volatile.Read(ref InternalShark.StartupCompleted))
            return (new HealthCheckResponse(
                "unhealthy",
                new Dictionary<string, HealthCheckEntry>
                {
                    ["startup"] = new("unhealthy", "Startup not complete", null, null)
                },
                GetUptime(),
                InternalShark.AppVersion ?? "0.0.0"
            ), 503);

        if (Volatile.Read(ref InternalShark.IsShuttingDown))
            return (new HealthCheckResponse(
                "unhealthy",
                new Dictionary<string, HealthCheckEntry>
                {
                    ["shutdown"] = new("unhealthy", "Server is shutting down", null, null)
                },
                GetUptime(),
                InternalShark.AppVersion ?? "0.0.0"
            ), 503);

        // SHARK-SEC-M015: bound the aggregate health-check call with a
        // linked CTS so a stuck individual check cannot keep /healthz
        // open beyond the configured timeout. The CheckHealthAsync
        // overload that accepts a CancellationToken forwards cancellation
        // to each registered IHealthCheck.CheckHealthAsync.
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(HealthCheckTimeout);

        HealthReport report;
        try
        {
            report = await healthCheck.CheckHealthAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested
                                                   && !cancellationToken.IsCancellationRequested)
        {
            // Per-check timeout fired. Return a synthetic unhealthy report
            // so the probe can fail fast instead of hanging.
            return (new HealthCheckResponse(
                "unhealthy",
                new Dictionary<string, HealthCheckEntry>
                {
                    ["timeout"] = new("unhealthy",
                        $"Health check exceeded {HealthCheckTimeout.TotalSeconds:F0}s timeout",
                        null, null)
                },
                GetUptime(),
                InternalShark.AppVersion ?? "0.0.0"
            ), 503);
        }

        var detailLevel = Shark.SharkOption.HealthCheckDetailLevel;
        var checks = report.Entries.ToDictionary(
            e => e.Key,
            e =>
            {
                var description = detailLevel >= HealthCheckDetailLevel.Description ? e.Value.Description : null;
                var data = detailLevel >= HealthCheckDetailLevel.Full && e.Value.Data.Count > 0 ? e.Value.Data : null;
                var exceptionMessage = detailLevel >= HealthCheckDetailLevel.Full ? e.Value.Exception?.Message : null;
                return new HealthCheckEntry(
                    e.Value.Status.ToString().ToLower(),
                    description,
                    data,
                    exceptionMessage
                );
            });

        var overall = report.Status switch
        {
            HealthStatus.Healthy => "healthy",
            HealthStatus.Degraded => "degraded",
            _ => "unhealthy"
        };

        var statusCode = report.Status == HealthStatus.Healthy ? 200
            : report.Status == HealthStatus.Degraded ? 200 : 503;

        return (new HealthCheckResponse(
            overall, checks, GetUptime(), InternalShark.AppVersion ?? "0.0.0"
        ), statusCode);
    }

    /// <summary>
    /// Maps the liveness probe endpoint at <c>/livez</c>.
    /// Always returns 200 <c>{"status":"alive"}</c> regardless of health check state.
    /// Unlike <c>/healthz</c>, this endpoint is NOT blocked by the readiness gate
    /// or graceful shutdown — it is intended for platform-level liveness checks
    /// (e.g. Kubernetes <c>livenessProbe</c>) to distinguish a hung process from
    /// a merely unhealthy one.
    /// </summary>
    internal static void MapLiveness(WebApplication app)
    {
        // AOT: direct HttpContext write (see Map above).
        app.MapGet("/livez", async (HttpContext ctx) =>
        {
            ctx.Response.StatusCode = 200;
            await ctx.Response.WriteAsJsonAsync(new LivenessResponse(), UnifiedResultSourceContext.Default.LivenessResponse);
        }).ExcludeFromDescription();
    }

    private static string GetUptime()
    {
        if (InternalShark.StartedAt == default)
            return "00:00:00";
        var span = (DateTimeOffset.UtcNow - InternalShark.StartedAt);
        return $"{(int)span.TotalHours:D2}:{span.Minutes:D2}:{span.Seconds:D2}";
    }

    /// <summary>Liveness probe response body (AOT-safe named type).</summary>
    internal sealed class LivenessResponse
    {
        public string status { get; set; } = "alive";
    }
}
