using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Sharkable;

/// <summary>
/// Middleware that adds ETag support to GET/HEAD responses.
/// On first request: computes SHA256 hash of the response body, stores it as ETag.
/// On subsequent requests with matching <c>If-None-Match</c>: returns 304 Not Modified.
/// </summary>
internal sealed class ETagMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ETagOptions _options;
    private readonly ILogger<ETagMiddleware> _logger;

    public ETagMiddleware(RequestDelegate next, ETagOptions options, ILogger<ETagMiddleware> logger)
    {
        _next = next;
        _options = options;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (!_options.CacheableMethods.Contains(context.Request.Method) || ShouldSkip(context.Request.Path))
        {
            await _next(context);
            return;
        }

        var originalBody = context.Response.Body;
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var counting = new CountingResponseBody(originalBody, _options.MaxResponseSize, hash);

        context.Response.Body = counting;
        var limitExceeded = false;
        try
        {
            await _next(context);
        }
        catch (ResponseSizeExceededException)
        {
            limitExceeded = true;
        }
        finally
        {
            context.Response.Body = originalBody;
        }

        if (IsUncacheableStatus(context.Response.StatusCode))
        {
            await counting.FlushAsync(context.RequestAborted);
            return;
        }

        // BUG-113: never buffer or ETag streaming responses — the spool would
        // grow without bound and the ETag would be meaningless.
        if (context.Response.ContentType?.StartsWith("text/event-stream", StringComparison.OrdinalIgnoreCase) == true)
        {
            await counting.FlushAsync(context.RequestAborted);
            return;
        }

        // BUG-113: if the endpoint flushed mid-response (SSE, chunked
        // streaming), the response has already started — mutating headers or
        // the status code would throw InvalidOperationException and turn a
        // successful stream into a 500.
        if (context.Response.HasStarted)
        {
            await counting.FlushAsync(context.RequestAborted);
            return;
        }

        if (limitExceeded || counting.BytesWritten > _options.MaxResponseSize)
        {
            _logger.LogWarning(
                "ETag skipped for {Method} {Path}: response body {Bytes} bytes exceeds MaxResponseSize {Cap}",
                context.Request.Method, context.Request.Path, counting.BytesWritten, _options.MaxResponseSize);
            await counting.FlushAsync(context.RequestAborted);
            return;
        }

        var hashBytes = hash.GetHashAndReset();
        var hashHex = ToHex(hashBytes);
        var etag = $"\"{hashHex}\"";

        context.Response.Headers["ETag"] = etag;
        // BUG-113: do not overwrite a Cache-Control the endpoint already set
        // (e.g. no-store on sensitive GETs) — only add the default when absent.
        if (!context.Response.Headers.ContainsKey("Cache-Control"))
            context.Response.Headers["Cache-Control"] = _options.CacheControlHeader;

        if (context.Request.Headers.TryGetValue("If-None-Match", out var ifNoneMatch)
            && MatchesIfNoneMatch(ifNoneMatch, hashHex))
        {
            context.Response.StatusCode = 304;
            context.Response.ContentLength = 0;
            return;
        }

        await counting.FlushAsync(context.RequestAborted);
    }

    /// <summary>
    /// SHARK-SEC-L011: properly compare <c>If-None-Match</c> against the
    /// generated ETag per RFC 9110 §13.1.2 — handle weak ETags
    /// (<c>W/"..."</c>) by ignoring the W/ prefix on both sides, and split
    /// the comma-separated list of candidate tags. The previous
    /// implementation used <c>Trim('"')</c> on the entire header value,
    /// which only worked for a single strong candidate and silently
    /// returned the wrong answer for weak / multi-value headers.
    /// </summary>
    private static bool MatchesIfNoneMatch(Microsoft.Extensions.Primitives.StringValues headerValue, string strongHex)
    {
        var strongEtag = "\"" + strongHex + "\"";
        foreach (var raw in headerValue)
        {
            if (raw == null) continue;
            foreach (var part in raw.Split(','))
            {
                var candidate = part.Trim();
                if (candidate.Length == 0) continue;

                // Weak comparison per RFC 9110 §8.8.3.2: weak comparison
                // ignores the W/ prefix on both sides. The middleware only
                // emits strong ETags today, so this matches both.
                var normalized = candidate.StartsWith("W/", StringComparison.Ordinal)
                    ? candidate[2..]
                    : candidate;
                if (normalized == strongEtag) return true;
                // Also accept '*' as a wildcard match per RFC 9110 §13.1.2.
                if (normalized == "*") return true;
            }
        }
        return false;
    }

    private bool ShouldSkip(string path)
    {
        foreach (var exclude in _options.ExcludePaths)
        {
            if (path.StartsWith(exclude, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private bool IsUncacheableStatus(int statusCode)
        => _options.ShouldSkipStatus(statusCode);

    private static string ToHex(byte[] bytes)
    {
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    /// <summary>
    /// Wraps a response stream, forwarding bytes to the inner stream while
    /// counting bytes written, incrementally updating a hash, and throwing
    /// <see cref="ResponseSizeExceededException"/> when the byte count exceeds
    /// the configured cap. Keeps an internal spool so partial writes past the
    /// cap can still be flushed to the client with no ETag header.
    /// </summary>
    private sealed class CountingResponseBody : Stream, IDisposable
    {
        private readonly Stream _inner;
        private readonly long _maxBytes;
        private readonly IncrementalHash _hash;
        private readonly MemoryStream _spool = new();
        private long _bytesWritten;

        public CountingResponseBody(Stream inner, long maxBytes, IncrementalHash hash)
        {
            _inner = inner;
            _maxBytes = maxBytes;
            _hash = hash;
        }

        public long BytesWritten => _bytesWritten;

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() => _inner.Flush();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                _spool.Dispose();
            base.Dispose(disposing);
        }

        public override async Task FlushAsync(CancellationToken cancellationToken)
        {
            // SHARK-SEC-012 follow-up: only flush the un-flushed portion of the spool,
            // then truncate it. The previous implementation copied from position 0 on
            // every call and disposed the spool, which duplicated pre-cap bytes on
            // any response that flushed more than once and threw on writes after the
            // first flush.
            if (_spool.Length == 0)
            {
                await _inner.FlushAsync(cancellationToken);
                return;
            }
            _spool.Position = 0;
            await _spool.CopyToAsync(_inner, cancellationToken);
            _spool.SetLength(0);
            await _inner.FlushAsync(cancellationToken);
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count)
        {
            _bytesWritten += count;
            if (_bytesWritten > _maxBytes)
            {
                // Spool the over-the-limit remainder so FlushAsync can still
                // deliver the body; the caller will detect limitExceeded and
                // skip ETag generation.
                _spool.Write(buffer, offset, count);
                throw new ResponseSizeExceededException();
            }
            _hash.AppendData(buffer, offset, count);
            _spool.Write(buffer, offset, count);
        }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            _bytesWritten += buffer.Length;
            if (_bytesWritten > _maxBytes)
            {
                _spool.Write(buffer.Span);
                throw new ResponseSizeExceededException();
            }
            _hash.AppendData(buffer.Span);
            _spool.Write(buffer.Span);
            return ValueTask.CompletedTask;
        }
    }
}