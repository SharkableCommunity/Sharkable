using System.Runtime.CompilerServices;
using Microsoft.Extensions.Hosting;
using System.Threading;
using System.Threading.Tasks;

namespace  Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Entry points for registering Sharkable services and wiring the application pipeline.
/// </summary>
public static class SharkableExtension
{
    /// <summary>
    /// Registers Sharkable services with automatic assembly discovery.
    /// </summary>
    /// <param name="services">The <see cref="IServiceCollection"/> to add services to.</param>
    /// <param name="setupOption">Optional callback to configure <see cref="SharkOption"/>.</param>
    [RequiresDynamicCode("use Assembly[] method instead")]
    public static void AddShark(this IServiceCollection services, Action<SharkOption>? setupOption = null)
    {
        //set aot mode to false if use this method
        InternalShark.AotMode = false;
        //get assemblies
        Shark.SetAssebly(Utils.GetAssemblies());
        //set common extensions
        services.AddCommon(setupOption);
    }

    /// <summary>
    /// Registers Sharkable services with explicit assemblies. Recommended for AOT scenarios.
    /// </summary>
    /// <param name="services">The <see cref="IServiceCollection"/> to add services to.</param>
    /// <param name="assembly">Assemblies to scan — required in AOT mode.</param>
    /// <param name="setupOption">Optional callback to configure <see cref="SharkOption"/>.</param>
    public static void AddShark(this IServiceCollection services, Assembly[]? assembly, Action<SharkOption>? setupOption = null)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        //set aot mode to true if use this method
        InternalShark.AotMode = true;
        //get assemblies
        Shark.SetAssebly(Utils.GetAssemblies(assembly));
        //set common extensions
        services.AddCommon(setupOption);
    }

    /// <summary>
    /// Wires Sharkable middleware into the application pipeline — exception handler, swagger, and endpoint mapping.
    /// Must be called after <c>builder.Build()</c> and before <c>app.Run()</c>.
    /// </summary>
    /// <param name="app">The <see cref="WebApplication"/> to configure.</param>
    /// <param name="setupOption">Optional callback to configure <see cref="UseSharkOptions"/>.</param>
    public static void UseShark(this WebApplication app, Action<UseSharkOptions>? setupOption = null)
    {
        // Apply environment-sensitive defaults now that the host is built
        if (app.Environment.IsDevelopment())
            Shark.SharkOption.HealthCheckDetailLevel = HealthCheckDetailLevel.Full;

        app.UseCommon(setupOption);

        // distributed tracing — must be first in pipeline to capture full request
        if (Shark.SharkOption.TracingOptions != null)
            app.UseMiddleware<TracingMiddleware>();

        // response compression — early for max coverage
        if (Shark.SharkOption.EnableResponseCompression)
            app.UseResponseCompression();

        // security headers
        if (Shark.SharkOption.SecurityHeadersOptions != null)
            app.UseMiddleware<SecurityHeadersMiddleware>();

        // request timeouts
        if (Shark.SharkOption.RequestTimeoutsConfigure != null)
            app.UseRequestTimeouts();

        // graceful shutdown — must be early in pipeline to reject new requests
        var gsOptions = Shark.SharkOption.GracefulShutdownOptions;
        var auditOptions = Shark.SharkOption.AuditTrailOptions;
        if (gsOptions != null || auditOptions?.EnsureFlushOnShutdown == true)
        {
            var lifetime = app.Services.GetRequiredService<IHostApplicationLifetime>();

            lifetime.ApplicationStopping.Register(() =>
            {
                if (gsOptions != null)
                {
                    Volatile.Write(ref InternalShark.IsShuttingDown, true);
                }

                // Fire-and-forget: returning sync from this callback is required so
                // ApplicationStopping can continue. We deliberately do NOT await
                // (and do NOT call .GetAwaiter().GetResult()) — that would still block
                // the shutdown thread. The k8s grace period covers in-flight requests.
                _ = Task.Run(async () =>
                {
                    try
                    {
                        if (gsOptions != null)
                        {
                            using var drainCts = new CancellationTokenSource(gsOptions.DrainTimeout);
                            var pollingMs = Math.Max(
                                (int)gsOptions.DrainPollingInterval.TotalMilliseconds, 10);

                            while (!drainCts.IsCancellationRequested)
                            {
                                if (Volatile.Read(ref InternalShark.ActiveRequests) == 0)
                                    break;
                                await Task.Delay(pollingMs, drainCts.Token);
                            }
                        }
                    }
                    catch (OperationCanceledException)
                    {
                    }
                    catch (Exception)
                    {
                    }

                    InternalShark.AuditLogBuffer?.FlushRemaining();
                    InternalShark.AuditLogBuffer?.Dispose();
                });
            });

            if (gsOptions != null)
                app.UseMiddleware<GracefulShutdownMiddleware>();
        }

        // multi-tenant
        if (Shark.SharkOption.TenantOptions?.ResolveTenant != null)
            app.UseMiddleware<TenantResolutionMiddleware>();

        // output cache
        if (Shark.SharkOption.OutputCacheConfigure != null)
            app.UseOutputCache();
        // CORS
        if (Shark.SharkOption.CorsConfigure != null)
            app.UseCors();

        // pipeline injection: before auth
        var useOpts = Shark.UseSharkOptions;

        // Plugin pipeline hooks (after framework middleware, before auth)
        if (Shark.SharkOption.DiscoveredPlugins is { Count: > 0 })
        {
            foreach (var plugin in Shark.SharkOption.DiscoveredPlugins)
            {
                try
                {
                    plugin.ConfigurePipeline(app, Shark.SharkOption);
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[Sharkable] Plugin '{plugin.Name}' ConfigurePipeline threw: {ex.Message}");
                }
            }
        }

        if (useOpts != null)
        {
            foreach (var action in useOpts.BeforeAuthActions)
                action(app);
        }

        // Auth
        if (Shark.SharkOption.JwtAuthority != null)
            app.UseAuthentication();
        if (Shark.SharkOption.EnableAuthorization)
            app.UseAuthorization();

        // pipeline injection: after auth
        if (useOpts != null)
        {
            foreach (var action in useOpts.AfterAuthActions)
                action(app);
        }

        // audit trail — registered BEFORE the rate limiter so rejected (429)
        // requests are still audited (BUG-111).
        if (Shark.SharkOption.AuditTrailOptions != null)
            app.UseMiddleware<AuditTrailMiddleware>();

        // rate limiter — registered AFTER authentication so the per-user key
        // generator branch can see the authenticated identity (BUG-110).
        if (Shark.SharkOption.RateLimiterConfigure != null)
            app.UseRateLimiter();
        if (Shark.SharkOption.RateLimitingOptions != null)
            app.UseMiddleware<SharkRateLimiterMiddleware>();

        // idempotency — register when global enable is on OR per-endpoint opt-in
        // is configured (ConfigureIdempotency called). The middleware checks
        // endpoint metadata for [SharkIdempotent] / [SharkNoIdempotency].
        if (Shark.SharkOption.EnableIdempotency || Shark.SharkOption.IdempotencyOptions != null)
            app.UseMiddleware<SharkIdempotencyMiddleware>();

        // ETag / 304 conditional responses
        if (Shark.SharkOption.EnableETag)
            app.UseMiddleware<ETagMiddleware>();

        // profiler — wraps endpoints to record latency/memory
        if (Shark.SharkOption.ProfilerOptions != null)
        {
            app.UseMiddleware<ProfilerMiddleware>();
            app.MapProfilerEndpoint();
        }

        // cron jobs admin endpoint
        if (Shark.SharkOption.ConfigureCronJobs != null)
            CronAdminEndpoint.Map(app);

        // exception handler — must be last middleware (just before endpoints)
        // so it catches exceptions from ALL preceding middleware
        if (Shark.UseSharkOptions?.EnableExceptionHandler ?? true)
        {
            Shark.SharkOption.ExceptionHandlerOptions.IsDevelopment = app.Environment.IsDevelopment();
            app.UseSharkExceptionHandler();
        }

        app.MapEndpoints();

        // pipeline injection: after endpoints
        if (useOpts != null)
        {
            foreach (var action in useOpts.AfterEndpointsActions)
                action(app);
        }

        // eager singleton activation
        foreach (var type in InternalShark.EagerSingletonTypes)
            app.Services.GetRequiredService(type);

        // DI validation
        foreach (var type in Shark.SharkOption.ValidateOnStartTypes)
            app.Services.GetRequiredService(type);

        // warmup — resolve all registered warmup services and run in parallel
        if (Shark.SharkOption.WarmupServiceTypes.Count > 0)
        {
            var warmupTypes = Shark.SharkOption.WarmupServiceTypes;
            var warmupTimeout = Shark.SharkOption.WarmupTimeout;
            var tasks = new List<Task>(warmupTypes.Count);
            var ctsList = new List<CancellationTokenSource>(warmupTypes.Count);

            foreach (var warmupType in warmupTypes)
            {
                var warmup = (IWarmupService)app.Services.GetRequiredService(warmupType);
                var warmupCts = new CancellationTokenSource(warmupTimeout);
                ctsList.Add(warmupCts);
                tasks.Add(Task.Run(() => warmup.WarmupAsync(warmupCts.Token), warmupCts.Token));
            }

            try
            {
                var completed = Task.WaitAll([.. tasks], warmupTimeout);
                if (!completed)
                    throw new TimeoutException(
                        $"Warmup did not complete within the configured timeout ({warmupTimeout}).");
            }
            catch (AggregateException ex)
            {
                // BUG-117: rethrow the root cause instead of the wrapper, and
                // let the finally block cancel the sibling tasks.
                throw ex.InnerException ?? ex;
            }
            finally
            {
                // BUG-117: disposing a CTS does NOT signal cancellation — it
                // only kills the auto-cancel timer. Cancel explicitly so
                // sibling warmup tasks are actually stopped and cannot keep
                // running unobserved (or throw ObjectDisposedException from
                // later token.Register calls).
                foreach (var cts in ctsList)
                {
                    try
                    {
                        cts.Cancel();
                    }
                    catch (ObjectDisposedException)
                    {
                    }
                }

                // Give the cancelled tasks a bounded moment to unwind, then
                // observe any faults so nothing is left unobserved.
                try
                {
                    Task.WaitAll([.. tasks], TimeSpan.FromSeconds(5));
                }
                catch (AggregateException)
                {
                    // Expected for tasks cancelled by the timeout path.
                }

                foreach (var cts in ctsList)
                    cts.Dispose();
            }
        }
        else if (Shark.SharkOption.WarmupServiceType != null)
        {
            var warmup = (IWarmupService)app.Services.GetRequiredService(Shark.SharkOption.WarmupServiceType);
            using var warmupCts = new CancellationTokenSource(Shark.SharkOption.WarmupTimeout);
            warmup.WarmupAsync(warmupCts.Token).GetAwaiter().GetResult();
        }

        // open readiness gate
        InternalShark.StartupCompleted = true;

        // startup banner
        if (Shark.SharkOption.ShowStartupBanner)
            PrintStartupBanner(app);

        // lifecycle hooks (#20)
        var appLifetime = app.Services.GetRequiredService<IHostApplicationLifetime>();
        if (Shark.SharkOption.OnStartedAction != null)
            appLifetime.ApplicationStarted.Register(() => Shark.SharkOption.OnStartedAction(app.Services));
        if (Shark.SharkOption.OnStoppedAction != null)
        {
            // Chain onto the existing stopping registration (graceful shutdown already
            // registers one above). Multiple Register calls are additive.
            appLifetime.ApplicationStopping.Register(() => Shark.SharkOption.OnStoppedAction(app.Services));
        }
    }

    private static void PrintStartupBanner(WebApplication app)
    {
        var version = InternalShark.AppVersion ?? "0.0.0";
        var env = app.Environment.EnvironmentName;
        var now = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd HH:mm:ss");

        Console.WriteLine();
        Console.WriteLine($"╔══════════════════════════════════════════════╗");
        Console.WriteLine($"║             Sharkable v{version,-20}║");
        Console.WriteLine($"║             Environment: {env,-14}║");
        Console.WriteLine($"║             Started at:  {now,-14}║");
        Console.WriteLine($"╚══════════════════════════════════════════════╝");
        Console.WriteLine();
    }
}
