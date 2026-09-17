using System.Security.Cryptography;
using System.Text;
using OrderSystem.Application.Authentication;

namespace OrderSystem.Infrastructure.Authentication;

internal sealed class SecureRefreshTokenProtector : IRefreshTokenProtector
{
    private const int TokenByteLength = 32;

    public GeneratedRefreshToken Generate()
    {
        var token = ToBase64Url(RandomNumberGenerator.GetBytes(TokenByteLength));
        return new(token, Hash(token));
    }

    public string Hash(string token) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    private static string ToBase64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
}
