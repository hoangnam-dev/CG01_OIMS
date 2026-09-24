using System.Security.Claims;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
using Microsoft.OpenApi;
using Microsoft.IdentityModel.Tokens;
using OrderSystem.Api.Authentication;
using OrderSystem.Api.Diagnostics;
using OrderSystem.Api.Endpoints;
using OrderSystem.Api.Errors;
using OrderSystem.Api.OpenApi;
using OrderSystem.Application.Authentication;
using OrderSystem.Application.Common.Results;
using OrderSystem.Infrastructure;
using OrderSystem.Infrastructure.Logging;
using OrderSystem.Infrastructure.Configuration;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSerilog(loggerConfiguration =>
    loggerConfiguration.ConfigureOimsLogging(builder.Configuration, "OrderSystem.Api"));
builder.Services.AddOrderSystemInfrastructure(builder.Configuration);
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        var jwt = builder.Configuration.GetRequiredSection(JwtOptions.SectionName).Get<JwtOptions>()
            ?? throw new InvalidOperationException("JWT configuration is required.");
        options.MapInboundClaims = false;
        var signingKey = string.IsNullOrWhiteSpace(jwt.SigningKey)
            ? null
            : new SymmetricSecurityKey(jwt.GetSigningKeyBytes());
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = jwt.Issuer,
            ValidateAudience = true,
            ValidAudience = jwt.Audience,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = signingKey,
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromSeconds(30),
            NameClaimType = "sub",
            RoleClaimType = ClaimTypes.Role
        };
        options.Events = new JwtBearerEvents
        {
            OnChallenge = async context =>
            {
                context.HandleResponse();
                await ApplicationResultHttpMapper.AuthenticationRequired(context.HttpContext)
                    .ExecuteAsync(context.HttpContext);
            },
            OnForbidden = context =>
                ApplicationResultHttpMapper.AccessForbidden(context.HttpContext)
                    .ExecuteAsync(context.HttpContext)
        };
    });
builder.Services.AddAuthorizationBuilder()
    .AddPolicy(AuthorizationPolicies.Admin, policy => policy.RequireRole("Admin"))
    .AddPolicy(AuthorizationPolicies.Customer, policy => policy.RequireRole("Customer"));
builder.Services.AddHttpContextAccessor();
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddScoped<ICurrentUser, HttpCurrentUser>();
builder.Services.AddOptions<AuthenticationWebOptions>()
    .Bind(builder.Configuration.GetRequiredSection(AuthenticationWebOptions.SectionName))
    .Validate(options => options.RefreshPermitLimit > 0, "Authentication:RefreshPermitLimit must be positive.")
    .Validate(options => options.RefreshWindow > TimeSpan.Zero, "Authentication:RefreshWindow must be positive.")
    .Validate(options => options.RefreshTokenCleanupInterval > TimeSpan.Zero, "Authentication:RefreshTokenCleanupInterval must be positive.")
    .Validate(options => options.RefreshTokenCleanupBatchSize > 0, "Authentication:RefreshTokenCleanupBatchSize must be positive.")
    .Validate(options => options.ExpiredRefreshTokenRetention >= TimeSpan.Zero, "Authentication:ExpiredRefreshTokenRetention cannot be negative.")
    .ValidateOnStart();
builder.Services.AddOptions<AdminBootstrapOptions>()
    .Bind(builder.Configuration.GetSection(AdminBootstrapOptions.SectionName))
    .Validate(
        options => !options.Enabled || builder.Environment.IsDevelopment() ||
            builder.Environment.IsEnvironment("Test"),
        "AdminBootstrap may be enabled only in Development or Test.")
    .Validate(
        options => !options.Enabled || options.HasValidEmail(),
        "AdminBootstrap:Email must be a valid email address when bootstrap is enabled.")
    .Validate(
        options => !options.Enabled || options.HasValidPasswordLength(),
        "AdminBootstrap:Password must contain between 8 and 13 characters when bootstrap is enabled.")
    .ValidateOnStart();
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy(AuthenticationWebOptions.RefreshRateLimitPolicy, httpContext =>
    {
        var authOptions = httpContext.RequestServices
            .GetRequiredService<IOptions<AuthenticationWebOptions>>().Value;
        var partitionKey = httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        return RateLimitPartition.GetFixedWindowLimiter(partitionKey, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = authOptions.RefreshPermitLimit,
            Window = authOptions.RefreshWindow,
            QueueLimit = 0,
            AutoReplenishment = true
        });
    });
    options.OnRejected = async (context, cancellationToken) =>
    {
        if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
        {
            context.HttpContext.Response.Headers.RetryAfter = Math.Ceiling(retryAfter.TotalSeconds)
                .ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        var problem = new ProblemDetails
        {
            Status = StatusCodes.Status429TooManyRequests,
            Title = "Too many requests",
            Detail = "Too many refresh attempts. Try again later.",
            Type = "https://oims.example/problems/rate-limited",
            Instance = context.HttpContext.Request.Path
        };
        problem.Extensions["code"] = "RATE_LIMITED";
        problem.Extensions["message"] = problem.Detail;
        problem.Extensions["traceId"] = context.HttpContext.TraceIdentifier;
        problem.Extensions["correlationId"] =
            context.HttpContext.Items[CorrelationIdMiddleware.HeaderName]?.ToString()
            ?? context.HttpContext.TraceIdentifier;
        await context.HttpContext.RequestServices.GetRequiredService<IProblemDetailsService>()
            .WriteAsync(new ProblemDetailsContext
            {
                HttpContext = context.HttpContext,
                ProblemDetails = problem
            });
    };
});
builder.Services.AddHostedService<RefreshTokenCleanupWorker>();
builder.Services.AddHostedService<AdminBootstrapHostedService>();
builder.Services.AddExceptionHandler<GlobalExceptionHandler>();
builder.Services.AddProblemDetails(options => options.CustomizeProblemDetails = context =>
{
    context.ProblemDetails.Extensions.TryAdd("code", context.HttpContext.Response.StatusCode == 400
        ? ApplicationErrors.ValidationFailed.Code
        : "HTTP_ERROR");
    context.ProblemDetails.Extensions.TryAdd("message", context.ProblemDetails.Detail ?? context.ProblemDetails.Title);
    context.ProblemDetails.Extensions.TryAdd("traceId", context.HttpContext.TraceIdentifier);
    context.ProblemDetails.Extensions.TryAdd("correlationId",
        context.HttpContext.Items[CorrelationIdMiddleware.HeaderName]?.ToString() ?? context.HttpContext.TraceIdentifier);
});
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    options.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
    {
        Type = SecuritySchemeType.Http,
        Scheme = "bearer",
        BearerFormat = "JWT",
        Description = "Enter the JWT access token returned by POST /api/auth/login."
    });
    options.OperationFilter<BearerSecurityOperationFilter>();
    options.OperationFilter<CorrelationIdOperationFilter>();
    options.OperationFilter<IdempotencyKeyOperationFilter>();
});

var app = builder.Build();

app.UseMiddleware<CorrelationIdMiddleware>();
app.UseRateLimiter();
app.UseAuthentication();
app.UseMiddleware<UserContextLoggingMiddleware>();
app.UseAuthorization();
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
app.MapAuthenticationEndpoints();
app.MapProductCatalogEndpoints();
app.MapInventoryEndpoints();
app.MapOrderEndpoints();

app.Run();

public partial class Program;
