using Microsoft.Extensions.Logging;

namespace Sharkable;

/// <summary>
/// Cron scheduler implementation. Runs as a singleton, managing all
/// registered jobs and their lifecycle.
/// </summary>
public sealed class CronScheduler : ICronScheduler
{
    // SHARK-SEC-M013: collapse the previous three correlated dictionaries
    // (jobs / states / expressions) into a single JobEntry record under
    // one lock. The previous design had TOCTOU gaps where Register could
    // mutate _expressions and _jobs but a concurrent GetDueJobsAsync
    // could read the partial state between the two writes.
    private sealed class JobEntry(CronJob Job, CronJobState State, CronExpression Expression)
    {
        public CronJob Job { get; } = Job;
        public CronJobState State { get; } = State;
        public CronExpression Expression { get; } = Expression;

        /// <summary>
        /// BUG-104: memoized horizon for expressions that can never match.
        /// Once <see cref="CronExpression.GetNext"/> exhausts its 4-year
        /// search window, this is set to that horizon so subsequent ticks
        /// skip the expensive scan until the horizon passes.
        /// </summary>
        public DateTimeOffset? ExhaustedUntil;
    }

    private readonly Dictionary<string, JobEntry> _entries = [];
    private readonly ICronJobStore _store;
    private readonly ILogger<CronScheduler> _logger;
    private readonly IHostApplicationLifetime? _lifetime;

    /// <summary>
    /// Distributed lock TTL applied when a cron job lock is acquired and on
    /// each renewal. Default 10 minutes.
    /// </summary>
    /// <remarks>
    /// MUST exceed the 99.99th-percentile job duration of the deployment. If
    /// a job runs longer than <c>CronLockTtl</c>, the lock will expire while
    /// the job is still executing, allowing a second instance to acquire the
    /// same lock and produce split-brain execution. The scheduler runs a
    /// background renewal task at <c>CronLockTtl / 3</c> intervals to keep the
    /// lock alive for long-running jobs (mirrors <see cref="SagaExecutor"/>).
    /// </remarks>
    public TimeSpan CronLockTtl { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>Creates a scheduler with the given store and logger.</summary>
    /// <param name="store">The persistent job state store.</param>
    /// <param name="logger">Logger.</param>
    /// <param name="lifetime">Optional host lifetime used to cancel manually
    /// triggered jobs during shutdown (BUG-128).</param>
    public CronScheduler(ICronJobStore store, ILogger<CronScheduler> logger, IHostApplicationLifetime? lifetime = null)
    {
        _store = store;
        _logger = logger;
        _lifetime = lifetime;
    }

    internal IReadOnlyCollection<CronJob> Jobs
    {
        get { lock (_entries) return _entries.Values.Select(e => e.Job).ToList(); }
    }

    /// <summary>Registers a new cron job and starts tracking its schedule.</summary>
    public async Task RegisterAsync(CronJob job)
    {
        // Parse cron first — fail fast before any state mutation
        CronExpression expr;
        try { expr = CronExpression.Parse(job.Cron); }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Cron job {Name} has invalid expression: {Cron}", job.Name, job.Cron);
            return;
        }

        // SHARK-SEC-017: await the store directly instead of blocking via
        // .GetAwaiter().GetResult(). The sync-over-async form previously used
        // here deadlocks under contention with distributed stores (Redis,
        // PostgreSQL) whose IO completions need the thread pool.
        var existing = await _store.LoadStateAsync(job.Name);
        var state = existing ?? new CronJobState
        {
            Name = job.Name,
            Description = job.Options.Description ?? "",
            Cron = job.Cron,
            Paused = job.Options.Paused,
        };
        state.Description = job.Options.Description ?? "";
        state.Cron = job.Cron;

        // Single dictionary insert under one lock — readers see the fully
        // initialized (Job, State, Expression) tuple atomically.
        lock (_entries) _entries[job.Name] = new JobEntry(job, state, expr);
    }

    internal async Task<List<(CronJob Job, CronJobState State, bool LockHeld)>> GetDueJobsAsync()
    {
        var now = DateTimeOffset.UtcNow;
        var due = new List<(CronJob, CronJobState, bool)>();

        JobEntry[] snapshot;
        lock (_entries) snapshot = _entries.Values.ToArray();

        foreach (var entry in snapshot)
        {
            var job = entry.Job;
            var state = entry.State;
            var expr = entry.Expression;

            if (state.Paused) continue;
            if (state.IsRunning && job.Options.Concurrency == CronJobConcurrency.SkipIfRunning)
                continue;

            // BUG-104: skip the 4-year scan for expressions already proven to
            // never match within the memoized horizon.
            if (entry.ExhaustedUntil is { } exhausted && now <= exhausted)
                continue;

            var next = expr.GetNext(now - TimeSpan.FromSeconds(1));
            if (next == null)
            {
                // No match within the next 4 years — memoize so this does not
                // burn ~2.1M iterations on every 1-second tick.
                entry.ExhaustedUntil = now.AddYears(4);
                continue;
            }

            lock (_entries) { if (_entries.TryGetValue(job.Name, out var e)) e.State.NextRun = next; }
            if (next > now) continue;

            var lockHeld = false;
            if (job.Options.Concurrency == CronJobConcurrency.SkipIfRunning)
            {
                if (!await _store.TryAcquireJobLockAsync(job.Name, CronLockTtl))
                    continue;
                lock (_entries) { if (_entries.TryGetValue(job.Name, out var e)) e.State.IsRunning = true; }
                lockHeld = true;
            }

            due.Add((job, state, lockHeld));
        }

        due.Sort((a, b) => (a.Item2.NextRun ?? DateTimeOffset.MaxValue)
            .CompareTo(b.Item2.NextRun ?? DateTimeOffset.MaxValue));
        return due;
    }

    internal async Task ExecuteJobAsync(CronJob job, CronJobState state, bool lockHeld, CancellationToken shutdownToken = default)
    {
        CancellationTokenSource? renewCts = null;
        Task? renewalTask = null;
        if (lockHeld && CronLockTtl > TimeSpan.Zero)
        {
            renewCts = new CancellationTokenSource();
            renewalTask = StartLockRenewalAsync(job.Name, renewCts.Token);
        }

        try
        {
            lock (_entries) { if (_entries.TryGetValue(job.Name, out var e)) { e.State.IsRunning = true; e.State.LastRun = DateTimeOffset.UtcNow; } }
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var success = false;

            for (var attempt = 0; attempt <= job.Options.RetryCount && !success; attempt++)
            {
                try
                {
                    using var timeoutCts = job.Options.Timeout.HasValue
                        ? new CancellationTokenSource(job.Options.Timeout.Value)
                        : new CancellationTokenSource();

                    using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(timeoutCts.Token, shutdownToken);
                    await job.Handler(linkedCts.Token);
                    success = true;
                    lock (_entries) { if (_entries.TryGetValue(job.Name, out var e)) e.State.LastError = null; }
                }
                catch (OperationCanceledException) when (!shutdownToken.IsCancellationRequested)
                {
                    lock (_entries) { if (_entries.TryGetValue(job.Name, out var e)) e.State.LastError = "Job timed out"; }
                    _logger.LogWarning("Cron job {Name} timed out (attempt {Attempt})", job.Name, attempt + 1);
                    break;
                }
                catch (Exception ex)
                {
                    lock (_entries) { if (_entries.TryGetValue(job.Name, out var e)) e.State.LastError = ex.Message; }
                    _logger.LogError(ex, "Cron job {Name} failed (attempt {Attempt})", job.Name, attempt + 1);
                    if (attempt < job.Options.RetryCount)
                        await Task.Delay(job.Options.RetryDelay, shutdownToken);
                }
            }

            sw.Stop();
            lock (_entries) { if (_entries.TryGetValue(job.Name, out var e)) { e.State.IsRunning = false; e.State.LastDurationMs = sw.ElapsedMilliseconds; if (success) e.State.RunCount++; } }
        }
        finally
        {
            if (lockHeld)
            {
                renewCts?.Cancel();
                if (renewalTask != null)
                {
                    try { await renewalTask; }
                    catch (OperationCanceledException) { }
                }
                await _store.ReleaseJobLockAsync(job.Name);
                renewCts?.Dispose();
            }

            await _store.SaveStateAsync(job.Name, state);
        }
    }

    /// <summary>
    /// Background loop that periodically renews the cron job lock until the
    /// cancellation token fires. Mirrors <see cref="SagaExecutor"/>'s renewal
    /// pattern so long-running jobs do not lose their lock to TTL expiry.
    /// </summary>
    private async Task StartLockRenewalAsync(string jobName, CancellationToken ct)
    {
        var interval = TimeSpan.FromTicks(CronLockTtl.Ticks / 3);
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(interval, ct);
                await _store.RenewJobLockAsync(jobName, CronLockTtl);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Cron job {Name} lock renewal failed", jobName);
        }
    }

    /// <summary>Manually triggers a registered cron job by name.</summary>
    public async Task<CronJobState?> TriggerAsync(string name)
    {
        JobEntry? entry;
        lock (_entries) _entries.TryGetValue(name, out entry);
        if (entry == null) return null;

        var job = entry.Job;
        var state = entry.State;

        // BUG-128: honor the concurrency policy — a manual trigger must not
        // run concurrently with an in-flight scheduled execution.
        if (state.IsRunning && job.Options.Concurrency == CronJobConcurrency.SkipIfRunning)
        {
            _logger.LogWarning("Cron job {Name} trigger skipped: already running", name);
            return state;
        }

        // Link to the host shutdown token so a manually triggered job is
        // cancelled when the application stops (previously CancellationToken.None).
        var shutdownToken = _lifetime?.ApplicationStopping ?? CancellationToken.None;
        _ = Task.Run(async () =>
        {
            try
            {
                await ExecuteJobAsync(job, state, lockHeld: false, shutdownToken);
            }
            catch (OperationCanceledException) when (shutdownToken.IsCancellationRequested)
            {
                // Expected during shutdown.
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Cron job {Name} manual trigger failed", job.Name);
            }
        }, shutdownToken);
        return state;
    }

    /// <summary>Pauses a cron job by name. Paused jobs skip their scheduled runs.</summary>
    public Task PauseAsync(string name)
    {
        lock (_entries) { if (_entries.TryGetValue(name, out var e)) e.State.Paused = true; }
        return Task.CompletedTask;
    }

    /// <summary>Resumes a previously paused cron job.</summary>
    public Task ResumeAsync(string name)
    {
        lock (_entries) { if (_entries.TryGetValue(name, out var e)) e.State.Paused = false; }
        return Task.CompletedTask;
    }

    /// <summary>Returns the runtime state of all registered cron jobs.</summary>
    public Task<IReadOnlyList<CronJobState>> ListAsync()
    {
        lock (_entries)
        {
            return Task.FromResult<IReadOnlyList<CronJobState>>(
                _entries.Values.Select(e => e.State).ToList());
        }
    }
}