using Microsoft.Extensions.Caching.Memory;

namespace Sharkable;

/// <summary>
/// In-process <see cref="IDistributedRateLimitStore"/> backed by
/// <see cref="IMemoryCache"/> with a hard <see cref="MemoryCacheOptions.SizeLimit"/>
/// cap. Single-instance only; for distributed scenarios implement
/// <see cref="IDistributedRateLimitStore"/> with Redis or similar.
///
/// The cache's <c>SizeLimit</c> together with per-entry <c>Size = 1</c>
/// cost accounting caps the total number of distinct rate limit keys
/// (default 100,000), so a slow-loris attacker cannot exhaust process
/// memory by probing unique URLs in a tight loop. Entries expire
/// automatically when their fixed window elapses via
/// <c>AbsoluteExpirationRelativeToNow</c>; <see cref="MemoryCache"/>'s
/// built-in background compaction evicts them on the next scan.
/// </summary>
public sealed class MemoryRateLimitStore : IDistributedRateLimitStore, IDisposable
{
    private readonly MemoryCache _cache;
    private bool _disposed;

    /// <summary>
    /// Creates a new <see cref="MemoryRateLimitStore"/> with the default
    /// cap of 100,000 distinct rate limit keys.
    /// </summary>
    public MemoryRateLimitStore() : this(100_000) { }

    /// <summary>
    /// Creates a new <see cref="MemoryRateLimitStore"/> with a custom cap.
    /// </summary>
    /// <param name="maxEntries">
    /// Maximum number of distinct rate limit keys to retain. Must be &gt; 0,
    /// or <c>-1</c> to disable the cap (unbounded — only for trusted-internal
    /// services). When the cap is reached, the cache evicts the
    /// least-recently-used entries.
    /// </param>
    public MemoryRateLimitStore(long maxEntries)
    {
        if (maxEntries == -1)
        {
            _cache = new MemoryCache(new MemoryCacheOptions());
        }
        else if (maxEntries <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxEntries), "MaxEntries must be > 0 or -1 for uncapped.");
        }
        else
        {
            _cache = new MemoryCache(new MemoryCacheOptions { SizeLimit = maxEntries });
        }
    }

    /// <inheritdoc />
    public Task<long> IncrementAsync(string key, TimeSpan window)
    {
        if (_disposed) return Task.FromResult(0L);
        var counter = _cache.GetOrCreate(key, entry =>
        {
            // BUG-109: one size unit per key — the previous 256-byte unit
            // accounting meant MaxEntries=100,000 only held ~390 distinct
            // keys, letting an attacker evict other clients' counters.
            entry.Size = 1;
            entry.AbsoluteExpirationRelativeToNow = window;
            return new long[1];
        })!;
        var newCount = Interlocked.Increment(ref counter[0]);
        return Task.FromResult(newCount);
    }

    /// <inheritdoc />
    public Task ResetAsync(string key)
    {
        if (_disposed) return Task.CompletedTask;
        _cache.Remove(key);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _cache.Dispose();
    }
}