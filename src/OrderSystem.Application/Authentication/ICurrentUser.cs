using OrderSystem.Domain.Users;

namespace OrderSystem.Application.Authentication;

public interface ICurrentUser
{
    bool IsAuthenticated { get; }

    Guid? UserId { get; }

    UserRole? Role { get; }
}
