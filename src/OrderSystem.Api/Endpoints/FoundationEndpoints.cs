using Microsoft.AspNetCore.Mvc;
using OrderSystem.Application.Common.Diagnostics;
using OrderSystem.Api.Contracts;

namespace OrderSystem.Api.Endpoints;

public static class FoundationEndpoints
{
    public static IEndpointRouteBuilder MapFoundationEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/foundation").WithTags("Foundation");

        group.MapGet("/success", async (IOperationHook operationHook, CancellationToken cancellationToken) =>
            {
                await operationHook.ReachAsync("foundation.success", cancellationToken);
                return Results.Ok(new ApiResponse<FoundationStatus>(new("ready"), null));
            })
            .Produces<ApiResponse<FoundationStatus>>()
            .Produces<ProblemDetails>(StatusCodes.Status500InternalServerError, "application/problem+json");
        group.MapGet("/paginated", () => Results.Ok(new ApiResponse<FoundationStatus[]>(
            [new("ready")],
            new(new(1, 20, 1, 1)))))
            .Produces<ApiResponse<FoundationStatus[]>>();
        group.MapDelete("/no-content", () => Results.NoContent())
            .Produces(StatusCodes.Status204NoContent);
        group.MapPost("/validate", (FoundationRequest request, HttpContext context) =>
        {
            if (!string.IsNullOrWhiteSpace(request.Name))
            {
                return Results.Ok(new ApiResponse<FoundationStatus>(new("valid"), null));
            }

            var problem = new HttpValidationProblemDetails(new Dictionary<string, string[]>
            {
                ["name"] = ["Name is required."]
            })
            {
                Status = StatusCodes.Status400BadRequest,
                Title = "Validation failed",
                Detail = "One or more validation errors occurred.",
                Type = "https://oims.example/problems/validation-failed",
                Instance = context.Request.Path
            };
            problem.Extensions["code"] = "VALIDATION_FAILED";
            problem.Extensions["message"] = problem.Detail;
            problem.Extensions["traceId"] = context.TraceIdentifier;
            problem.Extensions["correlationId"] = context.TraceIdentifier;
            return Results.Problem(problem);
        })
            .Produces<ApiResponse<FoundationStatus>>()
            .Produces<HttpValidationProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json");
        return endpoints;
    }

    public sealed record FoundationRequest(string? Name);

    public sealed record FoundationStatus(string Status);
}
