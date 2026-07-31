using Microsoft.Extensions.Logging;

namespace Sharkable;

/// <summary>
/// Background service that polls registered cron jobs every second
/// and executes those whose cron expression matches the current time.
/// </summary>
internal sealed class SharkCronHostedService : BackgroundService
{
    private readonly CronScheduler _scheduler;
    private readonly ILogger<SharkCronHostedService> _logger;

    public SharkCronHostedService(CronScheduler scheduler, ILogger<SharkCronHostedService> logger)
    {
        _scheduler = scheduler;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Invoke user-registered cron jobs (SHARK-SEC-017: await the async
        // callback so RegisterAsync's distributed-store IO does not block the
        // host startup loop)
        var configureAction = Shark.SharkOption.ConfigureCronJobs;
        if (configureAction != null)
        {
            await configureAction(_scheduler);
        }

        var consecutiveErrors = 0;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var due = await _scheduler.GetDueJobsAsync();
                foreach (var (job, state, lockHeld) in due)
                {
                    // SHARK-SEC-M012: link the host's stoppingToken to a per-job
                    // CancellationTokenSource so an in-flight long-running job
                    // is cancelled when the host shuts down (the previous
                    // fire-and-forget _ = Task.Run(...) left orphaned tasks that
                    // would survive app.StopAsync()).
                    var jobCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            await _scheduler.ExecuteJobAsync(job, state, lockHeld, jobCts.Token);
                        }
                        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                        {
                            // Expected during shutdown.
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "Cron job {Name} unhandled failure", job.Name);
                        }
                        finally
                        {
                            jobCts.Dispose();
                        }
                    }, jobCts.Token);
                }

                consecutiveErrors = 0;
            }
            catch (Exception ex)
            {
                // BUG-129: a persistent store outage previously logged once per
                // second forever. Back off exponentially (up to 30 s) so the
                // error volume stays bounded while the loop keeps retrying.
                consecutiveErrors++;
                var backoffSeconds = Math.Min(30, 1 << Math.Min(consecutiveErrors - 1, 5));
                _logger.LogError(ex,
                    "Cron scheduler loop error (attempt {Attempt}); retrying in {Delay}s",
                    consecutiveErrors, backoffSeconds);
                await Task.Delay(TimeSpan.FromSeconds(backoffSeconds), stoppingToken);
                continue;
            }

            await Task.Delay(1000, stoppingToken);
        }
    }
}