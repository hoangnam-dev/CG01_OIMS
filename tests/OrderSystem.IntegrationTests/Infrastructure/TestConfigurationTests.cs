using Microsoft.Extensions.Configuration;

namespace OrderSystem.IntegrationTests.Infrastructure;

public sealed class TestConfigurationTests
{
    [Fact]
    public void AddOimsTestConfiguration_OverridesAmbientBootstrapAndSuppliesValidSigningKey()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AdminBootstrap:Enabled"] = bool.TrueString
            })
            .AddOimsTestConfiguration()
            .Build();

        Assert.Equal(bool.FalseString, configuration["AdminBootstrap:Enabled"]);
        Assert.Equal(32, Convert.FromBase64String(configuration["Jwt:SigningKey"]!).Length);
    }
}
