using System.Net;
using Microsoft.AspNetCore.Http;

namespace Sharkable;

/// <summary>
/// Extension methods for wrapping values in unified results and returning <see cref="IResult"/>.
/// Respects <see cref="SharkOption.UnifiedResultFactory"/> when set.
/// </summary>
public static class UnifiedResultExtension
{
    /// <summary>Creates an <see cref="IUnifiedResult"/> using the configured <see cref="SharkOption.UnifiedResultFactory"/> (or default).</summary>
    internal static IUnifiedResult CreateResult(object? data, string? errorMessage, int statusCode)
    {
        var factory = UnifiedResultFactoryHelper.ResolveFactory();
        return factory.Create(data, errorMessage, statusCode);
    }

    /// <summary>Wraps data in a unified result with optional error and status code.</summary>
    public static IUnifiedResult? AsUnifiedResult<TResult>(this TResult? data,
        string? errors = null,
        HttpStatusCode statusCode = HttpStatusCode.OK)
    {
        return data == null ? null : CreateResult(data, errors, (int)statusCode);
    }

    /// <summary>Wraps an error string in a unified result with the given status code.</summary>
    public static IUnifiedResult? AsUnifiedError(this string? errors,
        HttpStatusCode statusCode = HttpStatusCode.OK)
    {
        return errors == null ? null : CreateResult(null, errors, (int)statusCode);
    }

    /// <summary>Returns a 200 <see cref="IResult"/> with the given data wrapped in a unified result.</summary>
    public static IResult AsOkResult<TResult>(this TResult? data,
        string? errors = null,
        HttpStatusCode statusCode = HttpStatusCode.OK)
    {
        return data == null ? Results.Ok() : Results.Ok(data.AsUnifiedResult(errors, statusCode));
    }

    /// <summary>Returns a 400 <see cref="IResult"/> with the given error message.</summary>
    public static IResult AsBadRequest(this string? errors,
        HttpStatusCode statusCode = HttpStatusCode.BadRequest)
    {
        return errors == null ? Results.StatusCode((int)statusCode) : Results.Json(errors.AsUnifiedError(statusCode), statusCode: (int)statusCode);
    }

    /// <summary>
    /// Returns a 401 <see cref="IResult"/> with the given error message.
    /// BUG-135: the previous implementation dropped the message and ignored
    /// <paramref name="statusCode"/> — every member of this family now emits
    /// the unified error envelope.
    /// </summary>
    public static IResult AsUnauthorized(this string? errors,
        HttpStatusCode statusCode = HttpStatusCode.Unauthorized)
    {
        return errors == null ? Results.StatusCode((int)statusCode) : Results.Json(errors.AsUnifiedError(statusCode), statusCode: (int)statusCode);
    }

    /// <summary>Returns a 404 <see cref="IResult"/> with the given error message.</summary>
    public static IResult AsNotFound(this string? errors,
        HttpStatusCode statusCode = HttpStatusCode.NotFound)
    {
        return errors == null ? Results.StatusCode((int)statusCode) : Results.Json(errors.AsUnifiedError(statusCode), statusCode: (int)statusCode);
    }

    /// <summary>
    /// Returns a 403 <see cref="IResult"/> with the given error message.
    /// BUG-135: the previous implementation dropped the message and ignored
    /// <paramref name="statusCode"/>.
    /// </summary>
    public static IResult AsForbidden(this string? errors,
        HttpStatusCode statusCode = HttpStatusCode.Forbidden)
    {
        return errors == null ? Results.StatusCode((int)statusCode) : Results.Json(errors.AsUnifiedError(statusCode), statusCode: (int)statusCode);
    }

    /// <summary>Returns a 409 <see cref="IResult"/> with the given error message.</summary>
    public static IResult AsConflict(this string? errors,
        HttpStatusCode statusCode = HttpStatusCode.Conflict)
    {
        return errors == null ? Results.StatusCode((int)statusCode) : Results.Json(errors.AsUnifiedError(statusCode), statusCode: (int)statusCode);
    }

    /// <summary>Returns a 204 <see cref="IResult"/>.</summary>
    public static IResult AsNoContent()
    {
        return Results.NoContent();
    }

    /// <summary>Returns a 201 <see cref="IResult"/> with the given data wrapped in a unified result.</summary>
    public static IResult AsCreated<TResult>(this TResult? data,
        string? uri = null,
        string? errors = null)
    {
        // BUG-134: always honor the 201 status — the previous null-uri path
        // downgraded to 200 OK.
        var result = data.AsUnifiedResult(errors, HttpStatusCode.Created);
        if (result == null) return Results.StatusCode(201);
        return uri != null ? Results.Created(uri, result) : Results.Json(result, statusCode: 201);
    }

    /// <summary>Returns a 202 <see cref="IResult"/> with the given data and optional location URI.</summary>
    public static IResult AsAccepted<TResult>(this TResult? data,
        string? uri = null,
        string? errors = null)
    {
        var result = data.AsUnifiedResult(errors, HttpStatusCode.Accepted);
        if (result == null) return Results.StatusCode(202);
        return uri != null ? Results.Accepted(uri, result) : Results.Json(result, statusCode: 202);
    }

    /// <summary>Returns a 405 <see cref="IResult"/> with the given error message.</summary>
    public static IResult AsMethodNotAllowed(this string? errors,
        HttpStatusCode statusCode = HttpStatusCode.MethodNotAllowed)
    {
        return errors == null ? Results.StatusCode((int)statusCode) : Results.Json(errors.AsUnifiedError(statusCode), statusCode: (int)statusCode);
    }

    /// <summary>Returns a 406 <see cref="IResult"/> with the given error message.</summary>
    public static IResult AsNotAcceptable(this string? errors,
        HttpStatusCode statusCode = HttpStatusCode.NotAcceptable)
    {
        return errors == null ? Results.StatusCode((int)statusCode) : Results.Json(errors.AsUnifiedError(statusCode), statusCode: (int)statusCode);
    }

    /// <summary>Returns a 410 <see cref="IResult"/> with the given error message.</summary>
    public static IResult AsGone(this string? errors,
        HttpStatusCode statusCode = HttpStatusCode.Gone)
    {
        return errors == null ? Results.StatusCode((int)statusCode) : Results.Json(errors.AsUnifiedError(statusCode), statusCode: (int)statusCode);
    }

    /// <summary>Returns a 415 <see cref="IResult"/> with the given error message.</summary>
    public static IResult AsUnsupportedMediaType(this string? errors,
        HttpStatusCode statusCode = HttpStatusCode.UnsupportedMediaType)
    {
        return errors == null ? Results.StatusCode((int)statusCode) : Results.Json(errors.AsUnifiedError(statusCode), statusCode: (int)statusCode);
    }

    /// <summary>Returns a 422 <see cref="IResult"/> with the given error message.</summary>
    public static IResult AsUnprocessableEntity(this string? errors,
        HttpStatusCode statusCode = HttpStatusCode.UnprocessableEntity)
    {
        return errors == null ? Results.StatusCode((int)statusCode) : Results.Json(errors.AsUnifiedError(statusCode), statusCode: (int)statusCode);
    }

    /// <summary>Returns a 429 <see cref="IResult"/> with the given error message.</summary>
    public static IResult AsTooManyRequests(this string? errors,
        HttpStatusCode statusCode = HttpStatusCode.TooManyRequests)
    {
        return errors == null ? Results.StatusCode((int)statusCode) : Results.Json(errors.AsUnifiedError(statusCode), statusCode: (int)statusCode);
    }

    /// <summary>Returns a 500 <see cref="IResult"/> with the given error message.</summary>
    public static IResult AsInternalServerError(this string? errors,
        HttpStatusCode statusCode = HttpStatusCode.InternalServerError)
    {
        return errors == null ? Results.StatusCode((int)statusCode) : Results.Json(errors.AsUnifiedError(statusCode), statusCode: (int)statusCode);
    }

    /// <summary>Returns a 501 <see cref="IResult"/> with the given error message.</summary>
    public static IResult AsNotImplemented(this string? errors,
        HttpStatusCode statusCode = HttpStatusCode.NotImplemented)
    {
        return errors == null ? Results.StatusCode((int)statusCode) : Results.Json(errors.AsUnifiedError(statusCode), statusCode: (int)statusCode);
    }

    /// <summary>Returns a 502 <see cref="IResult"/> with the given error message.</summary>
    public static IResult AsBadGateway(this string? errors,
        HttpStatusCode statusCode = HttpStatusCode.BadGateway)
    {
        return errors == null ? Results.StatusCode((int)statusCode) : Results.Json(errors.AsUnifiedError(statusCode), statusCode: (int)statusCode);
    }

    /// <summary>Returns a 503 <see cref="IResult"/> with the given error message.</summary>
    public static IResult AsServiceUnavailable(this string? errors,
        HttpStatusCode statusCode = HttpStatusCode.ServiceUnavailable)
    {
        return errors == null ? Results.StatusCode((int)statusCode) : Results.Json(errors.AsUnifiedError(statusCode), statusCode: (int)statusCode);
    }

    /// <summary>Returns a 504 <see cref="IResult"/> with the given error message.</summary>
    public static IResult AsGatewayTimeout(this string? errors,
        HttpStatusCode statusCode = HttpStatusCode.GatewayTimeout)
    {
        return errors == null ? Results.StatusCode((int)statusCode) : Results.Json(errors.AsUnifiedError(statusCode), statusCode: (int)statusCode);
    }

    /// <summary>Returns an <see cref="IResult"/> with the given custom status code and error message.</summary>
    public static IResult AsStatus(this string? errors,
        HttpStatusCode statusCode)
    {
        return errors == null ? Results.StatusCode((int)statusCode) : Results.Json(errors.AsUnifiedError(statusCode), statusCode: (int)statusCode);
    }

    /// <summary>Returns an <see cref="IResult"/> with the given custom status code and data wrapped in a unified result.</summary>
    public static IResult AsStatus<TResult>(this TResult? data,
        HttpStatusCode statusCode,
        string? errors = null)
    {
        var result = data.AsUnifiedResult(errors, statusCode);
        return result == null ? Results.StatusCode((int)statusCode) : Results.Json(result, statusCode: (int)statusCode);
    }
}
