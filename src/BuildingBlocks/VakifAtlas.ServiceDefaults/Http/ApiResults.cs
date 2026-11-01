using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using VakifAtlas.SharedKernel;

namespace VakifAtlas.ServiceDefaults.Http;

/// <summary>Writes an <see cref="ApiError"/> as problem+json.</summary>
/// <param name="error">The error to write.</param>
public sealed class ApiErrorResult(ApiError error) : IResult, IStatusCodeHttpResult
{
    /// <summary>The error to write.</summary>
    public ApiError Error { get; } = error;

    /// <summary>HTTP status code of the error.</summary>
    public int? StatusCode => Error.Status;

    /// <summary>Writes the error response.</summary>
    /// <param name="httpContext">The current request.</param>
    public async Task ExecuteAsync(HttpContext httpContext)
    {
        httpContext.Response.StatusCode = Error.Status;
        var problem = new ProblemDetails { Status = Error.Status, Title = Title(Error.Status) };
        problem.Extensions["code"] = Error.Code;
        if (Error.RetryAfterSeconds is { } retryAfter)
        {
            var seconds = Math.Max(1, retryAfter);
            httpContext.Response.Headers.RetryAfter = seconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
            problem.Extensions["retryAfter"] = seconds;
        }

        IProblemDetailsService writer = httpContext.RequestServices.GetRequiredService<IProblemDetailsService>();
        await writer.WriteAsync(new ProblemDetailsContext { HttpContext = httpContext, ProblemDetails = problem });
    }

    private static string Title(int status) => status switch
    {
        400 => "Bad Request",
        401 => "Unauthorized",
        403 => "Forbidden",
        404 => "Not Found",
        409 => "Conflict",
        412 => "Precondition Failed",
        422 => "Unprocessable Content",
        423 => "Locked",
        428 => "Precondition Required",
        429 => "Too Many Requests",
        _ => "Error",
    };
}

/// <summary>Converts errors and results to HTTP responses.</summary>
public static class ApiResults
{
    /// <summary>Converts the error to a problem+json response.</summary>
    /// <param name="error">The error.</param>
    public static IResult ToHttpResult(this ApiError error) => new ApiErrorResult(error);

    /// <summary>Uses <paramref name="onSuccess"/> on success, problem+json on failure.</summary>
    /// <typeparam name="T">Success value type.</typeparam>
    /// <param name="result">The result to convert.</param>
    /// <param name="onSuccess">Builds the success response.</param>
    public static IResult ToHttpResult<T>(this Result<T> result, Func<T, IResult> onSuccess) =>
        result.IsSuccess ? onSuccess(result.Value) : new ApiErrorResult(result.Error);

    /// <summary>Returns 200 OK with the value, or problem+json on failure.</summary>
    /// <typeparam name="T">Success value type.</typeparam>
    /// <param name="result">The result to convert.</param>
    public static IResult ToOk<T>(this Result<T> result) => result.ToHttpResult(value => TypedResults.Ok(value));

    /// <summary>Returns 204 No Content, or problem+json on failure.</summary>
    /// <typeparam name="T">Success value type.</typeparam>
    /// <param name="result">The result to convert.</param>
    public static IResult ToNoContent<T>(this Result<T> result) => result.ToHttpResult(_ => TypedResults.NoContent());
}
