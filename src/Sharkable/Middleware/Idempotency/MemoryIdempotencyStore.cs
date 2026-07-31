using Microsoft.Extensions.Caching.Memory;

namespace Sharkable;

/// <summary>
/// In-process <see cref="IIdempotencyStore"/> backed by a private
/// <see cref="MemoryCache"/>. Single-instance only; for distributed
/// scenarios implement <see cref="IIdempotencyStore"/> with Redis or
/// similar.
///
/// The cache is size-limited via <see cref="SharkIdempotencyOptions.MaxEntries"/>
/// (default 10,000 distinct entries) so an attacker sending random unique
/// <c>Idempotency-Key</c> headers cannot exhaust process memory. Each entry
/// counts as one size unit; the per-response byte cap is enforced separately
/// by <see cref="SharkIdempotencyOptions.MaxResponseSize"/>.
/// </summary>
public sealed class MemoryIdempotencyStore : IIdempotencyStore, IDisposable
{
    private readonly MemoryCache _cache;
    private readonly object _reservationLock = new();
    private bool _disposed;

    /// <summary>Marker for an in-flight slot (no record yet).</summary>
    private sealed record InFlightMarker;

    /// <summary>
    /// Creates an in-process idempotency store with a size-limited private <see cref="MemoryCache"/>.
    /// </summary>
    /// <param name="options">Idempotency options controlling the cache size limit.</param>
    public MemoryIdempotencyStore(SharkIdempotencyOptions options)
    {
        _cache = new MemoryCache(new MemoryCacheOptions { SizeLimit = options.MaxEntries });
    }

    /// <summary>
    /// Atomically reserves an idempotency key slot. Returns <c>true</c> if this
    /// caller won the reservation; <c>false</c> if another request already holds it.
    /// </summary>
    public Task<bool> TryReserveAsync(string key, TimeSpan inFlightTtl)
    {
        if (_disposed) return Task.FromResult(false);
        lock (_reservationLock)
        {
            var marker = new InFlightMarker();
            var actual = _cache.GetOrCreate<object>(key, entry =>
            {
                entry.Size = 1;
                entry.AbsoluteExpirationRelativeToNow = inFlightTtl;
                return marker;
            });
            return Task.FromResult(ReferenceEquals(actual, marker));
        }
    }

    /// <summary>Returns the current state (in-flight or completed) for a key.</summary>
    public Task<IdempotencyLookup?> GetAsync(string key)
    {
        if (_disposed) return Task.FromResult<IdempotencyLookup?>(null);
        if (!_cache.TryGetValue(key, out var value) || value is null)
            return Task.FromResult<IdempotencyLookup?>(null);
        var result = value switch
        {
            InFlightMarker => (IdempotencyLookup)new IdempotencyInFlight(),
            IdempotencyRecord r => new IdempotencyHit(r),
            _ => null,
        };
        return Task.FromResult(result);
    }

    /// <summary>Persists a completed idempotency record for replay.</summary>
    public Task StoreAsync(string key, IdempotencyRecord record, TimeSpan ttl)
    {
        if (_disposed) return Task.CompletedTask;
        using var entry = _cache.CreateEntry(key);
        entry.Size = 1;
        entry.AbsoluteExpirationRelativeToNow = ttl;
        entry.Value = record;
        return Task.CompletedTask;
    }

    /// <summary>Releases (removes) an idempotency key from the store.</summary>
    public Task ReleaseAsync(string key)
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