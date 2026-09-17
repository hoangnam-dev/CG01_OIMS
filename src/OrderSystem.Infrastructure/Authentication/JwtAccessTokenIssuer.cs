using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using OrderSystem.Application.Authentication;
using OrderSystem.Domain.Users;
using OrderSystem.Infrastructure.Configuration;

namespace OrderSystem.Infrastructure.Authentication;

internal sealed class JwtAccessTokenIssuer(IOptions<JwtOptions> options) : IAccessTokenIssuer
{
    private readonly JwtOptions _options = options.Value;

    public IssuedAccessToken Issue(User user, DateTimeOffset issuedAt)
    {
        var expiresAt = issuedAt.Add(_options.AccessTokenLifetime);
        var credentials = new SigningCredentials(
            new SymmetricSecurityKey(_options.GetSigningKeyBytes()),
            SecurityAlgorithms.HmacSha256);
        var claims = new Claim[]
        {
            new(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
            new(JwtRegisteredClaimNames.Email, user.Email),
            new(ClaimTypes.Role, user.Role.ToString()),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString())
        };
        var token = new JwtSecurityToken(
            _options.Issuer,
            _options.Audience,
            claims,
            issuedAt.UtcDateTime,
            expiresAt.UtcDateTime,
            credentials);

        return new(new JwtSecurityTokenHandler().WriteToken(token), expiresAt);
    }
}
