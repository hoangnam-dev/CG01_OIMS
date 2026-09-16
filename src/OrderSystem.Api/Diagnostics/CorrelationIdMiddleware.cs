using Microsoft.AspNetCore.Mvc;
using Serilog.Context;

namespace OrderSystem.Api.Diagnostics;

public sealed class CorrelationIdMiddleware(RequestDelegate next)
{
    public const string HeaderName = "X-Correlation-ID";

    public async Task InvokeAsync(HttpContext context, IProblemDetailsService problemDetailsService)
    {
        ArgumentNullException.ThrowIfNull(context);

        var suppliedValue = context.Request.Headers[HeaderName].FirstOrDefault();
        if (suppliedValue is not null && !Guid.TryParse(suppliedValue, out _))
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await problemDetailsService.WriteAsync(new ProblemDetailsContext
            {
                HttpContext = context,
                ProblemDetails = new ProblemDetails
                {
                    Status = StatusCodes.Status400BadRequest,
                    Title = "Validation failed",
                    Detail = $"{HeaderName} must be a UUID.",
                    Type = "https://oims.example/problems/validation-failed"
                }
            });
            return;
        }

        var correlationId = suppliedValue ?? Guid.NewGuid().ToString();
        context.TraceIdentifier = correlationId;
        context.Items[HeaderName] = correlationId;
        context.Response.OnStarting(() =>
        {
            context.Response.Headers[HeaderName] = correlationId;
            return Task.CompletedTask;
        });

        using (LogContext.PushProperty("CorrelationId", correlationId))
        {
            await next(context);
        }
    }
}
