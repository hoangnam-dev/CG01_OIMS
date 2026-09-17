using System.Security.Claims;
using OrderSystem.Application.Authentication;
using OrderSystem.Domain.Users;

namespace OrderSystem.Api.Authentication;

public sealed class HttpCurrentUser(IHttpContextAccessor httpContextAccessor) : ICurrentUser
{
    private ClaimsPrincipal? Principal => httpContextAccessor.HttpContext?.User;

    public bool IsAuthenticated => Principal?.Identity?.IsAuthenticated == true;

    public Guid? UserId =>
        IsAuthenticated && Guid.TryParse(Principal!.FindFirstValue("sub"), out var userId)
            ? userId
            : null;

    public UserRole? Role =>
        IsAuthenticated && Enum.TryParse<UserRole>(
            Principal!.FindFirstValue(ClaimTypes.Role),
            ignoreCase: false,
            out var role) && Enum.IsDefined(role)
                ? role
                : null;
}
