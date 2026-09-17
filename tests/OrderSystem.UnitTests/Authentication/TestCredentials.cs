using System.Security.Cryptography;

namespace OrderSystem.UnitTests.Authentication;

internal static class TestCredentials
{
    public static string ValidPassword { get; } = CreatePassword();

    public static string AlternatePassword { get; } = CreatePassword();

    public static string CreatePassword() => Convert.ToHexString(RandomNumberGenerator.GetBytes(6));

    public static string CreateHashPlaceholder() => $"test-hash-{Guid.NewGuid():N}";
}
