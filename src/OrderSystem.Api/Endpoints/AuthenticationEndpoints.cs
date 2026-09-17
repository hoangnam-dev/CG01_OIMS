using Microsoft.AspNetCore.Mvc;
using OrderSystem.Api.Authentication;
using OrderSystem.Api.Contracts;
using OrderSystem.Api.Errors;
using OrderSystem.Application.Authentication;
using OrderSystem.Application.Authentication.Contracts;

namespace OrderSystem.Api.Endpoints;

public static class AuthenticationEndpoints
{
    public static IEndpointRouteBuilder MapAuthenticationEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var auth = endpoints.MapGroup("/api/auth").WithTags("Authentication").AllowAnonymous();

        auth.MapPost("/register", Register)
            .Produces<ApiResponse<UserDto>>(StatusCodes.Status201Created)
            .Produces<HttpValidationProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json")
            .Produces<ProblemDetails>(StatusCodes.Status409Conflict, "application/problem+json");
        auth.MapPost("/login", Login)
            .Produces<ApiResponse<TokenResponse>>()
            .Produces<HttpValidationProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json")
            .Produces<ProblemDetails>(StatusCodes.Status401Unauthorized, "application/problem+json");
        auth.MapPost("/refresh", Refresh)
            .Produces<ApiResponse<TokenResponse>>()
            .Produces<HttpValidationProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json")
            .Produces<ProblemDetails>(StatusCodes.Status401Unauthorized, "application/problem+json")
            .Produces<ProblemDetails>(StatusCodes.Status429TooManyRequests, "application/problem+json")
            .RequireRateLimiting(AuthenticationWebOptions.RefreshRateLimitPolicy);
        auth.MapPost("/logout", Logout)
            .Produces(StatusCodes.Status204NoContent);

        return endpoints;
    }

    private static async Task<IResult> Register(
        RegisterRequest request,
        HttpContext context,
        AuthenticationService service,
        CancellationToken cancellationToken)
    {
        var result = await service.RegisterAsync(request, cancellationToken);
        return result.IsSuccess
            ? Results.Created($"/api/users/{result.Value!.Id}", new ApiResponse<UserDto>(result.Value, null))
            : ApplicationResultHttpMapper.ToProblem(context, result.Error!);
    }

    private static async Task<IResult> Login(
        LoginRequest request,
        HttpContext context,
        AuthenticationService service,
        CancellationToken cancellationToken)
    {
        var result = await service.LoginAsync(request, cancellationToken);
        if (!result.IsSuccess)
        {
            return ApplicationResultHttpMapper.ToProblem(context, result.Error!);
        }

        SetSessionResponse(context, result.Value!);
        return Results.Ok(new ApiResponse<TokenResponse>(result.Value!.ToResponse(), null));
    }

    private static async Task<IResult> Refresh(
        HttpContext context,
        AuthenticationService service,
        CancellationToken cancellationToken)
    {
        context.Request.Cookies.TryGetValue(RefreshTokenCookie.Name, out var refreshToken);
        var result = await service.RefreshAsync(refreshToken, cancellationToken);
        if (!result.IsSuccess)
        {
            RefreshTokenCookie.Delete(context.Response);
            SetNoStore(context.Response);
            return ApplicationResultHttpMapper.ToProblem(context, result.Error!);
        }

        SetSessionResponse(context, result.Value!);
        return Results.Ok(new ApiResponse<TokenResponse>(result.Value!.ToResponse(), null));
    }

    private static void SetSessionResponse(HttpContext context, AuthenticationSession session)
    {
        RefreshTokenCookie.Append(context.Response, session.RefreshToken, session.RefreshTokenExpiresAt);
        SetNoStore(context.Response);
    }

    private static void SetNoStore(HttpResponse response)
    {
        response.Headers.CacheControl = "no-store";
        response.Headers.Pragma = "no-cache";
    }

    private static async Task<IResult> Logout(
        HttpContext context,
        AuthenticationService service,
        CancellationToken cancellationToken)
    {
        context.Request.Cookies.TryGetValue(RefreshTokenCookie.Name, out var refreshToken);
        await service.LogoutAsync(refreshToken, cancellationToken);
        RefreshTokenCookie.Delete(context.Response);
        SetNoStore(context.Response);
        return Results.NoContent();
    }
}
