using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;

namespace Sharkable;

/// <summary>
/// Health check that verifies the configured JWT authority is reachable.
/// Registered automatically when <c>ConfigureJwt()</c> is set.
/// </summary>
internal sealed class JwtHealthCheck : IHealthCheck
{
    private static readonly TimeSpan CacheDuration = TimeSpan.FromSeconds(30);
    private readonly IHttpClientFactory? _httpClientFactory;
    private readonly ILogger<JwtHealthCheck>? _logger;
    private static (DateTimeOffset timestamp, HealthCheckResult result)? _cachedResult;
    private static readonly object _cacheLock = new();

    /// <summary>
    /// Resolved by the Microsoft.Extensions.DependencyInjection health-check
    /// infrastructure when <see cref="IServiceProvider"/> is available.
    /// </summary>
    public JwtHealthCheck() { }

    /// <summary>
    /// SHARK-SEC-M016: when DI is available we accept an
    /// <see cref="IHttpClientFactory"/> so the check reuses a pooled
    /// <see cref="HttpClient"/> across calls. Without the factory the
    /// fallback path opens a fresh socket per probe — k8s liveness probes
    /// every 5s would exhaust sockets on the OIDC provider.
    /// </summary>
    public JwtHealthCheck(IHttpClientFactory httpClientFactory, ILogger<JwtHealthCheck> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        lock (_cacheLock)
        {
            if (_cachedResult is { } c && (DateTimeOffset.UtcNow - c.timestamp) < CacheDuration)
                return c.result;
        }

        var result = await ProbeAsync(cancellationToken);

        lock (_cacheLock)
            _cachedResult = (DateTimeOffset.UtcNow, result);

        return result;
    }

    private async Task<HealthCheckResult> ProbeAsync(CancellationToken cancellationToken)
    {
        var authority = Shark.SharkOption.JwtAuthority;
        if (string.IsNullOrEmpty(authority))
            return HealthCheckResult.Unhealthy("JWT authority is not configured");

        // BUG-122: a non-URL authority (e.g. a plain issuer name for
        // self-issued JWTs — the documented ConfigureJwt pattern) cannot be
        // probed. Treat it as healthy instead of permanently unhealthy, which
        // would make /healthz fail readiness checks forever.
        if (!Uri.TryCreate(authority, UriKind.Absolute, out var authorityUri)
            || (authorityUri.Scheme != Uri.UriSchemeHttp && authorityUri.Scheme != Uri.UriSchemeHttps))
        {
            return HealthCheckResult.Healthy("JWT authority is not an HTTP(S) endpoint; skipping probe");
        }

        // SHARK-SEC-M004: /healthz is publicly readable and the previous
        // implementation echoed the authority URL (and ex.Message on
        // failure) into the JSON description, leaking the OIDC issuer
        // topology and exception internals to any anonymous caller.
        // Return generic descriptions; the full URL and exception details
        // are surfaced via structured logging below.
        var logger = _logger ?? InternalShark.ServiceProvider?.GetService<ILoggerFactory>()
            ?.CreateLogger<JwtHealthCheck>();

        try
        {
            using var probeCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            probeCts.CancelAfter(TimeSpan.FromSeconds(5));

            // SHARK-SEC-M016: prefer the IHttpClientFactory-injected client
            // (shared sockets, handler lifetime managed). Fall back to a
            // short-lived client only when the type is constructed without
            // DI (some test hosts do not register the factory).
            var http = _httpClientFactory?.CreateClient("sharkable.jwt-authority")
                       ?? new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            var ownsHttp = _httpClientFactory == null;

            try
            {
                // BUG-123: dispose the response (and its content stream) so
                // the pooled connection is returned promptly.
                using var response = await http.GetAsync(
                    $"{authority.TrimEnd('/')}/.well-known/openid-configuration",
                    probeCts.Token);

                if (response.IsSuccessStatusCode)
                {
                    logger?.LogDebug("JWT authority reachable: {Authority}", authority);
                    return HealthCheckResult.Healthy("JWT authority reachable");
                }

                logger?.LogWarning(
                    "JWT authority returned {StatusCode}: {Authority}",
                    response.StatusCode, authority);
                return HealthCheckResult.Degraded("JWT authority probe failed");
            }
            finally
            {
                if (ownsHttp)
                    http.Dispose();
            }
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "JWT authority unreachable: {Authority}", authority);
            return HealthCheckResult.Unhealthy("JWT authority unreachable");
        }
    }
}
