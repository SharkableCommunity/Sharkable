using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace Sharkable;

internal sealed class AuditLogBuffer : IDisposable
{
    private readonly Channel<AuditLogEntry> _channel;
    private readonly int _batchSize;
    private readonly TimeSpan _flushInterval;
    private readonly ILogger _logger;
    private readonly IAuditSink? _auditSink;
    private readonly LogLevel _successLevel;
    private readonly LogLevel _warningLevel;
    private readonly LogLevel _errorLevel;
    private readonly AuditTrailFormat _logFormat;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _consumerTask;
    private long _droppedCount;
    private int _errorLogged;
    private bool _disposed;

    internal AuditLogBuffer(AuditTrailOptions options, ILogger logger, IAuditSink? auditSink = null)
    {
        _channel = options.AsyncWrite
            ? Channel.CreateBounded<AuditLogEntry>(new BoundedChannelOptions(4096)
            {
                FullMode = BoundedChannelFullMode.DropWrite
            })
            : Channel.CreateUnbounded<AuditLogEntry>(new UnboundedChannelOptions { SingleReader = true });

        _batchSize = options.BatchSize;
        _flushInterval = options.FlushInterval;
        _logger = logger;
        _auditSink = auditSink;
        _successLevel = options.SuccessLogLevel;
        _warningLevel = options.WarningLogLevel;
        _errorLevel = options.ErrorLogLevel;
        _logFormat = options.LogFormat;

        _consumerTask = ConsumeAsync(_cts.Token);
    }

    internal void Write(AuditLogEntry entry)
    {
        if (!_channel.Writer.TryWrite(entry))
            Interlocked.Increment(ref _droppedCount);
    }

    internal void FlushRemaining()
    {
        _cts.Cancel();
        try
        {
            _consumerTask.GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task ConsumeAsync(CancellationToken ct)
    {
        var buffer = new List<AuditLogEntry>(_batchSize);
        var reader = _channel.Reader;

        try
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    if (!await reader.WaitToReadAsync(ct).ConfigureAwait(false))
                        break;

                    buffer.Clear();
                    var timer = _batchSize > 1 ? Task.Delay(_flushInterval, ct) : Task.CompletedTask;

                    while (buffer.Count < _batchSize && reader.TryRead(out var entry))
                    {
                        buffer.Add(entry);
                    }

                    await FlushBatchAsync(buffer);

                    if (_batchSize > 1)
                    {
                        try { await timer; } catch (OperationCanceledException) { }
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    if (Interlocked.CompareExchange(ref _errorLogged, 1, 0) == 0)
                        _logger.LogError(ex, "AuditLogBuffer consumer loop faulted; resuming");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Expected on shutdown — fall through to the drain below.
        }
        finally
        {
            // BUG-112: drain whatever is left so EnsureFlushOnShutdown is
            // honored. Previously a cancellation landing inside
            // WaitToReadAsync(ct) escaped the loop and skipped this block,
            // silently dropping every queued entry at shutdown.
            buffer.Clear();
            while (reader.TryRead(out var entry))
                buffer.Add(entry);
            if (buffer.Count > 0)
                await FlushBatchAsync(buffer);

            var dropped = Interlocked.Read(ref _droppedCount);
            _logger.LogDebug("AuditLogBuffer consumer exiting (cancellation token fired, {Dropped} entries dropped)", dropped);
        }
    }

    private async Task FlushBatchAsync(List<AuditLogEntry> batch)
    {
        if (_auditSink != null)
        {
            // BUG-138: await the sink so asynchronous write failures are
            // observed and logged instead of becoming unobserved task faults.
            try
            {
                await _auditSink.WriteBatchAsync(batch, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                if (Interlocked.CompareExchange(ref _errorLogged, 1, 0) == 0)
                    _logger.LogWarning(ex, "AuditLogBuffer sink flush failed");
            }
            return;
        }

        foreach (var entry in batch)
        {
            try
            {
                var level = entry.StatusCode >= 500 ? _errorLevel
                          : entry.StatusCode >= 400 ? _warningLevel
                          : _successLevel;

                if (!_logger.IsEnabled(level))
                    continue;

                if (_logFormat == AuditTrailFormat.Default)
                {
                    _logger.Log(level,
                        "HTTP {Method} {Path}{Query} responded {StatusCode} in {ElapsedMs}ms [CorrelationId: {CorrelationId}] Headers={Headers}",
                        entry.Method, entry.Path, entry.Query, entry.StatusCode, entry.ElapsedMs, entry.CorrelationId, entry.Headers);
                }
                else
                {
                    var message = AuditTrailOptions.FormatEntry(entry, _logFormat);
                    _logger.Log(level, "{Message}", message);
                }
            }
            catch (Exception ex)
            {
                if (Interlocked.CompareExchange(ref _errorLogged, 1, 0) == 0)
                    _logger.LogWarning(ex, "AuditLogBuffer batch flush failed");
            }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _cts.Cancel();
        // Wait (bounded) for the consumer so the drain in ConsumeAsync's
        // finally has a chance to complete before the CTS is disposed.
        try
        {
            _consumerTask.Wait(TimeSpan.FromSeconds(5));
        }
        catch (AggregateException)
        {
        }
        _cts.Dispose();
    }
}

/// <summary>
/// Represents a single audit-trail log entry captured by
/// <see cref="AuditTrailMiddleware"/>.
/// </summary>
public readonly record struct AuditLogEntry(
    string Method,
    string Path,
    string? Query,
    string Headers,
    int StatusCode,
    long ElapsedMs,
    string CorrelationId);
