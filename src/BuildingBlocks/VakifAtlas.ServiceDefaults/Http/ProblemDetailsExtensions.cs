using System.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using VakifAtlas.SharedKernel;

namespace VakifAtlas.ServiceDefaults.Http;

/// <summary>problem+json error setup.</summary>
public static class ProblemDetailsExtensions
{
    /// <summary>Adds problem+json errors with <c>code</c> and <c>traceId</c>.</summary>
    /// <param name="services">The service collection.</param>
    public static IServiceCollection AddApiProblemDetails(this IServiceCollection services) =>
        services.AddProblemDetails(options => options.CustomizeProblemDetails = context =>
        {
            ProblemDetails problem = context.ProblemDetails;
            problem.Extensions.TryAdd("code", DefaultCode(problem.Status ?? context.HttpContext.Response.StatusCode));
            problem.Extensions.TryAdd("traceId", Activity.Current?.TraceId.ToString() ?? context.HttpContext.TraceIdentifier);
            problem.Instance ??= null;
        });

    /// <summary>Maps a status code to a contract error code.</summary>
    /// <param name="status">HTTP status code.</param>
    public static string DefaultCode(int status) => status switch
    {
        StatusCodes.Status401Unauthorized => ErrorCodes.Unauthorized,
        StatusCodes.Status403Forbidden => ErrorCodes.Unauthorized,
        StatusCodes.Status404NotFound => ErrorCodes.NotFound,
        StatusCodes.Status429TooManyRequests => ErrorCodes.TooManyRequests,
        >= 500 => ErrorCodes.ServerError,
        _ => ErrorCodes.InvalidRequest,
    };
}
