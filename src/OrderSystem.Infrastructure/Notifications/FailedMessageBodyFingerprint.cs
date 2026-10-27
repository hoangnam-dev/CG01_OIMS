using System.Security.Cryptography;

namespace OrderSystem.Infrastructure.Notifications;

internal static class FailedMessageBodyFingerprint
{
    public static string Compute(ReadOnlySpan<byte> body) => Convert.ToHexStringLower(SHA256.HashData(body));
}
