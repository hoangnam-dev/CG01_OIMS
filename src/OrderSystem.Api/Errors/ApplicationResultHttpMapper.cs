using Microsoft.AspNetCore.Mvc;
using OrderSystem.Api.Diagnostics;
using OrderSystem.Application.Common.Results;

namespace OrderSystem.Api.Errors;

public static class ApplicationResultHttpMapper
{
    public static IResult ToProblem(HttpContext context, ApplicationError error)
    {
        var status = error.Kind switch
        {
            ApplicationErrorKind.Validation => StatusCodes.Status400BadRequest,
            ApplicationErrorKind.NotFound => StatusCodes.Status404NotFound,
            ApplicationErrorKind.Conflict => StatusCodes.Status409Conflict,
            ApplicationErrorKind.Forbidden => StatusCodes.Status403Forbidden,
            _ => StatusCodes.Status500InternalServerError
        };
        var title = error.Kind switch
        {
            ApplicationErrorKind.Validation => "Validation failed",
            ApplicationErrorKind.NotFound => "Resource not found",
            ApplicationErrorKind.Conflict => "Request conflict",
            ApplicationErrorKind.Forbidden => "Forbidden",
            _ => "An unexpected error occurred"
        };
        ProblemDetails problem = error.ValidationErrors is null
            ? new ProblemDetails()
            : new HttpValidationProblemDetails(error.ValidationErrors);
        problem.Status = status;
        problem.Title = title;
        problem.Detail = error.Message;
        problem.Type = $"https://oims.example/problems/{ToProblemSlug(error.Code)}";
        problem.Instance = context.Request.Path;
        AddExtensions(context, problem, error.Code, error.Message);
        return Results.Problem(problem);
    }

    public static IResult? RequireAdmin(HttpContext context)
    {
        if (context.User.Identity?.IsAuthenticated != true)
        {
            return AccessProblem(
                context,
                StatusCodes.Status401Unauthorized,
                "Unauthorized",
                "UNAUTHORIZED",
                "Authentication is required.");
        }

        return context.User.IsInRole("Admin")
            ? null
            : AccessProblem(
                context,
                StatusCodes.Status403Forbidden,
                "Forbidden",
                "FORBIDDEN",
                "Admin access is required.");
    }

    public static IResult InvalidUuid(HttpContext context, string fieldName) =>
        ToProblem(context, new(
            ApplicationErrorKind.Validation,
            "VALIDATION_FAILED",
            "One or more validation errors occurred.",
            new Dictionary<string, string[]>(StringComparer.Ordinal)
            {
                [fieldName] = ["A valid UUID is required."]
            }));

    private static IResult AccessProblem(
        HttpContext context,
        int status,
        string title,
        string code,
        string message)
    {
        var problem = new ProblemDetails
        {
            Status = status,
            Title = title,
            Detail = message,
            Type = $"https://oims.example/problems/{ToProblemSlug(code)}",
            Instance = context.Request.Path
        };
        AddExtensions(context, problem, code, message);
        return Results.Problem(problem);
    }

    private static void AddExtensions(HttpContext context, ProblemDetails problem, string code, string message)
    {
        problem.Extensions["code"] = code;
        problem.Extensions["message"] = message;
        problem.Extensions["traceId"] = context.TraceIdentifier;
        problem.Extensions["correlationId"] = context.Items[CorrelationIdMiddleware.HeaderName]?.ToString()
            ?? context.TraceIdentifier;
    }

    private static string ToProblemSlug(string code) => code.Replace('_', '-').ToLowerInvariant();
}
