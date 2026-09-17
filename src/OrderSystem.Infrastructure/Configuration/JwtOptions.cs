namespace OrderSystem.Infrastructure.Configuration;

public sealed class JwtOptions
{
    public const string SectionName = "Jwt";

    public string Issuer { get; init; } = string.Empty;

    public string Audience { get; init; } = string.Empty;

    public string? SigningKey { get; init; }

    public TimeSpan AccessTokenLifetime { get; init; }

    public byte[] GetSigningKeyBytes()
    {
        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(SigningKey ?? string.Empty);
        }
        catch (FormatException exception)
        {
            throw new InvalidOperationException("Jwt:SigningKey must be valid Base64.", exception);
        }

        if (bytes.Length < 32)
        {
            throw new InvalidOperationException("Jwt:SigningKey must contain at least 32 bytes.");
        }

        return bytes;
    }
}
