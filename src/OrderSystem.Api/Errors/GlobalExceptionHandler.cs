using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using OrderSystem.Api.Diagnostics;

namespace OrderSystem.Api.Errors;

public sealed class GlobalExceptionHandler(
    ILogger<GlobalExceptionHandler> logger,
    IProblemDetailsService problemDetailsService) : IExceptionHandler
{
    private static readonly Action<ILogger, string, string?, Exception?> UnhandledFailure =
        LoggerMessage.Define<string, string?>(
            LogLevel.Error,
            new EventId(1, nameof(UnhandledFailure)),
            "Unhandled request failure for {Path}; exception type {ExceptionType}");

    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        UnhandledFailure(logger, httpContext.Request.Path, exception.GetType().FullName, null);
        httpContext.Response.StatusCode = StatusCodes.Status500InternalServerError;

        var problem = new ProblemDetails
        {
            Status = StatusCodes.Status500InternalServerError,
            Title = "An unexpected error occurred",
            Detail = "The server could not complete the request.",
            Type = "https://oims.example/problems/internal-error",
            Instance = httpContext.Request.Path
        };
        problem.Extensions["code"] = "INTERNAL_ERROR";
        problem.Extensions["message"] = problem.Detail;
        problem.Extensions["traceId"] = httpContext.TraceIdentifier;
        problem.Extensions["correlationId"] = httpContext.Items[CorrelationIdMiddleware.HeaderName]?.ToString()
            ?? httpContext.TraceIdentifier;

        return await problemDetailsService.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            ProblemDetails = problem
        });
    }
}
