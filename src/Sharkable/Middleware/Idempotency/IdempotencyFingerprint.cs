using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;

namespace Sharkable;

/// <summary>
/// Computes the SHA-256 fingerprint used to detect "same Idempotency-Key,
/// different request payload". Hashes <c>userId + "\n" + method + "\n" + path + "\n" + body</c>
/// using incremental hashing so the full body need not be materialized.
/// </summary>
internal static class IdempotencyFingerprint
{
    /// <summary>
    /// Computes the lower-case hex SHA-256 of the request identity.
    /// </summary>
    /// <param name="userId">
    /// Stable user identifier (e.g. <c>User.Identity.Name</c> or the literal
    /// <c>"&lt;anon&gt;"</c> for unauthenticated callers). Mixed into the hash
    /// so a replayed response cannot leak across users (SHARK-SEC-M021).
    /// </param>
    /// <param name="method">HTTP method (case-insensitive; normalized to upper).</param>
    /// <param name="path">Request path. Null/empty is treated as <c>"/"</c>.</param>
    /// <param name="body">Raw request body bytes.</param>
    /// <returns>64-character lower-case hex SHA-256 digest of <c>userId + "\n" + method + "\n" + path + "\n" + body</c>.</returns>
    public static string Compute(string userId, string method, PathString path, ReadOnlySpan<byte> body)
    {
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var userBytes = Encoding.UTF8.GetBytes(userId ?? "<anon>");
        sha.AppendData(userBytes);
        sha.AppendData(new byte[] { (byte)'\n' });
        var methodBytes = Encoding.ASCII.GetBytes(method.ToUpperInvariant());
        sha.AppendData(methodBytes);
        sha.AppendData(new byte[] { (byte)'\n' });
        var pathValue = path.Value ?? "/";
        sha.AppendData(Encoding.UTF8.GetBytes(pathValue));
        sha.AppendData(new byte[] { (byte)'\n' });
        sha.AppendData(body);
        return Convert.ToHexString(sha.GetHashAndReset()).ToLowerInvariant();
    }

    /// <summary>
    /// Computes a SHA-256 fingerprint of <c>userId + "\n" + method + "\n" + path + "\n" + contentLength + "\n" + body</c>,
    /// reading the body incrementally from <paramref name="body"/> up to <paramref name="maxBodySize"/>
    /// bytes. <paramref name="contentLength"/> is included to prevent fingerprint collisions between
    /// bodies of different sizes whose first N bytes are identical. This avoids allocating a full
    /// buffer for the body, preventing OOM from attacker-controlled <c>Content-Length</c> values.
    /// Note: the hash input differs from <see cref="Compute(string,string,PathString,ReadOnlySpan{byte})"/>,
    /// which omits the content length field.
    /// </summary>
    /// <param name="userId">
    /// Stable user identifier (e.g. <c>User.Identity.Name</c> or the literal
    /// <c>"&lt;anon&gt;"</c>). Mixed into the hash so replays cannot cross users
    /// (SHARK-SEC-M021).
    /// </param>
    /// <param name="method">HTTP method (case-insensitive; normalized to upper).</param>
    /// <param name="path">Request path. Null/empty is treated as <c>"/"</c>.</param>
    /// <param name="body">Request body stream. Read incrementally — never materialized as a full buffer.</param>
    /// <param name="maxBodySize">Maximum number of body bytes to include in the hash. Acts as a hard
    /// upper bound to prevent OOM when an attacker advertises a huge <c>Content-Length</c>.</param>
    /// <param name="contentLength">The <c>Content-Length</c> of the request, or <c>-1</c> when the
    /// length is unknown (e.g. <c>Transfer-Encoding: chunked</c>). The sentinel <c>-1</c> is mixed
    /// into the hash so chunked requests cannot collide with fixed-length requests of identical body
    /// content, nor with one another when their bodies differ.</param>
    /// <returns>64-character lower-case hex SHA-256 digest.</returns>
    public static async Task<string> ComputeAsync(
        string userId, string method, PathString path, Stream body, int maxBodySize, long contentLength)
    {
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var userBytes = Encoding.UTF8.GetBytes(userId ?? "<anon>");
        sha.AppendData(userBytes);
        sha.AppendData(new byte[] { (byte)'\n' });
        var methodBytes = Encoding.ASCII.GetBytes(method.ToUpperInvariant());
        sha.AppendData(methodBytes);
        sha.AppendData(new byte[] { (byte)'\n' });
        var pathValue = path.Value ?? "/";
        sha.AppendData(Encoding.UTF8.GetBytes(pathValue));
        sha.AppendData(new byte[] { (byte)'\n' });
        sha.AppendData(Encoding.ASCII.GetBytes(contentLength.ToString()));
        sha.AppendData(new byte[] { (byte)'\n' });

        int remaining = maxBodySize;
        byte[] buffer = new byte[4096];
        while (remaining > 0)
        {
            var chunkSize = Math.Min(buffer.Length, remaining);
            int read = await body.ReadAsync(buffer.AsMemory(0, chunkSize));
            if (read == 0) break;
            sha.AppendData(buffer.AsSpan(0, read));
            remaining -= read;
        }

        return Convert.ToHexString(sha.GetHashAndReset()).ToLowerInvariant();
    }
}