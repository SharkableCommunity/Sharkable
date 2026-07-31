using System.IdentityModel.Tokens.Jwt;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace Sharkable;

/// <summary>Extension methods on <c>IServiceCollection</c> and <c>IApplicationBuilder</c> for configuring Sharkable.</summary>
public static class SharkExtension
{
    /// <summary>
    /// common service collection extensions for sharkable
    /// </summary>
    /// <param name="services"></param>
    /// <param name="setupOptions"></param>
    internal static void AddCommon(this IServiceCollection services, Action<SharkOption>? setupOptions = null)
    {
        services.AddJsonContext();
        services.AddDiFactory();
        //reset and invoke options
        Shark.SharkOption = new SharkOption();
        setupOptions?.Invoke(Shark.SharkOption);
        // BUG-126: the previous services.Configure<SharkOption> re-invoked the
        // user's setup callback on a second, divergent options instance. All
        // framework reads now go through the single static Shark.SharkOption;
        // the Configure registration is removed so side-effectful setup
        // callbacks run exactly once and there is no second source of truth.
        //wire endpoints
        services.WireSharkEndpoint();
        //wire service lifetime
        services.AddServicesWithAttributeOfTypeFromAssembly(Shark.Assemblies);
        //discover and configure plugins
        DiscoverAndConfigurePlugins(services);
        //setup OpenAPI document generation
        services.AddSharkOpenApi();
        //register fluent validation
        services.AddValidators();
        //setup auto crud services
        services.AddAutoCrud();
        //register rate limiter
        if (Shark.SharkOption.RateLimiterConfigure != null)
            services.AddRateLimiter(Shark.SharkOption.RateLimiterConfigure);
        if (Shark.SharkOption.OutputCacheConfigure != null)
            services.AddOutputCache(Shark.SharkOption.OutputCacheConfigure);
        //register health checks
        if (Shark.SharkOption.EnableHealthChecks)
        {
            var hc = services.AddHealthChecks();
            Shark.SharkOption.HealthChecksConfigure?.Invoke(hc);

            // auto-check JWT authority if configured
            if (Shark.SharkOption.JwtAuthority != null)
                hc.AddCheck<JwtHealthCheck>("jwt");
        }
        //register response compression
        if (Shark.SharkOption.EnableResponseCompression)
            services.AddResponseCompression();
        //register request timeouts
        if (Shark.SharkOption.RequestTimeoutsConfigure != null)
            services.AddRequestTimeouts(Shark.SharkOption.RequestTimeoutsConfigure);
        //register CORS
        if (Shark.SharkOption.CorsConfigure != null)
            services.AddCors(Shark.SharkOption.CorsConfigure);
        //register API key validator (shared across filter, cron admin, profiler gates)
        services.TryAddSingleton<ApiKeyValidator>();
        services.TryAddSingleton<IApiKeyValidator, DefaultApiKeyValidator>();
        if (Shark.SharkOption.EnableIdempotency || Shark.SharkOption.IdempotencyOptions != null)
        {
            var idempotencyOptions = Shark.SharkOption.IdempotencyOptions ?? new SharkIdempotencyOptions();
            services.AddSingleton(idempotencyOptions);
            if (Shark.SharkOption.IdempotencyStoreFactory != null)
                services.AddSingleton(Shark.SharkOption.IdempotencyStoreFactory);
            else
                services.TryAddSingleton<IIdempotencyStore, MemoryIdempotencyStore>();
        }
        //register distributed rate limiter
        var rateLimitMaxEntries = Shark.SharkOption.RateLimitingOptions?.MaxEntries ?? 100_000;
        if (Shark.SharkOption.RateLimitingOptions != null)
        {
            services.AddSingleton(Shark.SharkOption.RateLimitingOptions);
            // BUG-136: register via factory so MS.DI owns the singleton and
            // disposes it (Timer/Process handle) with the container — an
            // instance registration is never disposed.
            if (Shark.SharkOption.RateLimitingOptions.EnableAdaptive)
                services.AddSingleton(sp => new AdaptiveLimitMonitor(Shark.SharkOption.RateLimitingOptions, autoStart: true));
            if (Shark.SharkOption.RateLimitStoreFactory != null)
                services.AddSingleton(Shark.SharkOption.RateLimitStoreFactory);
            else
                services.TryAddSingleton<IDistributedRateLimitStore>(_ => new MemoryRateLimitStore(rateLimitMaxEntries));
        }
        else
        {
            // Always register the store interface so plugins can provide their own.
            // TryAddSingleton ensures a previously-registered plugin implementation wins.
            if (Shark.SharkOption.RateLimitStoreFactory != null)
                services.AddSingleton(Shark.SharkOption.RateLimitStoreFactory);
            else
                services.TryAddSingleton<IDistributedRateLimitStore>(_ => new MemoryRateLimitStore(rateLimitMaxEntries));
        }
        //register authorization (default enabled)
        if (Shark.SharkOption.EnableAuthorization)
        {
            if (Shark.SharkOption.ConfigureAuthorizationInternal != null)
                services.AddAuthorization(Shark.SharkOption.ConfigureAuthorizationInternal);
            else
                services.AddAuthorization();
        }
        //register JWT auth
        if (Shark.SharkOption.JwtAuthority != null)
        {
            services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
                .AddJwtBearer(opt =>
                {
                    opt.Authority = Shark.SharkOption.JwtAuthority;

                    // SHARK-SEC-M019: invoke the user's JwtConfigure callback
                    // BEFORE we install framework defaults. The previous order
                    // installed the framework TokenValidationParameters first,
                    // then called the user callback — which meant any user code
                    // that replaced opt.TokenValidationParameters (e.g. to add a
                    // custom IssuerValidator) silently wiped the framework's
                    // security defaults (algorithm allowlist, RequireSignedTokens,
                    // ClockSkew=30s, etc.). Now the user callback can mutate the
                    // properties in place (the documented contract) or replace
                    // the instance; in either case we apply framework safety
                    // properties on the final instance below.
                    Shark.SharkOption.JwtConfigure?.Invoke(opt);

                    opt.TokenValidationParameters ??= new TokenValidationParameters();

                    var tvp = opt.TokenValidationParameters;
                    tvp.ValidateIssuer = true;
                    // SHARK-SEC-007 follow-up: ConfigureJwt now guarantees a non-empty
                    // audience list, so always validate audience. Tokens with any other
                    // audience are rejected. (Defense-in-depth: ConfigurationValidator
                    // and the Create call site both fail closed if the list is empty.)
                    tvp.ValidateAudience = true;
                    tvp.ValidateIssuerSigningKey = true;
                    tvp.ValidateLifetime = true;
                    tvp.RequireSignedTokens = true;
                    tvp.RequireExpirationTime = true;
                    // SHARK-SEC-007: allowlist signing algorithms to prevent algorithm-confusion attacks
                    // (e.g. an attacker swapping RS256 for HS256 and signing with the public key).
                    // Only apply the default allowlist if the user has not already configured one —
                    // honor explicit opt-outs.
                    if (tvp.ValidAlgorithms == null || !tvp.ValidAlgorithms.Any())
                    {
                        tvp.ValidAlgorithms = new[]
                        {
                            SecurityAlgorithms.RsaSha256,
                            SecurityAlgorithms.RsaSha384,
                            SecurityAlgorithms.RsaSha512,
                            SecurityAlgorithms.RsaSsaPssSha256,
                            SecurityAlgorithms.RsaSsaPssSha384,
                            SecurityAlgorithms.RsaSsaPssSha512,
                            SecurityAlgorithms.EcdsaSha256,
                            SecurityAlgorithms.EcdsaSha384,
                            SecurityAlgorithms.EcdsaSha512,
                            SecurityAlgorithms.HmacSha256,
                            SecurityAlgorithms.HmacSha384,
                            SecurityAlgorithms.HmacSha512,
                        };
                    }
                    tvp.ValidAudiences = Shark.SharkOption.JwtAudiences!;
                    tvp.NameClaimType = JwtRegisteredClaimNames.Sub;
                    // SHARK-SEC-007 (M-18): tighten from the 5-minute default to 30s
                    // so a stolen token expires within a smaller clock-drift window.
                    // Only apply the override when the user did not explicitly set it
                    // (IdentityModel's default is 5 minutes; treat that as 'unset').
                    if (tvp.ClockSkew == TimeSpan.FromMinutes(5))
                        tvp.ClockSkew = TimeSpan.FromSeconds(30);

                    // Save user's handlers so we can chain with Sharkable defaults
                    var userOnChallenge = opt.Events.OnChallenge;
                    var userOnForbidden = opt.Events.OnForbidden;

                    opt.Events.OnChallenge = async ctx =>
                    {
                        if (userOnChallenge != null)
                            await userOnChallenge(ctx);
                        if (!ctx.Response.HasStarted)
                        {
                            ctx.HandleResponse();
                            ctx.Response.StatusCode = 401;
                            await ProblemDetailsResult.WriteAsync(ctx.HttpContext, 401, "Authentication failed");
                        }
                    };

                    opt.Events.OnForbidden = async ctx =>
                    {
                        if (userOnForbidden != null)
                            await userOnForbidden(ctx);
                        if (!ctx.Response.HasStarted)
                        {
                            ctx.Response.StatusCode = 403;
                            await ProblemDetailsResult.WriteAsync(ctx.HttpContext, 403, "Forbidden");
                        }
                    };
                });
        }
        //register redacting log formatter
        if (Shark.SharkOption.RedactingLogOptions != null)
        {
            services.AddSingleton(Shark.SharkOption.RedactingLogOptions);
            services.Replace(ServiceDescriptor.Singleton(typeof(ILogger<>), typeof(RedactingLogger<>)));
        }
        //register multi-tenant services
        if (Shark.SharkOption.TenantOptions != null)
        {
            services.AddScoped<ITenant, Tenant>();

            if (Shark.SharkOption.TenantOptions.DataSourceOptions != null)
            {
                services.AddSingleton(Shark.SharkOption.TenantOptions.DataSourceOptions);
                services.AddScoped<ITenantDataSource, DefaultTenantDataSource>();
            }
        }
        //register distributed tracing
        if (Shark.SharkOption.TracingOptions != null)
        {
            services.AddSingleton(Shark.SharkOption.TracingOptions);
            if (Shark.SharkOption.TracingOptions.Exporter != null)
                services.AddSingleton(Shark.SharkOption.TracingOptions.Exporter);
        }
        //register profiler
        if (Shark.SharkOption.ProfilerOptions != null)
        {
            services.AddSingleton(Shark.SharkOption.ProfilerOptions);
        }
        //register ETag
        if (Shark.SharkOption.EnableETag)
        {
            services.AddSingleton(Shark.SharkOption.ETagOptions ?? new ETagOptions());
        }
        //register audit sink
        if (Shark.SharkOption.AuditTrailOptions != null)
        {
            if (Shark.SharkOption.AuditSinkFactory != null)
                services.AddSingleton<IAuditSink>(sp => Shark.SharkOption.AuditSinkFactory(sp));
            else
                services.TryAddSingleton<IAuditSink, LoggingAuditSink>();
        }
        //register metrics
        if (Shark.SharkOption.MetricsOptions?.Enabled == true)
        {
            services.AddSingleton(Shark.SharkOption.MetricsOptions);

            if (Shark.SharkOption.MetricsFactory != null)
                services.AddSingleton<ISharkMetrics>(sp => Shark.SharkOption.MetricsFactory(sp));
            else
                services.TryAddSingleton<ISharkMetrics, SharkMetrics>();
        }
        //register error localizer
        if (Shark.SharkOption.ErrorLocalizerFactory != null)
        {
            services.AddSingleton<IErrorLocalizer>(sp => Shark.SharkOption.ErrorLocalizerFactory(sp));
        }
        else
        {
            services.TryAddSingleton<IErrorLocalizer, DefaultErrorLocalizer>();
        }
        //register authorization interceptor
        if (Shark.SharkOption.AuthorizationInterceptorFactory != null)
        {
            services.AddSingleton(Shark.SharkOption.AuthorizationInterceptorFactory);
        }
        //register saga store
        if (Shark.SharkOption.SagaStoreFactory != null)
            services.AddSingleton<ISagaStore>(sp => Shark.SharkOption.SagaStoreFactory(sp));
        else
            services.TryAddSingleton<ISagaStore, MemorySagaStore>();
        //register saga executor
        if (Shark.SharkOption.SagaExecutorFactory != null)
            services.AddSingleton<ISagaExecutor>(sp => Shark.SharkOption.SagaExecutorFactory(sp));
        else
        {
            services.TryAddSingleton<SagaExecutor>();
            services.TryAddSingleton<ISagaExecutor>(sp => sp.GetRequiredService<SagaExecutor>());
        }

        //register cron job store + scheduler
        if (Shark.SharkOption.CronJobStoreFactory != null)
            services.AddSingleton<ICronJobStore>(sp => Shark.SharkOption.CronJobStoreFactory(sp));
        else
            services.TryAddSingleton<ICronJobStore, MemoryCronJobStore>();
        services.TryAddSingleton<CronScheduler>();
        services.TryAddSingleton<ICronScheduler>(sp => sp.GetRequiredService<CronScheduler>());
        services.AddHostedService<SharkCronHostedService>();

        //validate configuration
        var configErrors = ConfigurationValidator.Validate();
        if (configErrors.Count > 0)
        {
            throw new SharkConfigurationException(configErrors);
        }
    }
    /// <summary>
    /// common webapplication extensions for sharkable
    /// </summary>
    /// <param name="app"></param>
    /// <param name="setupOptions"></param>
    internal static void UseCommon(this WebApplication app, Action<UseSharkOptions>? setupOptions = null)
    {
        var opt = new UseSharkOptions();
        //invoke options
        setupOptions?.Invoke(opt);
        Shark.UseSharkOptions = opt;
        //configure internal shark 
        InternalShark.Configuration = app.Configuration;
        InternalShark.HostEnvironment = app.Environment;
        InternalShark.ServiceScopeFactory = app.Services.GetRequiredService<IServiceScopeFactory>();
        InternalShark.ServiceProvider = app.Services;
        InternalShark.StartedAt = DateTimeOffset.UtcNow;
        InternalShark.AppVersion = System.Reflection.Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3);
        //setup OpenAPI endpoint + Scalar UI
        app.UseSharkOpenApi();
       // app.MapSharkEndpointsWithAttributes();
    }

    private static void DiscoverAndConfigurePlugins(IServiceCollection services)
    {
        var plugins = PluginLoader.Discover(Shark.SharkOption, logger: null);
        Shark.SharkOption.DiscoveredPlugins = plugins;

        foreach (var plugin in plugins)
        {
            try
            {
                plugin.ConfigureServices(services, Shark.SharkOption);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[Sharkable] Plugin '{plugin.Name}' ConfigureServices threw: {ex.Message}");
            }
        }
    }
}
