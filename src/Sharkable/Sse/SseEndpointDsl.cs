using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Metadata;

namespace Sharkable;

/// <summary>
/// Marks an endpoint as a Server-Sent Events stream. Consumed by
/// <see cref="SseEndpointDsl.SharkSse"/> to skip idempotency buffering and
/// declare the <c>text/event-stream</c> response in OpenAPI.
/// </summary>
internal sealed class SseMetadata
{
}

/// <summary>
/// Endpoint DSL for Server-Sent Events streams. Usage:
/// <code>
/// app.MapGet("stream", (CancellationToken ct) =&gt; Results.Extensions.Sse(...)).SharkSse();
/// </code>
/// The DSL:
/// <list type="bullet">
/// <item>excludes the endpoint from idempotency buffering (streams cannot be replayed),</item>
/// <item>declares the <c>text/event-stream</c> response in the generated OpenAPI document,</item>
/// <item>relies on the pipeline's existing <c>text/event-stream</c> guards (ETag and
/// cache-profile middleware skip streaming responses by content type) so events
/// reach clients unbuffered.</item>
/// </list>
/// </summary>
public static class SseEndpointDsl
{
    /// <summary>
    /// Configures an endpoint as an SSE stream: skips idempotency, declares
    /// <c>text/event-stream</c> in OpenAPI, and disables response buffering.
    /// </summary>
    /// <param name="builder">The endpoint being configured.</param>
    public static IEndpointConventionBuilder SharkSse(this IEndpointConventionBuilder builder)
    {
        builder.WithMetadata(new SseMetadata());
        builder.WithMetadata(new NoIdempotencyMetadata());
        // Microsoft.AspNetCore.OpenApi builds response content from
        // ProducesResponseTypeMetadata; a custom IProducesResponseTypeMetadata
        // with a null Type is ignored, so use the concrete type.
        builder.WithMetadata(new ProducesResponseTypeMetadata(200, typeof(string), ["text/event-stream"]));
        return builder;
    }
}
