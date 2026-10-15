using System.Net;
using OrderSystem.Infrastructure.Configuration;
using OrderSystem.Infrastructure.Products.Caching;

namespace OrderSystem.IntegrationTests.Products.Caching;

public sealed class RedisConnectionOptionsFactoryTests
{
    [Fact]
    public void Create_OverridesConnectionStringWithOimsConnectionPolicy()
    {
        var redisOptions = new RedisOptions
        {
            Configuration =
                "localhost:6380,defaultDatabase=4,abortConnect=true,connectTimeout=9000,asyncTimeout=8000",
            KeyPrefix = "test-run",
            ProductTtl = TimeSpan.FromMinutes(5),
            OperationTimeout = TimeSpan.FromMilliseconds(500),
            ConnectTimeout = TimeSpan.FromSeconds(1)
        };

        var connectionOptions =
            RedisConnectionOptionsFactory.Create(redisOptions);

        var endpoint = Assert.IsType<DnsEndPoint>(
            Assert.Single(connectionOptions.EndPoints));

        Assert.Equal("localhost", endpoint.Host);
        Assert.Equal(6380, endpoint.Port);
        Assert.Equal(4, connectionOptions.DefaultDatabase);

        Assert.False(connectionOptions.AbortOnConnectFail);
        Assert.Equal(1000, connectionOptions.ConnectTimeout);
        Assert.Equal(500, connectionOptions.AsyncTimeout);
    }
}