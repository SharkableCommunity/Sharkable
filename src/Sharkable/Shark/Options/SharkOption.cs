using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;
using Scalar.AspNetCore;

namespace Sharkable;

/// <summary>
/// Configuration options for Sharkable. Passed via callback in <c>builder.Services.AddShark(opt => ...)</c>.
/// </summary>
public sealed class SharkOption : ISharkOption
{
    /// <summary>
    /// Reserved section name. Sharkable options are currently configured
    /// exclusively via the <c>AddShark(opt => ...)</c> callback — appsettings.json
    /// binding is not yet implemented (BUG-126); this constant is reserved for
    /// future configuration binding.
    /// </summary>
    public const string Default = "Sharkable";
    /// <summary>
    /// API prefix for all endpoint groups. Default is <c>"api"</c>.
    /// </summary>
    public string ApiPrefix { get; set; } = "api";
    /// <summary>
    /// Default API version applied to endpoints without <c>[SharkVersion]</c>.
    /// When set, the version segment is included in all URL paths (e.g., <c>/api/v2/group/route</c>).
    /// Endpoints with an explicit <c>[SharkVersion]</c> always use their declared version.
    /// Default is <c>null</c> (no version segment for unversioned endpoints).
    /// </summary>
    public string? DefaultApiVersion { get; set; }
    /// <summary>
    /// Whether to enable OpenAPI document generation and Scalar UI. Default is true.
    /// </summary>
    public bool UseOpenApi { get; set; } = true;
    /// <summary>
    /// Endpoint path format, default is camel case.
    /// </summary>
    public EndpointFormat Format { get; set; } = EndpointFormat.CamelCase;
    /// <summary>
    /// Indicates whether the current environment is in AOT mode.
    /// </summary>
    public bool AotMode => InternalShark.AotMode;
    /// <summary>
    /// Options for the global exception handler middleware.
    /// </summary>
    public ExceptionHandlerOptions ExceptionHandlerOptions { get; set; } = new();
    /// <summary>
    /// When <c>true</c>, endpoint return values that are not <see cref="IResult"/> are
    /// automatically wrapped in <see cref="UnifiedResult{T}"/>.
    /// Can be overridden at runtime via <see cref="UseSharkOptions.EnableAutoWrap"/>.
    /// Default is <c>true</c>.
    /// </summary>
    public bool EnableAutoWrap { get; set; } = true;
    /// <summary>
    /// Custom factory for the OpenAPI schema used by <see cref="EnableAutoWrap"/>.
    /// Takes the original response schema and returns the wrapped schema.
    /// When <c>null</c>, the default <see cref="UnifiedResult{T}"/> shape is used.
    /// Set this when using a custom <see cref="IUnifiedResultFactory"/> so that
    /// the generated OpenAPI document matches your actual response structure.
    /// </summary>
    public Func<OpenApiSchema, IOpenApiSchema>? WrapSchemaFactory { get; set; }
    /// <summary>
    /// When true, scan and register FluentValidation validators, and auto-validate
    /// endpoint parameters that have a registered <c>IValidator&lt;T&gt;</c>.
    /// Default is false (opt-in).
    /// </summary>
    public bool EnableValidation { get; set; } = false;
    /// <summary>
    /// Controls the shape of validation error responses.
    /// <see cref="ValidationErrorMode.Messages"/> (default) joins messages
    /// with <c>"; "</c> in the unified result envelope.
    /// <see cref="ValidationErrorMode.ProblemDetails"/> emits RFC 7807
    /// ProblemDetails with an <c>errors</c> object.
    /// </summary>
    public ValidationErrorMode ValidationErrorMode { get; set; } = ValidationErrorMode.Messages;
    /// <summary>
    /// Factory for creating unified result responses.
    /// Set this to use a custom result format.
    /// Defaults to <see cref="DefaultUnifiedResultFactory"/> producing <see cref="UnifiedResult{T}"/>.
    /// </summary>
    public IUnifiedResultFactory? UnifiedResultFactory { get; set; }
    /// <summary>
    /// Configures rate limiting policies. Calls <c>services.AddRateLimiter()</c> when set.
    /// Apply to endpoints via <c>.SharkRequireRateLimiting("policyName")</c>.
    /// For distributed rate limiting backed by Redis/PostgreSQL, use
    /// <see cref="ConfigureRateLimiting"/> instead.
    /// </summary>
    public Action<RateLimiterOptions>? RateLimiterConfigure { get; set; }
    /// <summary>
    /// Overrides the default <see cref="IIdempotencyStore"/> registration.
    /// When set, the factory is invoked with <see cref="IServiceProvider"/> to
    /// create the store instance. Use this to plug in a Redis or database-backed
    /// store. If <c>null</c>, <see cref="MemoryIdempotencyStore"/> is used as
    /// the default unless a custom implementation was already registered.
    /// </summary>
    public Func<IServiceProvider, IIdempotencyStore>? IdempotencyStoreFactory { get; set; }
    /// <summary>
    /// Overrides the default <see cref="IDistributedRateLimitStore"/> registration.
    /// When set, the factory is invoked with <see cref="IServiceProvider"/> to
    /// create the store instance. Use this to plug in a Redis or database-backed
    /// store. If <c>null</c>, <see cref="MemoryRateLimitStore"/> is used as
    /// the default unless a custom implementation was already registered.
    /// </summary>
    public Func<IServiceProvider, IDistributedRateLimitStore>? RateLimitStoreFactory { get; set; }
    /// <summary>
    /// Configures output caching policies. Calls <c>services.AddOutputCache()</c> when set.
    /// Apply to endpoints via <c>.SharkCacheOutput("policyName")</c>.
    /// </summary>
    public Action<OutputCacheOptions>? OutputCacheConfigure { get; set; }
    /// <summary>
    /// When <c>true</c> (default), registers health check services and maps <c>/healthz</c> endpoint.
    /// Set to <c>false</c> to disable built-in health checks.
    /// </summary>
    public bool EnableHealthChecks { get; set; } = true;
    /// <summary>
    /// Controls how much detail <c>/healthz</c> exposes per check.
    /// <c>StatusOnly</c> (default in non-Development) hides exception messages
    /// and data; <c>Description</c> includes the check description;
    /// <c>Fulll</c> includes everything including <c>Exception.Message</c>.
    /// Default in Development is <c>Full</c>; otherwise <c>StatusOnly</c>.
    /// </summary>
    public HealthCheckDetailLevel HealthCheckDetailLevel { get; set; } = HealthCheckDetailLevel.StatusOnly;
    /// <summary>
    /// Optional callback to add custom health checks. Called after
    /// <c>services.AddHealthChecks()</c>. The builder parameter is
    /// <see cref="IHealthChecksBuilder"/>.
    /// </summary>
    public Action<IHealthChecksBuilder>? HealthChecksConfigure { get; set; }
    /// <summary>
    /// Endpoint path for the health check endpoint. Default is <c>"/healthz"</c>.
    /// </summary>
    public string HealthCheckPath { get; set; } = "/healthz";
    /// <summary>
    /// When <c>true</c>, wires the idempotency middleware into the pipeline.
    /// Requests carrying an <c>Idempotency-Key</c> header on an unsafe HTTP
    /// method are deduplicated and replayed. Default is <c>false</c>.
    /// </summary>
    public bool EnableIdempotency { get; set; } = false;
    /// <summary>
    /// Configures CORS policies. Calls <c>services.AddCors()</c> when set, and wires <c>app.UseCors()</c>.
    /// </summary>
    public Action<CorsOptions>? CorsConfigure { get; set; }
    /// <summary>
    /// Valid API keys for the API Key authentication middleware.
    /// When set, requests must include a valid <c>X-Api-Key</c> header.
    /// </summary>
    public string[]? ApiKeys { get; set; }
    /// <summary>
    /// Request header name for API key authentication. Default is <c>"X-Api-Key"</c>.
    /// </summary>
    public string ApiKeyHeaderName { get; set; } = "X-Api-Key";
    /// <summary>
    /// Configures JWT Bearer authentication with opinionated defaults.
    /// Calls <c>services.AddAuthentication().AddJwtBearer()</c> with the given authority and audiences.
    /// </summary>
    /// <param name="authority">The trusted token authority (issuer) URL.</param>
    /// <param name="audiences">
    /// Accepted audience values. <b>Must contain at least one non-empty entry.</b>
    /// An empty (or all-whitespace) audience list throws <see cref="InvalidOperationException"/>
    /// at startup — passing one would silently disable audience validation and accept tokens
    /// minted for any audience (SHARK-SEC-007 follow-up).
    /// </param>
    /// <param name="configure">Optional additional <see cref="JwtBearerOptions"/> configuration.</param>
    [Obsolete("Use ConfigureJwt(Action<JwtOptions>) instead. This overload will be removed in v0.8.0.")]
    public void ConfigureJwt(string authority, string[] audiences, Action<JwtBearerOptions>? configure = null)
    {
        if (string.IsNullOrWhiteSpace(authority))
            throw new ArgumentException("Authority cannot be null or whitespace.", nameof(authority));
        if (audiences is null || audiences.Length == 0)
            throw new InvalidOperationException(
                "JWT audience list cannot be empty. Audiences cannot be empty when JWT auth is enabled " +
                "(SHARK-SEC-007). Specify at least one audience, e.g. " +
                "opt.ConfigureJwt(authority: \"https://issuer\", audiences: new[] { \"api://default\" }).");
        for (var i = 0; i < audiences.Length; i++)
        {
            if (string.IsNullOrWhiteSpace(audiences[i]))
                throw new InvalidOperationException(
                    $"JWT audience at index {i} is null or whitespace (SHARK-SEC-007). All audience entries must be non-empty.");
        }
        JwtAuthority = authority;
        JwtAudiences = audiences;
        JwtConfigure = configure;
    }

    /// <summary>
    /// Configures JWT Bearer authentication with opinionated defaults.
    /// Calls <c>services.AddAuthentication().AddJwtBearer()</c> with the given options.
    /// </summary>
    /// <param name="configure">Callback to configure <see cref="JwtOptions"/>.</param>
    public void ConfigureJwt(Action<JwtOptions> configure)
    {
        var opt = new JwtOptions();
        configure(opt);

        if (string.IsNullOrWhiteSpace(opt.Authority))
            throw new ArgumentException("JwtOptions.Authority cannot be null or whitespace.", nameof(configure));
        if (opt.Audiences is null || opt.Audiences.Length == 0)
            throw new InvalidOperationException(
                "JWT audience list cannot be empty. Audiences cannot be empty when JWT auth is enabled " +
                "(SHARK-SEC-007). Specify at least one audience, e.g. " +
                "opt.ConfigureJwt(jwt => { jwt.Authority = \"https://issuer\"; jwt.Audiences = [\"api://default\"]; }).");
        for (var i = 0; i < opt.Audiences.Length; i++)
        {
            if (string.IsNullOrWhiteSpace(opt.Audiences[i]))
                throw new InvalidOperationException(
                    $"JWT audience at index {i} is null or whitespace (SHARK-SEC-007). All audience entries must be non-empty.");
        }

        JwtAuthority = opt.Authority;
        JwtAudiences = opt.Audiences;
        JwtConfigure = opt.BearerConfigure;
    }
    internal string? JwtAuthority { get; set; }
    internal string[]? JwtAudiences { get; set; }
    internal Action<JwtBearerOptions>? JwtConfigure { get; set; }
    /// <summary>
    /// When <c>true</c> (default), calls <c>services.AddAuthorization()</c> to register
    /// authorization services. Set to <c>false</c> to disable entirely.
    /// </summary>
    public bool EnableAuthorization { get; set; } = true;
    /// <summary>
    /// Configures <see cref="Microsoft.AspNetCore.Authorization.AuthorizationOptions"/>.
    /// For example: add custom policies, default authorization policy, fallback policy, etc.
    /// Ignored when <see cref="EnableAuthorization"/> is <c>false</c>.
    /// </summary>
    public void ConfigureAuthorization(Action<AuthorizationOptions> configure)
    {
        ConfigureAuthorizationInternal = configure;
    }

    internal Action<AuthorizationOptions>? ConfigureAuthorizationInternal { get; set; }
    /// <summary>
    /// When <c>true</c>, automatically adds <see cref="AuthorizeAttribute"/> metadata
    /// to every Sharkable endpoint (equivalent to calling <c>.RequireAuthorization()</c>
    /// on each one). Endpoints that already carry <see cref="IAuthorizeData"/> or
    /// <see cref="IAllowAnonymous"/> metadata are skipped. Default is <c>false</c>
    /// to preserve backward compatibility. Ignored when <see cref="EnableAuthorization"/>
    /// is <c>false</c>.
    /// </summary>
    public bool RequireAuthenticatedByDefault { get; set; } = false;
    /// <summary>
    /// Configures the OpenAPI document generation options.
    /// </summary>
    public void ConfigureOpenApi(Action<OpenApiOptions>? options)
    {
        OpenApiConfigure = options;
    }

    /// <summary>
    /// Registers an OpenAPI operation transformer without requiring direct
    /// access to <see cref="OpenApiOptions"/>. Equivalent to calling
    /// <c>options.AddOperationTransformer(...)</c>.
    /// Safe to call multiple times; each transformer is added to the pipeline.
    /// </summary>
    public void AddOpenApiOperationTransformer(Func<OpenApiOperation, OpenApiOperationTransformerContext, CancellationToken, Task> transformer)
    {
        OpenApiOperationTransformers.Add(transformer);
    }

    /// <summary>
    /// Registers an OpenAPI schema transformer without requiring direct
    /// access to <see cref="OpenApiOptions"/>. Equivalent to calling
    /// <c>options.AddSchemaTransformer(...)</c>.
    /// Safe to call multiple times; each transformer is added to the pipeline.
    /// </summary>
    public void AddOpenApiSchemaTransformer(Func<OpenApiSchema, OpenApiSchemaTransformerContext, CancellationToken, Task<OpenApiSchema>> transformer)
    {
        OpenApiSchemaTransformers.Add(transformer);
    }

    /// <summary>
    /// OpenAPI example generation hook. Receives a type and returns an example
    /// object of that type to include in the generated schema.
    /// Set once; the last registration wins.
    /// </summary>
    public Func<Type, object?>? OpenApiExampleFactory { get; set; }

    internal List<Func<OpenApiOperation, OpenApiOperationTransformerContext, CancellationToken, Task>> OpenApiOperationTransformers { get; } = [];
    internal List<Func<OpenApiSchema, OpenApiSchemaTransformerContext, CancellationToken, Task<OpenApiSchema>>> OpenApiSchemaTransformers { get; } = [];
    /// <summary>
    /// Configures the Scalar API reference UI.
    /// </summary>
    public void ConfigureScalar(Action<ScalarOptions> configure)
    {
        ScalarConfigure = configure;
    }
    internal Action<ScalarOptions>? ScalarConfigure { get; set; }
    /// <summary>
    /// Configures structured request/response audit trail logging with default options.
    /// When set, the <see cref="AuditTrailMiddleware"/> is wired into the pipeline.
    /// </summary>
    public void ConfigureAuditTrail()
    {
        AuditTrailOptions = new AuditTrailOptions();
    }

    /// <summary>
    /// Configures structured request/response audit trail logging.
    /// When set, the <see cref="AuditTrailMiddleware"/> is wired into the pipeline.
    /// </summary>
    public void ConfigureAuditTrail(Action<AuditTrailOptions> configure)
    {
        var opt = new AuditTrailOptions();
        configure(opt);
        AuditTrailOptions = opt;
    }
    internal AuditTrailOptions? AuditTrailOptions { get; set; }
    /// <summary>
    /// Pluggable audit sink factory. When set, the factory is invoked with
    /// <see cref="IServiceProvider"/> to create an <see cref="IAuditSink"/> instance.
    /// Use this to ship audit entries to Seq, Elasticsearch, Kafka, etc.
    /// Default (<c>null</c>) uses <see cref="LoggingAuditSink"/> which preserves
    /// the current structured-logging behavior.
    /// </summary>
    public Func<IServiceProvider, IAuditSink>? AuditSinkFactory { get; set; }

    /// <summary>
    /// Stores the idempotency options provided via <see cref="ConfigureIdempotency"/>.
    /// </summary>
    internal SharkIdempotencyOptions? IdempotencyOptions { get; set; }
    /// <summary>
    /// Stores the distributed rate limiter options provided via
    /// <see cref="ConfigureRateLimiting"/>.
    /// </summary>
    internal SharkRateLimiterOptions? RateLimitingOptions { get; set; }
    /// <summary>
    /// Configures AutoCrud (SqlSugar) options.
    /// </summary>
    public void ConfigureAutoCrud(Action<SqlSugarOptions>? options)
    {
        SqlSugarOptionsConfigure = options;
    }

    /// <summary>
    /// Enables multi-tenant row isolation for AutoCrud endpoints (DATA-04).
    /// When <c>true</c>, every AutoCrud query is filtered by the current tenant
    /// (via <see cref="ITenant"/>) and writes fill/keep the tenant column, so a
    /// tenant can never read or modify another tenant's rows. Requires the
    /// multi-tenant middleware (<see cref="TenantOptions"/>) to be configured so
    /// <see cref="ITenant.TenantId"/> is populated, and the entity's table must
    /// contain the tenant column. Default: <c>false</c> (opt-in).
    /// </summary>
    public bool EnableAutoCrudTenantFilter { get; set; }

    /// <summary>
    /// Column name used for tenant filtering when
    /// <see cref="EnableAutoCrudTenantFilter"/> is <c>true</c>. Default:
    /// <c>"TenantId"</c>. Must be a valid SQL identifier (letters, digits and
    /// underscore only, no quotes/spaces) — validated at endpoint generation time.
    /// </summary>
    public string AutoCrudTenantColumn { get; set; } = "TenantId";

    /// <summary>
    /// Configures the distributed rate limiter middleware. When set, a
    /// fixed-window rate limiter backed by <see cref="IDistributedRateLimitStore"/>
    /// is wired into the pipeline. Uses <see cref="MemoryRateLimitStore"/> by
    /// default; swap to Redis via <see cref="RateLimitStoreFactory"/>.
    /// </summary>
    /// <param name="configure">Callback to mutate the options instance.</param>
    public void ConfigureRateLimiting(Action<SharkRateLimiterOptions> configure)
    {
        var opt = new SharkRateLimiterOptions();
        configure(opt);
        RateLimitingOptions = opt;
    }
    /// <summary>
    /// Configures the idempotency middleware. Called only when
    /// <see cref="EnableIdempotency"/> is <c>true</c>.
    /// </summary>
    /// <param name="configure">Callback to mutate the options instance.</param>
    public void ConfigureIdempotency(Action<SharkIdempotencyOptions> configure)
    {
        var opt = new SharkIdempotencyOptions();
        configure(opt);
        IdempotencyOptions = opt;
    }
    /// <summary>
    /// Stores the OpenAPI configuration action provided via <see cref="ConfigureOpenApi"/>.
    /// </summary>
    internal Action<OpenApiOptions>? OpenApiConfigure { get; private set; }
    /// <summary>
    /// Stores the SqlSugar configuration action provided via <see cref="ConfigureAutoCrud"/>.
    /// </summary>
    internal Action<SqlSugarOptions>? SqlSugarOptionsConfigure{ get; private set; }
    /// <summary>
    /// Configures structured log field redaction.
    /// When set, <c>ILogger&lt;T&gt;</c> is replaced with a redacting wrapper.
    /// </summary>
    public void ConfigureRedactingLog(Action<RedactingLogOptions> configure)
    {
        var opt = new RedactingLogOptions();
        configure(opt);
        RedactingLogOptions = opt;
    }
    internal RedactingLogOptions? RedactingLogOptions { get; set; }
    /// <summary>
    /// Configures ASP.NET Core request timeouts. Calls <c>services.AddRequestTimeouts()</c>
    /// when set. Apply to endpoints via <c>.SharkRequestTimeout(ms)</c>.
    /// </summary>
    public Action<RequestTimeoutOptions>? RequestTimeoutsConfigure { get; set; }

    /// <summary>
    /// Default request timeout policy name applied when no per-endpoint timeout
    /// is specified via <c>.SharkRequestTimeout()</c>.
    /// Set via <c>o.AddPolicy(name, builder => ...); o.DefaultPolicy = name;</c>
    /// inside the <see cref="RequestTimeoutsConfigure"/> callback.
    /// Null means no default.
    /// </summary>
    public string? DefaultRequestTimeoutPolicy { get; set; }

    /// <summary>
    /// Configures framework metrics via <see cref="System.Diagnostics.Metrics"/>.
    /// When enabled, counters and histograms flow into OpenTelemetry
    /// automatically. Default is off.
    /// </summary>
    public void ConfigureMetrics(Action<MetricsOptions> configure)
    {
        var opt = new MetricsOptions();
        configure(opt);
        MetricsOptions = opt;
    }

    /// <summary>
    /// Stores the metrics options provided via <see cref="ConfigureMetrics"/>.
    /// </summary>
    internal MetricsOptions? MetricsOptions { get; set; }

    /// <summary>
    /// Overrides the default <see cref="ISharkMetrics"/> registration.
    /// When set, the factory is invoked with <see cref="IServiceProvider"/> to
    /// create the metrics instance. Use this to customize counter names, add
    /// tags/dimensions, or push to external systems.
    /// If <c>null</c>, <see cref="SharkMetrics"/> is used as the default
    /// unless a custom implementation was already registered.
    /// </summary>
    public Func<IServiceProvider, ISharkMetrics>? MetricsFactory { get; set; }

    /// <summary>
    /// Configures security headers middleware (X-Content-Type-Options, X-Frame-Options,
    /// Referrer-Policy, Content-Security-Policy, etc.). When set, a security headers
    /// middleware is wired into the pipeline. All headers are opt-in; no headers are
    /// added unless explicitly enabled.
    /// </summary>
    public void ConfigureSecurityHeaders(Action<SecurityHeadersOptions> configure)
    {
        var opt = new SecurityHeadersOptions();
        configure(opt);
        SecurityHeadersOptions = opt;
    }

    /// <summary>
    /// Stores the security headers options provided via <see cref="ConfigureSecurityHeaders"/>.
    /// </summary>
    internal SecurityHeadersOptions? SecurityHeadersOptions { get; set; }

    /// <summary>
    /// Configures multi-tenant support.
    /// When set, the <see cref="ITenant"/> scoped service and resolution middleware are registered.
    /// </summary>
    public void ConfigureMultiTenant(Action<TenantOptions> configure)
    {
        var opt = new TenantOptions();
        configure(opt);
        TenantOptions = opt;
    }
    internal TenantOptions? TenantOptions { get; set; }
    /// <summary>
    /// Configures graceful shutdown behavior. When set, the application will:
    /// <list type="bullet">
    ///   <item>Mark <c>/healthz</c> as unhealthy on SIGTERM (load balancer traffic drain)</item>
    ///   <item>Reject new requests with 503 during shutdown</item>
    ///   <item>Wait for in-flight requests to complete up to <see cref="GracefulShutdownOptions.DrainTimeout"/></item>
    /// </list>
    /// </summary>
    public void ConfigureGracefulShutdown(Action<GracefulShutdownOptions> configure)
    {
        var opt = new GracefulShutdownOptions();
        configure(opt);
        GracefulShutdownOptions = opt;
    }
    internal GracefulShutdownOptions? GracefulShutdownOptions { get; set; }
    /// <summary>
    /// Configures distributed tracing. When set, a W3C <c>traceparent</c>
    /// middleware is wired at the front of the pipeline, creating an
    /// <see cref="System.Diagnostics.Activity"/> per request.
    /// Compatible with OpenTelemetry exporters (Jaeger, Zipkin, OTLP)
    /// out of the box.
    /// </summary>
    public void ConfigureTracing(Action<TracingOptions> configure)
    {
        var opt = new TracingOptions();
        configure(opt);
        TracingOptions = opt;
    }
    internal TracingOptions? TracingOptions { get; set; }
    /// <summary>
    /// Configures the built-in profiler panel. Exposes a
    /// <c>/_sharkable/profiler</c> (configurable) endpoint showing request
    /// counts, average latency, and top-N slowest recent requests.
    /// </summary>
    public void ConfigureProfiler(Action<ProfilerOptions> configure)
    {
        var opt = new ProfilerOptions();
        configure(opt);
        ProfilerOptions = opt;
    }
    internal ProfilerOptions? ProfilerOptions { get; set; }
    /// <summary>
    /// When <c>true</c> (default), the <c>/_sharkable/profiler</c> endpoint
    /// requires a valid API key from <see cref="ApiKeys"/> sent via
    /// <see cref="ApiKeyHeaderName"/>. If no API keys are configured at all the
    /// endpoint responds <c>404</c> so its existence is not leaked to
    /// unauthenticated probes (SHARK-SEC-015). Set <c>false</c> to opt out of
    /// the gate (not recommended in production — the endpoint surfaces paths,
    /// status codes, latencies and memory deltas for every observed request).
    /// </summary>
    public bool ProfilerRequireApiKey { get; set; } = true;
    /// <summary>
    /// Enables automatic ETag generation and 304 Not Modified responses for
    /// GET/HEAD requests. Excludes health check, OpenAPI, and profiler paths
    /// by default.
    /// Default is <c>false</c>.
    /// </summary>
    public bool EnableETag { get; set; } = false;
    /// <summary>
    /// Options for ETag middleware (excluded paths, etc.).
    /// </summary>
    public ETagOptions? ETagOptions { get; set; }
    /// <summary>
    /// Pluggable error message localizer. When set, error messages from
    /// middleware (429, 503, etc.) are translated based on the
    /// <c>Accept-Language</c> request header. Set a factory to provide
    /// your implementation, or leave <c>null</c> for the default
    /// no-op (keys returned unchanged).
    /// </summary>
    public Func<IServiceProvider, IErrorLocalizer>? ErrorLocalizerFactory { get; set; }
    /// <summary>
    /// Default culture used when the <c>Accept-Language</c> header is missing or empty.
    /// Default is <c>"en"</c>.
    /// </summary>
    public string DefaultCulture { get; set; } = "en";
    /// <summary>
    /// Pluggable authorization interceptor. Runs before every endpoint.
    /// Return <c>null</c> to allow; return a non-null <see cref="IResult"/>
    /// to reject. Use for claim-based RBAC, tenant-scoped access, or custom
    /// API-key validation logic.
    /// </summary>
    public Func<IServiceProvider, IAuthorizationInterceptor>? AuthorizationInterceptorFactory { get; set; }
    /// <summary>
    /// Custom factory for ProblemDetails <c>type</c> URI.
    /// Receives the HTTP status code, returns the type URI.
    /// Default: <c>https://httpstatuses.com/{code}</c>.
    /// </summary>
    public Func<int, string>? ProblemDetailsTypeFactory { get; set; }
    /// <summary>
    /// Custom factory for ProblemDetails <c>title</c> string.
    /// Receives the HTTP status code, returns the title.
    /// Default: standard English titles (e.g. 400 → "Bad Request").
    /// </summary>
    public Func<int, string>? ProblemDetailsTitleFactory { get; set; }
    /// <summary>
    /// When <c>true</c>, all error responses (401, 403, 429, 500, 503, etc.)
    /// are written in RFC 7807 ProblemDetails format instead of the Sharkable
    /// unified result envelope. Default is <c>false</c>.
    /// </summary>
    public bool UseProblemDetails { get; set; } = false;
    /// <summary>
    /// When <c>true</c>, enables response compression via
    /// <c>services.AddResponseCompression()</c> and
    /// <c>app.UseResponseCompression()</c>. Skips already-compressed
    /// MIME types (images, videos). Default is <c>false</c>.
    /// </summary>
    public bool EnableResponseCompression { get; set; } = false;
    /// <summary>
    /// Overrides the default <see cref="ISagaStore"/> registration.
    /// When set, the factory is invoked with <see cref="IServiceProvider"/>
    /// to create the store instance. If <c>null</c>, <see cref="MemorySagaStore"/>
    /// is used as the default unless a custom implementation was already registered.
    /// </summary>
    public Func<IServiceProvider, ISagaStore>? SagaStoreFactory { get; set; }
    /// <summary>
    /// Overrides the default <see cref="ISagaExecutor"/> registration.
    /// When set, the factory is invoked with <see cref="IServiceProvider"/>
    /// to create the executor instance. Use this to customize lock TTL,
    /// compensation behavior, or replace the entire saga orchestration.
    /// If <c>null</c>, <see cref="SagaExecutor"/> is used as the default
    /// unless a custom implementation was already registered.
    /// </summary>
    public Func<IServiceProvider, ISagaExecutor>? SagaExecutorFactory { get; set; }
    /// <summary>
    /// Overrides the default <see cref="ICronJobStore"/> registration.
    /// Same factory pattern as <see cref="SagaStoreFactory"/>.
    /// </summary>
    public Func<IServiceProvider, ICronJobStore>? CronJobStoreFactory { get; set; }
    /// <summary>
    /// Callback for registering cron jobs. Invoked during
    /// <c>AddShark()</c> so that jobs can be registered before the
    /// hosted service starts. Returns a <see cref="Task"/> so callers can
    /// <c>await</c> <see cref="ICronScheduler.RegisterAsync"/> without
    /// forcing sync-over-async inside the host startup loop
    /// (SHARK-SEC-017).
    /// </summary>
    public Func<ICronScheduler, Task>? ConfigureCronJobs { get; set; }
    /// <summary>
    /// When <c>true</c> (default), the <c>/_sharkable/jobs</c> admin endpoint
    /// requires a valid API key from <see cref="ApiKeys"/> sent via
    /// <see cref="ApiKeyHeaderName"/>. If no API keys are configured at all the
    /// endpoint responds <c>404</c> so its existence is not leaked to
    /// unauthenticated probes. The endpoint also redacts each job's
    /// <c>LastError</c> field to its first 100 characters plus <c>"..."</c> to
    /// prevent business-logic leakage via error text (SHARK-SEC-016). Set
    /// <c>false</c> to opt out of the gate (not recommended in production — the
    /// endpoint exposes cron job names, run counts, paused status and errors).
    /// </summary>
    public bool CronAdminRequireApiKey { get; set; } = true;
    /// <summary>
    /// Pre-filled JWT token in the Scalar UI authentication dialog.
    /// When set, replaces the default placeholder in Scalar's Bearer auth.
    /// </summary>
    public string? ScalarJwtToken { get; set; }
    /// <summary>
    /// Pre-filled API key value in the Scalar UI authentication dialog.
    /// When set, replaces the default placeholder in Scalar's API Key auth.
    /// </summary>
    public string? ScalarApiKeyValue { get; set; }
    /// <summary>
    /// When <c>true</c> (default), prints a formatted startup banner (version, environment,
    /// UTC timestamp) to the console at the end of <c>UseShark()</c>.
    /// </summary>
    public bool ShowStartupBanner { get; set; } = true;

    /// <summary>
    /// Registers a callback that runs when the application has started
    /// (after <c>app.Run()</c> begins accepting requests).
    /// The callback receives the root <see cref="IServiceProvider"/>.
    /// </summary>
    public void ConfigureOnStarted(Action<IServiceProvider> onStarted)
    {
        OnStartedAction = onStarted;
    }
    internal Action<IServiceProvider>? OnStartedAction { get; set; }

    /// <summary>
    /// Registers a callback that runs when the application is stopping
    /// (SIGTERM / graceful shutdown).
    /// The callback receives the root <see cref="IServiceProvider"/>.
    /// </summary>
    public void ConfigureOnStopped(Action<IServiceProvider> onStopped)
    {
        OnStoppedAction = onStopped;
    }
    internal Action<IServiceProvider>? OnStoppedAction { get; set; }

    /// <summary>
    /// Registers a warmup service type that implements <see cref="IWarmupService"/>.
    /// The service is resolved from DI and its <see cref="IWarmupService.WarmupAsync"/>
    /// runs synchronously during startup, before the readiness gate opens.
    /// Throw to fail startup. Multiple warmup services run in parallel.
    /// </summary>
    public void ConfigureWarmup<T>() where T : class, IWarmupService
    {
        WarmupServiceTypes.Add(typeof(T));
    }
    internal List<Type> WarmupServiceTypes { get; set; } = [];
    internal Type? WarmupServiceType => WarmupServiceTypes.Count > 0 ? WarmupServiceTypes[0] : null;

    /// <summary>
    /// Timeout for warmup execution. Default is 30 seconds.
    /// BUG-142: made public so users can tune the warmup deadline.
    /// </summary>
    public TimeSpan WarmupTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Registers a service type to be validated at startup.
    /// <c>app.Services.GetRequiredService()</c> is called for each registered type
    /// during <c>UseShark()</c>, before the readiness gate opens.
    /// A resolution failure throws immediately, preventing the server from starting.
    /// </summary>
    public void ValidateOnStart<T>() where T : class
    {
        ValidateOnStartTypes.Add(typeof(T));
    }
    internal List<Type> ValidateOnStartTypes { get; set; } = [];

    /// <summary>
    /// Regex pattern for stripping common suffixes from endpoint group names.
    /// Default strips <c>Endpoint</c>, <c>Service</c>, <c>Controller</c>, etc.
    /// </summary>
    public string GroupNameSuffixPattern { get; set; } = "(endpoint|service|services|controller|controllers|apicontroller)(?=V?\\d*$)";
    /// <summary>
    /// Regex pattern for converting version prefixes in endpoint URLs.
    /// Default converts <c>V1</c> → <c>@1</c>.
    /// </summary>
    public string VersionFormatPattern { get; set; } = @"V(\d+)";
    /// <summary>
    /// Replacement string for <see cref="VersionFormatPattern"/>.
    /// Default is <c>@$1</c>.
    /// </summary>
    public string VersionFormatReplacement { get; set; } = @"@$1";

    /// <summary>
    /// Callback invoked for each endpoint group during registration.
    /// Receives the <see cref="RouteGroupBuilder"/> and the group name so
    /// callers can add shared filters, auth policies, or response type
    /// conventions in a single place.
    /// </summary>
    public Action<RouteGroupBuilder, string>? GroupConvention { get; set; }

    /// <summary>
    /// Callback invoked for each individual endpoint route group during registration.
    /// Receives the <see cref="RouteGroupBuilder"/> for the endpoint and the
    /// endpoint class <see cref="Type"/>. Use for per-endpoint
    /// <c>ProducesResponseType</c>, authorization, or metadata conventions.
    /// </summary>
    public Action<RouteGroupBuilder, Type>? EndpointConvention { get; set; }

    /// <summary>
    /// When <c>true</c> (default), automatically discovers <see cref="ISharkPlugin"/>
    /// implementations in all scanned assemblies (NuGet references + project assemblies).
    /// Set to <c>false</c> to disable assembly scanning — use
    /// <see cref="RegisterPlugin"/> or <see cref="ConfigurePlugins"/> instead.
    /// </summary>
    public bool AutoDiscoverPlugins { get; set; } = true;

    /// <summary>
    /// Configures the plugin hot-plug folder scanning system.
    /// When <see cref="PluginOptions.ScanOnStartup"/> is <c>true</c>,
    /// each subfolder under the configured directory is scanned for
    /// a <c>.dll</c> containing an <see cref="ISharkPlugin"/> implementation.
    /// Requires JIT (not AOT). Default is off.
    /// </summary>
    public void ConfigurePlugins(Action<PluginOptions> configure)
    {
        var opt = new PluginOptions();
        configure(opt);
        PluginOptionsInternal = opt;
    }

    /// <summary>
    /// Registers a plugin instance manually. Use in AOT mode or for in-app plugins.
    /// Deduplicated by <see cref="ISharkPlugin.Name"/>.
    /// </summary>
    public void RegisterPlugin(ISharkPlugin plugin)
    {
        ManualPlugins.Add(plugin);
    }

    /// <summary>
    /// Prevents a plugin from loading. Use the plugin's <see cref="ISharkPlugin.Name"/>.
    /// Effective across all three discovery paths.
    /// </summary>
    public void DisablePlugin(string pluginName)
    {
        DisabledPlugins.Add(pluginName);
    }

    internal PluginOptions? PluginOptions => PluginOptionsInternal;
    internal PluginOptions? PluginOptionsInternal { get; set; }
    internal List<ISharkPlugin> ManualPlugins { get; set; } = [];
    internal HashSet<string> DisabledPlugins { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    internal List<ISharkPlugin>? DiscoveredPlugins { get; set; }
}
