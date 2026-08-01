using System.Text;

namespace Sharkable;

/// <summary>
/// Writes Server-Sent Events to an <see cref="HttpResponse"/> body stream.
/// AOT-safe (plain UTF-8 byte writes — no JSON serialization, no reflection).
/// </summary>
/// <remarks>
/// Wire format per the SSE spec (https://html.spec.whatwg.org/multipage/server-sent-events.html):
/// fields are <c>event</c>, <c>data</c>, <c>id</c>, <c>retry</c> and comments
/// (<c>:</c> lines); each event is terminated by a blank line.
/// </remarks>
public sealed class SseEventWriter
{
    private static readonly byte[] FieldEvent = "event: "u8.ToArray();
    private static readonly byte[] FieldData = "data: "u8.ToArray();
    private static readonly byte[] FieldId = "id: "u8.ToArray();
    private static readonly byte[] FieldRetry = "retry: "u8.ToArray();
    private static readonly byte[] CommentPrefix = ": "u8.ToArray();
    private static readonly byte[] NewLine = "\n"u8.ToArray();

    private readonly Stream _body;
    private readonly CancellationToken _cancellationToken;

    internal SseEventWriter(Stream body, CancellationToken cancellationToken)
    {
        _body = body;
        _cancellationToken = cancellationToken;
    }

    /// <summary>
    /// Writes a complete SSE event: optional <c>id</c>, optional <c>event</c> type,
    /// and the payload as one or more <c>data:</c> lines (multi-line payloads are
    /// split per the spec), followed by the terminating blank line.
    /// </summary>
    /// <param name="data">Event payload. May contain newlines; each line is emitted as its own <c>data:</c> field.</param>
    /// <param name="eventType">Optional event type (<c>event:</c> field).</param>
    /// <param name="id">Optional event id (<c>id:</c> field) — clients resume from this via <c>Last-Event-ID</c>.</param>
    /// <param name="retryMs">Optional reconnection time in milliseconds (<c>retry:</c> field).</param>
    /// <exception cref="ArgumentNullException"><paramref name="data"/> is <c>null</c>.</exception>
    public async Task WriteEventAsync(string data, string? eventType = null, string? id = null, int? retryMs = null)
    {
        ArgumentNullException.ThrowIfNull(data);

        if (id != null)
            await WriteFieldAsync(FieldId, id);

        if (eventType != null)
            await WriteFieldAsync(FieldEvent, eventType);

        if (retryMs is >= 0)
            await WriteFieldAsync(FieldRetry, retryMs.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));

        // Split multi-line payloads into one data: field per line (spec §6.1).
        var lineStart = 0;
        while (lineStart <= data.Length)
        {
            var lineEnd = data.IndexOf('\n', lineStart);
            if (lineEnd < 0) lineEnd = data.Length;

            var line = data.AsSpan(lineStart, lineEnd - lineStart);
            if (line.Length > 0 && line[^1] == '\r')
                line = line[..^1];

            // Convert before any await — spans cannot cross await boundaries.
            var lineBytes = Encoding.UTF8.GetBytes(line.ToArray());
            await _body.WriteAsync(FieldData, _cancellationToken);
            await _body.WriteAsync(lineBytes, _cancellationToken);
            await _body.WriteAsync(NewLine, _cancellationToken);

            lineStart = lineEnd + 1;
        }

        // One trailing blank line terminates the event. Field lines already
        // carry their own "\n", so a single extra "\n" produces the required
        // empty line — writing "\n\n" here would emit TWO blank lines.
        await _body.WriteAsync(NewLine, _cancellationToken);
    }

    /// <summary>
    /// Writes a comment line (<c>: comment</c>). Comments are ignored by clients but
    /// are the standard keep-alive mechanism (they also detect dropped connections).
    /// Multi-line comments are emitted as one comment line per line so the
    /// <c>:</c> framing is never broken.
    /// </summary>
    public async Task WriteCommentAsync(string comment)
    {
        var lineStart = 0;
        while (lineStart <= comment.Length)
        {
            var lineEnd = comment.IndexOf('\n', lineStart);
            if (lineEnd < 0) lineEnd = comment.Length;

            var line = comment.AsSpan(lineStart, lineEnd - lineStart);
            if (line.Length > 0 && line[^1] == '\r')
                line = line[..^1];

            var lineBytes = Encoding.UTF8.GetBytes(line.ToArray());
            await _body.WriteAsync(CommentPrefix, _cancellationToken);
            await _body.WriteAsync(lineBytes, _cancellationToken);
            await _body.WriteAsync(NewLine, _cancellationToken);

            lineStart = lineEnd + 1;
        }
    }

    /// <summary>Flushes buffered bytes to the client. Call after a batch of events when low latency matters.</summary>
    public async Task FlushAsync() => await _body.FlushAsync(_cancellationToken);

    private async Task WriteFieldAsync(byte[] prefix, string value)
    {
        await _body.WriteAsync(prefix, _cancellationToken);
        await _body.WriteAsync(Encoding.UTF8.GetBytes(value), _cancellationToken);
        await _body.WriteAsync(NewLine, _cancellationToken);
    }
}
