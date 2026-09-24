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
            ApplicationErrorKind.Unauthorized => StatusCodes.Status401Unauthorized,
            _ => StatusCodes.Status500InternalServerError
        };
        var title = error.Kind switch
        {
            ApplicationErrorKind.Validation => "Validation failed",
            ApplicationErrorKind.NotFound => "Resource not found",
            ApplicationErrorKind.Conflict => "Request conflict",
            ApplicationErrorKind.Forbidden => "Forbidden",
            ApplicationErrorKind.Unauthorized => "Unauthorized",
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

    public static IResult AuthorizationDenied(HttpContext context) =>
        context.User.Identity?.IsAuthenticated == true
            ? AccessForbidden(context)
            : AuthenticationRequired(context);

    public static IResult AuthenticationRequired(HttpContext context) =>
        ToProblem(context, ApplicationErrors.Unauthorized.Create());

    public static IResult AccessForbidden(HttpContext context) =>
        ToProblem(
            context,
            ApplicationErrors.Forbidden.Create(
                message: "The authenticated user is not authorized to access this resource."));

    public static IResult InvalidUuid(HttpContext context, string fieldName) =>
        ToProblem(
            context,
            ApplicationErrors.ValidationFailed.Create(
                validationErrors: new Dictionary<string, string[]>(StringComparer.Ordinal)
                {
                    [fieldName] = ["A valid UUID is required."]
                }));

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
