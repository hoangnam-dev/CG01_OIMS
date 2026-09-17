using System.Security.Cryptography;
using Microsoft.Extensions.Configuration;

namespace OrderSystem.IntegrationTests.Infrastructure;

internal static class TestConfiguration
{
    public static string SigningKey { get; } =
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

    public static IConfigurationBuilder AddOimsTestConfiguration(
        this IConfigurationBuilder configuration,
        params KeyValuePair<string, string?>[] overrides)
    {
        var settings = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["Jwt:SigningKey"] = SigningKey,
            ["AdminBootstrap:Enabled"] = bool.FalseString
        };
        foreach (var setting in overrides)
        {
            settings[setting.Key] = setting.Value;
        }

        return configuration.AddInMemoryCollection(settings);
    }
}
