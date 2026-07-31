using System.Globalization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Sharkable;

/// <summary>
/// Middleware that enforces idempotency for unsafe HTTP methods via the
/// <c>Idempotency-Key</c> header. See
/// <c>docs/superpowers/specs/2026-06-26-idempotency-middleware-design.md</c>.
/// </summary>
internal sealed class SharkIdempotencyMiddleware
{
    private readonly RequestDelegate _next;
    private readonly IIdempotencyStore _store;
    private readonly SharkIdempotencyOptions _options;
    private readonly ILogger<SharkIdempotencyMiddleware> _logger;
    private readonly HashSet<string> _unsafeMethodNames;
    private readonly ISharkMetrics? _metrics;

    public SharkIdempotencyMiddleware(
        RequestDelegate next,
        IIdempotencyStore store,
        SharkIdempotencyOptions options,
        ILogger<SharkIdempotencyMiddleware> logger,
        ISharkMetrics? metrics = null)
    {
        _next = next;
        _store = store;
        _options = options;
        _logger = logger;
        _metrics = metrics;
        _unsafeMethodNames = new HashSet<string>(
            options.UnsafeMethods.Select(m => m.Method),
            StringComparer.OrdinalIgnoreCase);
    }

    private bool IsUnsafeMethod(string method) => _unsafeMethodNames.Contains(method);

    public async Task InvokeAsync(HttpContext context)
    {
        // 0. Check if idempotency applies to this endpoint.
        // Global enable → all endpoints participate (unless NoIdempotency).
        // Per-endpoint metadata: collected regardless of global enable state.
        var perEndpointMeta = context.GetEndpoint()?.Metadata.GetMetadata<SharkIdempotentMetadata>();

        // Global disable → only [SharkIdempotent] endpoints participate.
        if (!Shark.SharkOption.EnableIdempotency)
        {
            if (perEndpointMeta == null)
            {
                await _next(context);
                return;
            }
        }

        // 1. Header present?
        if (!context.Request.Headers.TryGetValue(_options.HeaderName, out var headerValues)
            || headerValues.Count == 0
            || string.IsNullOrWhiteSpace(headerValues[0]))
        {
            await _next(context);
            return;
        }

        var key = headerValues[0]!;

        // 2. Key valid?
        if (!_options.IsValidKey(key))
        {
            await WriteUnified(context, 400, "invalid_idempotency_key",
                $"Key must be {SharkIdempotencyOptions.MinKeyLength}..{_options.MaxKeyLength} printable ASCII characters.");
            return;
        }

        // 3. Method eligible? (compare strings to avoid HttpMethod allocation)
        if (!IsUnsafeMethod(context.Request.Method))
        {
            await _next(context);
            return;
        }

        // 4. Try to reserve the slot.
        if (!await _store.TryReserveAsync(key, _options.InFlightTtl))
        {
            // Slot was already taken. Look at its state.
            var existing = await _store.GetAsync(key);
            switch (existing)
            {
                case IdempotencyInFlight:
                    context.Response.Headers["Retry-After"] = _options.RetryAfterSeconds.ToString(CultureInfo.InvariantCulture);
                    await WriteUnified(context, 409, "idempotency_in_progress",
                        "An identical request is already in progress; retry after 1 second.");
                    return;

                case IdempotencyHit hit:
                    _metrics?.IdempotencyHit.Add(1);
                    var fingerprint = await ComputeFingerprint(context);
                    if (hit.Record.Fingerprint != fingerprint)
                    {
                        _metrics?.IdempotencyConflict.Add(1);
                        await WriteUnified(context, 422, "idempotency_key_conflict",
                            "Idempotency-Key was reused with a different request payload.");
                        return;
                    }
                    await Replay(context, hit.Record);
                    return;

                default:
                    // Race: placeholder expired between TryReserve and Get. Fall through and execute.
                    break;
            }
        }

        // 5. We own the in-flight slot. Check for streaming / SSE endpoints
        // before buffering — replacing Response.Body with a MemoryStream
        // silently breaks flush-to-client behavior for SSE and long-polling.
        _metrics?.IdempotencyMiss.Add(1);
        if (context.GetEndpoint()?.Metadata.GetMetadata<NoIdempotencyMetadata>() is not null)
        {
            await _store.ReleaseAsync(key);
            await _next(context);
            return;
        }

        // 5a. Compute the fingerprint BEFORE executing the handler, from a
        // buffered (rewound) request body. The stored record must match what
        // a replay will compute — the previous implementation hashed the body
        // after _next() had already consumed it (Kestrel's request stream is
        // not seekable), so every replay of a body-carrying request mismatched
        // and returned 422 idempotency_key_conflict (BUG-101).
        var storedFingerprint = await ComputeFingerprint(context);

        // 6. Execute downstream with response buffering.
        // SHARK-SEC-M008: use a counting stream wrapper around a bounded
        // MemoryStream so the peak allocation is capped at MaxResponseSize
        // bytes. The previous MemoryStream grew unbounded; the post-hoc
        // Length > MaxResponseSize check could only react after OOM had
        // already occurred. The wrapper throws ResponseSizeExceededException
        // (caught in the finally block) the moment the cap is hit.
        var originalBody = context.Response.Body;
        await using var buffer = new MemoryStream();
        await using var counting = new CountingResponseBody(buffer, _options.MaxResponseSize);
        context.Response.Body = counting;
        var limitExceeded = false;
        try
        {
            await _next(context);
        }
        catch (ResponseSizeExceededException)
        {
            // SHARK-SEC-M008: the response body exceeded the cap. Discard
            // what we buffered, release the in-flight slot, and emit a 500
            // explaining why the response cannot be cached.
            limitExceeded = true;
        }
        finally
        {
            if (!context.Response.HasStarted)
            {
                context.Response.Body = originalBody;
            }
        }

        if (limitExceeded)
        {
            _logger.LogWarning(
                "Idempotency response for key {Key} exceeds {Max} bytes; " +
                "rejecting and releasing in-flight slot.",
                key, _options.MaxResponseSize);
            await _store.ReleaseAsync(key);
            context.Response.StatusCode = 500;
            await WriteUnified(context, 500, "idempotency_response_too_large",
                $"Response body exceeded {_options.MaxResponseSize} bytes; " +
                "idempotent replay is not available for this response.");
            return;
        }

        // 6a. Successful (cacheable) responses -> store and forward.
        if (_options.ShouldCacheStatus(context.Response.StatusCode))
        {
            ArraySegment<byte> segment = buffer.TryGetBuffer(out var buf)
                ? buf
                : new ArraySegment<byte>(buffer.ToArray());
            var bytes = segment.Count == segment.Array!.Length
                ? segment.Array
                : segment.ToArray();
            // BUG-102: reset the position first — CopyToAsync starts from the
            // current position and the buffer was left at the end of the
            // written response, which delivered an empty body to the caller.
            buffer.Position = 0;
            await buffer.CopyToAsync(originalBody);

            var record = new IdempotencyRecord(
                key,
                storedFingerprint,
                context.Response.StatusCode,
                context.Response.ContentType ?? "application/octet-stream",
                bytes,
                DateTimeOffset.UtcNow);
            var effectiveTtl = perEndpointMeta?.TtlSeconds != null
                ? TimeSpan.FromSeconds(perEndpointMeta.TtlSeconds.Value)
                : _options.Ttl;
            await _store.StoreAsync(key, record, effectiveTtl);
        }
        else
        {
            // 429 or 5xx: do not cache. Release slot and forward body.
            await _store.ReleaseAsync(key);
            buffer.Position = 0;
            await buffer.CopyToAsync(originalBody);
        }
    }

    private async Task<string> ComputeFingerprint(HttpContext context)
    {
        var contentLength = context.Request.ContentLength;
        var maxBodySize = _options.MaxFingerprintBodySize;

        // SHARK-SEC-M021: include the authenticated user identity in the
        // fingerprint so a replayed response cannot leak across users when
        // they happen to share an Idempotency-Key + body. Unauthenticated
        // requests fall back to the stable literal "<anon>" so anonymous
        // requests still de-duplicate within the same key+body bucket.
        var userId = context.User?.Identity?.IsAuthenticated == true
            ? (context.User.Identity.Name ?? context.User.FindFirst("sub")?.Value ?? "<auth>")
            : "<anon>";

        // Content-Length: 0 (or negative) — no body. Use the empty-body path so its
        // fingerprint stays distinct from any chunked request.
        if (contentLength is not null && contentLength.Value <= 0)
        {
            return IdempotencyFingerprint.Compute(
                userId,
                context.Request.Method,
                context.Request.Path,
                ReadOnlySpan<byte>.Empty);
        }

        // Content-Length absent — typically Transfer-Encoding: chunked. We cannot
        // trust the body to be empty, so hash incrementally up to maxBodySize and
        // pass -1 as the contentLength sentinel. The sentinel is mixed into the
        // hash, ensuring chunked requests never collide with one another (different
        // bodies → different fingerprints) or with the empty-body path.
        var body = context.Request.Body;
        if (!body.CanSeek)
        {
            // BUG-101: Kestrel's request stream is not seekable and the endpoint
            // consumes it. Buffer the body (bounded by MaxFingerprintBodySize, the
            // same cap the hash uses) so the fingerprint covers the actual payload
            // and the endpoint can still re-read it from the start afterwards.
            context.Request.EnableBuffering(_options.MaxFingerprintBodySize);
            body = context.Request.Body;
        }

        body.Position = 0;

        var bytesToHash = maxBodySize;
        var hashContentLength = contentLength ?? -1;
        if (contentLength is not null && contentLength.Value < bytesToHash)
            bytesToHash = (int)contentLength.Value;

        try
        {
            return await IdempotencyFingerprint.ComputeAsync(
                userId,
                context.Request.Method,
                context.Request.Path,
                body,
                bytesToHash,
                hashContentLength);
        }
        finally
        {
            // Rewind so downstream model binding reads the body from the start.
            if (body.CanSeek)
                body.Position = 0;
        }
    }

    private async Task Replay(HttpContext context, IdempotencyRecord record)
    {
        context.Response.StatusCode = record.StatusCode;
        context.Response.ContentType = record.ContentType;
        context.Response.Headers[_options.ReplayedHeaderName] = "true";
        await context.Response.Body.WriteAsync(record.Body);
    }

    private static Task WriteUnified(
        HttpContext context, int status, string code, string message)
    {
        var factory = UnifiedResultFactoryHelper.ResolveFactory();
        var errorMessage = $"[{code}] {message}";
        var result = factory.Create(data: null, errorMessage: errorMessage, statusCode: status);
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/json";
        return context.Response.WriteAsJsonAsync(result, result.GetType());
    }

    /// <summary>
    /// Wraps the idempotency response-body buffer so the total number of
    /// bytes written can be capped. Once the count exceeds
    /// <see cref="SharkIdempotencyOptions.MaxResponseSize"/>, every further
    /// write throws <see cref="ResponseSizeExceededException"/> so the
    /// downstream pipeline can react immediately rather than after the
    /// response has already exhausted memory (SHARK-SEC-M008).
    /// </summary>
    private sealed class CountingResponseBody : Stream
    {
        private readonly MemoryStream _inner;
        private readonly long _maxBytes;
        private long _bytesWritten;

        public CountingResponseBody(MemoryStream inner, long maxBytes)
        {
            _inner = inner;
            _maxBytes = maxBytes;
        }

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
        public override Task FlushAsync(CancellationToken cancellationToken) => _inner.FlushAsync(cancellationToken);

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count)
        {
            _bytesWritten += count;
            if (_bytesWritten > _maxBytes)
                throw new ResponseSizeExceededException();
            _inner.Write(buffer, offset, count);
        }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            _bytesWritten += buffer.Length;
            if (_bytesWritten > _maxBytes)
                return ValueTask.FromException(new ResponseSizeExceededException());
            return _inner.WriteAsync(buffer, cancellationToken);
        }
    }
}
