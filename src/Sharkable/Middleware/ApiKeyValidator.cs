using System.Security.Cryptography;
using System.Text;

namespace Sharkable;

/// <summary>
/// Shared API-key validator with cached constant-time SHA-256 comparisons
/// (SHARK-SEC-008). Reads keys from the single static
/// <see cref="Shark.SharkOption"/> instance and rebuilds its hash cache
/// whenever the <c>ApiKeys</c> array reference changes — keeping hot-reload
/// semantics without depending on the DI options machinery (BUG-126: all
/// framework state lives on the static options instance).
/// </summary>
internal sealed class ApiKeyValidator
{
    private readonly object _sync = new();
    private byte[][] _cachedHashes = [];
    private string[]? _cachedKeys;

    /// <summary>
    /// Validates a candidate API key against the configured keys using
    /// constant-time comparison. Returns <c>false</c> when no keys are
    /// configured or the key does not match.
    /// </summary>
    public bool Validate(string providedApiKey)
    {
        var keys = Shark.SharkOption.ApiKeys;
        if (!ReferenceEquals(keys, _cachedKeys))
        {
            lock (_sync)
            {
                if (!ReferenceEquals(keys, _cachedKeys))
                {
                    _cachedKeys = keys;
                    RebuildCache(keys);
                }
            }
        }

        var cached = _cachedHashes;
        if (cached.Length == 0) return false;
        var candidateHash = SHA256.HashData(Encoding.UTF8.GetBytes(providedApiKey));
        var matched = false;
        for (var i = 0; i < cached.Length; i++)
        {
            if (CryptographicOperations.FixedTimeEquals(candidateHash, cached[i]))
                matched = true;
        }
        return matched;
    }

    /// <summary>True when at least one API key is configured.</summary>
    public bool HasConfiguredKeys
    {
        get
        {
            var keys = Shark.SharkOption.ApiKeys;
            return keys is { Length: > 0 };
        }
    }

    private void RebuildCache(string[]? keys)
    {
        if (keys == null || keys.Length == 0)
        {
            _cachedHashes = [];
            return;
        }
        var hashes = new byte[keys.Length][];
        for (var i = 0; i < keys.Length; i++)
            hashes[i] = SHA256.HashData(Encoding.UTF8.GetBytes(keys[i]));
        _cachedHashes = hashes;
    }
}
