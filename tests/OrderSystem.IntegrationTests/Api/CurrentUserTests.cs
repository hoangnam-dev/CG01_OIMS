using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using OrderSystem.Api.Authentication;
using OrderSystem.Domain.Users;

namespace OrderSystem.IntegrationTests.Api;

public sealed class CurrentUserTests
{
    [Theory]
    [InlineData("Customer", UserRole.Customer)]
    [InlineData("Admin", UserRole.Admin)]
    public void AuthenticatedPrincipal_WithValidSubjectAndRole_ExposesTrustedIdentity(
        string roleClaim,
        UserRole expectedRole)
    {
        var userId = Guid.NewGuid();
        var currentUser = CreateCurrentUser(
            new Claim("sub", userId.ToString()),
            new Claim(ClaimTypes.Role, roleClaim));

        Assert.True(currentUser.IsAuthenticated);
        Assert.Equal(userId, currentUser.UserId);
        Assert.Equal(expectedRole, currentUser.Role);
    }

    [Fact]
    public void AnonymousPrincipal_DoesNotExposeClaimsAsAuthoritativeIdentity()
    {
        var userId = Guid.NewGuid();
        var context = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim("sub", userId.ToString()), new Claim(ClaimTypes.Role, "Admin")]))
        };
        var currentUser = new HttpCurrentUser(new HttpContextAccessor { HttpContext = context });

        Assert.False(currentUser.IsAuthenticated);
        Assert.Null(currentUser.UserId);
        Assert.Null(currentUser.Role);
    }

    [Theory]
    [InlineData(null, "Admin")]
    [InlineData("not-a-uuid", "Admin")]
    [InlineData("valid", null)]
    [InlineData("valid", "SuperAdmin")]
    public void AuthenticatedPrincipal_WithMalformedRequiredClaims_DoesNotExposeInvalidValues(
        string? subject,
        string? role)
    {
        var claims = new List<Claim>();
        if (subject is not null)
        {
            claims.Add(new Claim("sub", subject == "valid" ? Guid.NewGuid().ToString() : subject));
        }

        if (role is not null)
        {
            claims.Add(new Claim(ClaimTypes.Role, role));
        }

        var currentUser = CreateCurrentUser(claims.ToArray());

        Assert.True(currentUser.IsAuthenticated);
        if (subject is null || subject == "not-a-uuid")
        {
            Assert.Null(currentUser.UserId);
        }

        if (role is null || role == "SuperAdmin")
        {
            Assert.Null(currentUser.Role);
        }
    }

    private static HttpCurrentUser CreateCurrentUser(params Claim[] claims)
    {
        var context = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Bearer"))
        };
        return new HttpCurrentUser(new HttpContextAccessor { HttpContext = context });
    }
}
