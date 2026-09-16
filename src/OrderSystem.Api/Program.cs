using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using OrderSystem.Api.Diagnostics;
using OrderSystem.Api.Endpoints;
using OrderSystem.Api.Errors;
using OrderSystem.Api.OpenApi;
using OrderSystem.Infrastructure;
using OrderSystem.Infrastructure.Logging;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSerilog(loggerConfiguration =>
    loggerConfiguration.ConfigureOimsLogging(builder.Configuration, "OrderSystem.Api"));
builder.Services.AddOrderSystemInfrastructure(builder.Configuration);
builder.Services.AddExceptionHandler<GlobalExceptionHandler>();
builder.Services.AddProblemDetails(options => options.CustomizeProblemDetails = context =>
{
    context.ProblemDetails.Extensions.TryAdd("code", context.HttpContext.Response.StatusCode == 400
        ? "VALIDATION_FAILED"
        : "HTTP_ERROR");
    context.ProblemDetails.Extensions.TryAdd("message", context.ProblemDetails.Detail ?? context.ProblemDetails.Title);
    context.ProblemDetails.Extensions.TryAdd("traceId", context.HttpContext.TraceIdentifier);
    context.ProblemDetails.Extensions.TryAdd("correlationId",
        context.HttpContext.Items[CorrelationIdMiddleware.HeaderName]?.ToString() ?? context.HttpContext.TraceIdentifier);
});
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options => options.OperationFilter<CorrelationIdOperationFilter>());

var app = builder.Build();

app.UseMiddleware<CorrelationIdMiddleware>();
app.UseMiddleware<UserContextLoggingMiddleware>();
app.UseExceptionHandler();
app.UseSerilogRequestLogging(options =>
{
    options.MessageTemplate = "HTTP {RequestMethod} {RequestPath} responded {StatusCode} in {Elapsed:0.0000} ms";
    options.EnrichDiagnosticContext = (diagnosticContext, httpContext) =>
    {
        diagnosticContext.Set("TraceId", httpContext.TraceIdentifier);
        diagnosticContext.Set("CorrelationId", httpContext.TraceIdentifier);
    };
});

app.UseSwagger(options => options.RouteTemplate = "openapi/{documentName}.json");
if (app.Environment.IsDevelopment())
{
    app.UseSwaggerUI(options =>
        options.SwaggerEndpoint("/openapi/v1.json", "OrderSystem.Api v1"));
}

app.MapHealthChecks("/health/live", new HealthCheckOptions
{
    Predicate = _ => false
});
app.MapHealthChecks("/health/ready", new HealthCheckOptions
{
    Predicate = registration => registration.Tags.Contains("ready")
});
app.MapFoundationEndpoints();

app.Run();

public partial class Program;
