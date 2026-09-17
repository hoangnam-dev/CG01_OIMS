using System.Security.Cryptography;

namespace OrderSystem.IntegrationTests.Infrastructure;

internal static class TestCredentials
{
    public static string ValidPassword { get; } = CreatePassword();

    public static string AlternatePassword { get; } = CreatePassword();

    public static string LowercasePassword { get; } = new('a', 8);

    public static string CreatePassword() => Convert.ToHexString(RandomNumberGenerator.GetBytes(6));

    public static string CreateHashPlaceholder() => $"test-hash-{Guid.NewGuid():N}";
}
