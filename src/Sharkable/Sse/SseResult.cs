using Microsoft.AspNetCore.Http;

namespace Sharkable;

/// <summary>
/// <see cref="IResult"/> that streams Server-Sent Events to the client.
/// Created via <c>Results.Extensions.Sse(handler)</c> — the .NET 9+ pattern for
/// custom <see cref="IResult"/> types.
/// </summary>
/// <remarks>
/// Sets <c>Content-Type: text/event-stream</c> and <c>Cache-Control: no-cache</c>,
/// then invokes the handler with an <see cref="SseEventWriter"/> bound to the
/// response body. AOT-safe: no reflection, no JSON serialization.
/// </remarks>
public sealed class SseResult : IResult
{
    private readonly Func<SseEventWriter, CancellationToken, Task> _handler;

    /// <summary>
    /// Creates an SSE result that invokes <paramref name="handler"/> for each request.
    /// </summary>
    /// <param name="handler">Streams events through the provided <see cref="SseEventWriter"/>.</param>
    public SseResult(Func<SseEventWriter, CancellationToken, Task> handler)
    {
        _handler = handler ?? throw new ArgumentNullException(nameof(handler));
    }

    /// <inheritdoc />
    public async Task ExecuteAsync(HttpContext httpContext)
    {
        var response = httpContext.Response;
        response.ContentType = "text/event-stream";
        response.Headers.CacheControl = "no-cache";
        // Disable any buffering so events reach the client immediately.
        response.Headers["X-Accel-Buffering"] = "no";

        await response.StartAsync(httpContext.RequestAborted);

        var writer = new SseEventWriter(response.Body, httpContext.RequestAborted);
        try
        {
            await _handler(writer, httpContext.RequestAborted);
        }
        finally
        {
            // Flush any remaining bytes even when the handler aborts. Use
            // CancellationToken.None inside a guard so a client disconnect
            // during flush cannot mask the handler's original exception.
            try
            {
                await response.Body.FlushAsync(CancellationToken.None);
            }
            catch (Exception)
            {
                // Client already gone — nothing left to deliver.
            }
        }
    }
}

/// <summary>
/// Extension methods exposing <see cref="SseResult"/> through <c>Results.Extensions</c>.
/// </summary>
public static class SseResultExtensions
{
    /// <summary>
    /// Returns an SSE (Server-Sent Events) result. Usage:
    /// <code>
    /// app.MapGet("stream", (CancellationToken ct) =&gt; Results.Extensions.Sse(async (w, ct) =&gt;
    /// {
    ///     await w.WriteEventAsync("hello", eventType: "message");
    ///     await w.WriteCommentAsync("keep-alive");
    /// }));
    /// </code>
    /// </summary>
    /// <param name="_">The <see cref="IResultExtensions"/> marker (always <c>Results.Extensions</c>).</param>
    /// <param name="handler">Receives an <see cref="SseEventWriter"/> and streams events to the client.</param>
    public static IResult Sse(this IResultExtensions _, Func<SseEventWriter, CancellationToken, Task> handler)
        => new SseResult(handler);
}
