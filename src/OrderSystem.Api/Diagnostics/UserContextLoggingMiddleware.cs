using System.Security.Claims;
using Serilog.Context;

namespace OrderSystem.Api.Diagnostics;

public sealed class UserContextLoggingMiddleware(RequestDelegate next)
{
    public const string AnonymousUserId = "anonymous";

    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        using (LogContext.PushProperty("UserId", ResolveUserId(context.User)))
        {
            await next(context);
        }
    }

    private static string ResolveUserId(ClaimsPrincipal user)
    {
        if (user.Identity?.IsAuthenticated != true)
        {
            return AnonymousUserId;
        }

        var value = user.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? user.FindFirstValue("sub");

        return Guid.TryParse(value, out var userId)
            ? userId.ToString()
            : AnonymousUserId;
    }
}
