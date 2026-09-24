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
    private static readonly Action<ILogger, string, string, Exception?> DatabaseFailure =
        LoggerMessage.Define<string, string>(
            LogLevel.Warning,
            new EventId(2, nameof(DatabaseFailure)),
            "Database request failure for {Path}; classification {Classification}");

    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        var databaseFailure = DatabaseFailureClassifier.Classify(exception);
        if (databaseFailure is not null)
        {
            DatabaseFailure(logger, httpContext.Request.Path, databaseFailure.Code, null);
        }
        else if (DatabaseFailureClassifier.IsContention(exception))
        {
            DatabaseFailure(logger, httpContext.Request.Path, "DATABASE_CONTENTION", null);
        }
        else
        {
            UnhandledFailure(logger, httpContext.Request.Path, exception.GetType().FullName, null);
        }
        var statusCode = databaseFailure?.StatusCode ?? StatusCodes.Status500InternalServerError;
        var code = databaseFailure?.Code ?? "INTERNAL_ERROR";
        var detail = databaseFailure?.Message ?? "The server could not complete the request.";
        httpContext.Response.StatusCode = statusCode;

        var problem = new ProblemDetails
        {
            Status = statusCode,
            Title = statusCode == StatusCodes.Status503ServiceUnavailable ? "Dependency unavailable" : statusCode == StatusCodes.Status409Conflict ? "Request conflict" : "An unexpected error occurred",
            Detail = detail,
            Type = $"https://oims.example/problems/{code.Replace('_', '-').ToLowerInvariant()}",
            Instance = httpContext.Request.Path
        };
        problem.Extensions["code"] = code;
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
