namespace OrderSystem.Infrastructure.Configuration;

public sealed class JwtOptions
{
    public const string SectionName = "Jwt";

    public string Issuer { get; init; } = string.Empty;

    public string Audience { get; init; } = string.Empty;

    public string? SigningKey { get; init; }

    public TimeSpan AccessTokenLifetime { get; init; }
}
