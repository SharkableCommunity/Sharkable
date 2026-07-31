using Microsoft.Extensions.Logging;

namespace Sharkable;

/// <summary>
/// Orchestrates saga execution: acquires a distributed lock, loads
/// crash-recovery progress, executes steps, compensates on failure,
/// and persists progress for durability.
/// Use via DI: inject <c>SagaExecutor</c> and call <c>ExecuteAsync</c>.
/// </summary>
public sealed class SagaExecutor : ISagaExecutor
{
    private readonly ISagaStore _store;
    private readonly ILogger<SagaExecutor> _logger;

    /// <summary>
    /// Distributed lock TTL applied when the saga lock is acquired and on each renewal.
    /// </summary>
    /// <remarks>
    /// MUST exceed the 99.99th-percentile total step duration of the deployment.
    /// If a saga's steps run longer than <c>LockTtl</c>, the lock will expire while
    /// the saga is still executing, allowing a second instance to acquire the same
    /// lock and produce split-brain execution. Use <see cref="LockRenewalInterval"/>
    /// (default <c>LockTtl / 3</c>) so that long-running steps keep the lock alive.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when set to a negative value, or to a value &lt;= <see cref="LockRenewalInterval"/>
    /// (which would defeat the renewal protocol — the lock would expire before the
    /// first renewal fires).
    /// </exception>
    public TimeSpan LockTtl
    {
        get => _lockTtl;
        set
        {
            if (value < TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(nameof(value),
                    "LockTtl must be >= TimeSpan.Zero.");
            if (value != TimeSpan.Zero && _lockRenewalInterval != TimeSpan.Zero && value <= _lockRenewalInterval)
                throw new ArgumentOutOfRangeException(nameof(value),
                    $"LockTtl ({value}) must be greater than LockRenewalInterval ({_lockRenewalInterval}) so the renewal protocol is effective.");
            _lockTtl = value;
        }
    }

    private TimeSpan _lockTtl = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Timeout for compensation steps. When a saga is cancelled or a step fails,
    /// compensation runs with its own timeout that is NOT linked to the execution
    /// cancellation token — ensuring the saga can always roll back even when the
    /// caller has cancelled. Default is 60 seconds.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when set to a negative value or <see cref="TimeSpan.Zero"/>.
    /// </exception>
    public TimeSpan CompensationTimeout
    {
        get => _compensationTimeout;
        set
        {
            if (value <= TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(nameof(value),
                    "CompensationTimeout must be greater than TimeSpan.Zero.");
            _compensationTimeout = value;
        }
    }
    private TimeSpan _compensationTimeout = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Interval between automatic lock TTL renewals while a saga is in progress.
    /// </summary>
    /// <remarks>
    /// Defaults to <c>LockTtl / 3</c> so that at least two renewals occur before
    /// TTL expiry even if one renewal is lost. Set to <see cref="TimeSpan.Zero"/>
    /// to disable renewal (only safe when every saga completes well within <c>LockTtl</c>).
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when set to a negative value, or to a value &gt;= <see cref="LockTtl"/>
    /// (which would defeat the renewal protocol — the lock would expire before the
    /// first renewal fires).
    /// </exception>
    public TimeSpan LockRenewalInterval
    {
        get => _lockRenewalInterval;
        set
        {
            if (value < TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(nameof(value),
                    "LockRenewalInterval must be >= TimeSpan.Zero.");
            if (value != TimeSpan.Zero && _lockTtl != TimeSpan.Zero && value >= _lockTtl)
                throw new ArgumentOutOfRangeException(nameof(value),
                    $"LockRenewalInterval ({value}) must be less than LockTtl ({_lockTtl}) so the renewal protocol is effective.");
            _lockRenewalInterval = value;
        }
    }

    private TimeSpan _lockRenewalInterval;

    /// <summary>
    /// Creates a <see cref="SagaExecutor"/> with the default lock TTL (5 minutes).
    /// </summary>
    /// <param name="store">The saga store used for locks and progress persistence.</param>
    /// <param name="logger">Logger for saga lifecycle diagnostics.</param>
    public SagaExecutor(ISagaStore store, ILogger<SagaExecutor> logger)
        : this(store, logger, TimeSpan.FromMinutes(5))
    {
    }

    /// <summary>
    /// Creates a <see cref="SagaExecutor"/> with a custom lock TTL.
    /// </summary>
    /// <param name="store">The saga store used for locks and progress persistence.</param>
    /// <param name="logger">Logger for saga lifecycle diagnostics.</param>
    /// <param name="lockTtl">Distributed lock TTL; see <see cref="LockTtl"/>.</param>
    public SagaExecutor(ISagaStore store, ILogger<SagaExecutor> logger, TimeSpan lockTtl)
    {
        _store = store;
        _logger = logger;
        LockTtl = lockTtl;
        LockRenewalInterval = TimeSpan.FromTicks(lockTtl.Ticks / 3);
    }

    /// <summary>
    /// Executes a saga. Steps run in order. If a step fails, previously
    /// completed steps are compensated in reverse order.
    /// </summary>
    /// <param name="sagaId">Unique identifier for this saga instance.</param>
    /// <param name="saga">The saga definition with ordered steps.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns><see cref="SagaResult"/> indicating success or failure.</returns>
    public async Task<SagaResult> ExecuteAsync(string sagaId, Saga saga, CancellationToken ct = default)
    {
        if (!await _store.TryAcquireLockAsync(sagaId, LockTtl))
        {
            _logger.LogWarning("Saga {SagaId} is already executing", sagaId);
            return new SagaResult(false, "Saga is already executing");
        }

        using var renewCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var renewalTask = StartLockRenewalAsync(sagaId, renewCts.Token);

        try
        {
            var resumeFrom = await _store.LoadProgressAsync(sagaId, ct);
            return await ExecuteStepsAsync(sagaId, saga, resumeFrom, ct);
        }
        finally
        {
            renewCts.Cancel();
            try { await renewalTask; } catch (OperationCanceledException) { }
            await _store.ReleaseLockAsync(sagaId);
        }
    }

    /// <summary>
    /// Background loop that periodically renews the saga lock until the linked
    /// cancellation token fires. Returns immediately if renewal is disabled.
    /// </summary>
    private async Task StartLockRenewalAsync(string sagaId, CancellationToken ct)
    {
        if (LockRenewalInterval <= TimeSpan.Zero) return;

        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(LockRenewalInterval, ct);
                await _store.RenewLockAsync(sagaId, LockTtl);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Saga {SagaId} lock renewal failed", sagaId);
        }
    }

    private async Task<SagaResult> ExecuteStepsAsync(
        string sagaId, Saga saga, int resumeFrom, CancellationToken ct)
    {
        var steps = saga.Steps;
        var completedCount = resumeFrom + 1; // steps 0..resumeFrom are done

        for (var i = resumeFrom < 0 ? 0 : resumeFrom + 1; i < steps.Count; i++)
        {
            if (ct.IsCancellationRequested)
                return await CompensateAsync(sagaId, saga, completedCount, ct, "Cancelled");

            _logger.LogInformation("Saga {SagaId} step {Index}/{Total}", sagaId, i + 1, steps.Count);

            SagaStepResult stepResult;
            try
            {
                var result = await steps[i].ExecuteAsync(ct);
                stepResult = result.Success
                    ? new SagaStepResult(true)
                    : new SagaStepResult(false, result.Error);
            }
            catch (Exception ex)
            {
                stepResult = new SagaStepResult(false, ex.Message);
            }

            if (!stepResult.Success)
            {
                _logger.LogError("Saga {SagaId} step {Index} failed: {Error}", sagaId, i, stepResult.Error);
                return await CompensateAsync(sagaId, saga, completedCount, ct, stepResult.Error ?? "Unknown error");
            }

            await _store.SaveProgressAsync(sagaId, i, ct);
            completedCount++;
        }

        await _store.DeleteAsync(sagaId, ct);
        _logger.LogInformation("Saga {SagaId} completed successfully", sagaId);
        return new SagaResult(true);
    }

    private async Task<SagaResult> CompensateAsync(
        string sagaId, Saga saga, int completedCount, CancellationToken ct, string error)
    {
        var compensationFailed = false;

        for (var i = completedCount - 1; i >= 0; i--)
        {
            _logger.LogWarning("Saga {SagaId} compensating step {Index}", sagaId, i + 1);
            try
            {
                // BUG-115: fresh per-step timeout so one slow step cannot
                // consume the whole compensation budget and silently starve
                // the remaining rollback steps.
                using var stepCts = new CancellationTokenSource(CompensationTimeout);
                await saga.Steps[i].CompensateAsync(stepCts.Token);
            }
            catch (OperationCanceledException)
            {
                _logger.LogError(
                    "Saga {SagaId} compensation for step {Index} timed out after {Timeout}",
                    sagaId, i, CompensationTimeout);
                compensationFailed = true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Saga {SagaId} compensation for step {Index} failed", sagaId, i);
                compensationFailed = true;
            }
        }

        if (compensationFailed)
        {
            // BUG-116: keep the persisted progress so a later retry resumes
            // from the correct step instead of re-running forward steps of a
            // saga whose rollback never completed.
            _logger.LogError(
                "Saga {SagaId} compensation incomplete; progress retained for manual/out-of-band recovery",
                sagaId);
            return new SagaResult(false, $"{error} (compensation incomplete — saga progress retained)", completedCount);
        }

        await _store.DeleteAsync(sagaId, CancellationToken.None);
        return new SagaResult(false, error, completedCount);
    }
}
