using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Sharkable;

/// <summary>
/// Enhanced <see cref="BackgroundService"/> with built-in health reporting,
/// graceful stopping, retry policy, and execution tracing.
/// Inherit from this class and override <see cref="ExecuteAsync"/>.
/// </summary>
public abstract class SharkBackgroundService : BackgroundService, IHealthCheck
{
    private readonly TimeSpan _interval;
    private int _maxRetries;
    private TimeSpan _retryDelay;

    /// <summary>
    /// Current status of the background service.
    /// </summary>
    public BackgroundServiceStatus Status { get; private set; } = BackgroundServiceStatus.Idle;

    /// <summary>
    /// When the last execution completed (UTC).
    /// </summary>
    public DateTimeOffset? LastRunAt { get; private set; }

    /// <summary>
    /// Error message from the last failed execution, or <c>null</c>.
    /// </summary>
    public string? LastError { get; private set; }

    /// <summary>
    /// Total successful executions since startup.
    /// </summary>
    public long RunCount { get; private set; }

    /// <summary>
    /// Creates a new background service with the given interval and retry policy.
    /// </summary>
    /// <param name="interval">How often to execute the job.</param>
    /// <param name="maxRetries">Max retries on failure. Default 3.</param>
    /// <param name="retryDelay">Delay between retries. Default 5s.</param>
    protected SharkBackgroundService(TimeSpan interval, int maxRetries = 3, TimeSpan? retryDelay = null)
    {
        _interval = interval;
        _maxRetries = maxRetries;
        _retryDelay = retryDelay ?? TimeSpan.FromSeconds(5);
    }

    /// <inheritdoc />
    protected sealed override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            Status = BackgroundServiceStatus.Idle;
            await Task.Delay(_interval, stoppingToken);

            if (stoppingToken.IsCancellationRequested) break;

            Status = BackgroundServiceStatus.Running;
            var success = false;
            for (var attempt = 0; attempt <= _maxRetries && !success; attempt++)
            {
                try
                {
                    await OnExecuteAsync(stoppingToken);
                    success = true;
                    LastError = null;
                    RunCount++;
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    // SHARK-SEC-L024: preserve the full stack trace for the
                    // LastError so operators can diagnose failures without
                    // attaching a debugger. Previously LastError was set to
                    // ex.Message, which discards the inner exception (the
                    // actual cause for 9 out of 10 wrapper-style exceptions
                    // such as TargetInvocationException, AggregateException,
                    // etc.).
                    LastError = ex.ToString();
                    if (attempt < _maxRetries)
                    {
                        try { await Task.Delay(_retryDelay, stoppingToken); }
                        catch (OperationCanceledException) { break; }
                    }
                }
            }

            LastRunAt = DateTimeOffset.UtcNow;
            Status = success ? BackgroundServiceStatus.Idle : BackgroundServiceStatus.Failed;
        }

        Status = BackgroundServiceStatus.Stopped;
    }

    /// <summary>
    /// Override to provide the actual job logic. Called once per interval.
    /// </summary>
    protected abstract Task OnExecuteAsync(CancellationToken cancellationToken);

    /// <inheritdoc />
    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult(Status switch
        {
            BackgroundServiceStatus.Running => HealthCheckResult.Healthy("Running"),
            BackgroundServiceStatus.Idle => HealthCheckResult.Healthy("Idle"),
            // BUG-137: never surface the full exception (stack trace, file
            // paths) to anonymous /healthz callers — keep the detailed error
            // on the LastError property and in logs only.
            BackgroundServiceStatus.Failed => HealthCheckResult.Unhealthy("Background service failed"),
            BackgroundServiceStatus.Stopped => HealthCheckResult.Unhealthy("Stopped"),
            _ => HealthCheckResult.Healthy(),
        });
    }
}

/// <summary>
/// Status of a <see cref="SharkBackgroundService"/>.
/// </summary>
public enum BackgroundServiceStatus
{
    /// <summary>Service has not started yet.</summary>
    Idle,
    /// <summary>Service is actively executing.</summary>
    Running,
    /// <summary>Service terminated with an unhandled exception.</summary>
    Failed,
    /// <summary>Service has been cleanly stopped.</summary>
    Stopped,
}
