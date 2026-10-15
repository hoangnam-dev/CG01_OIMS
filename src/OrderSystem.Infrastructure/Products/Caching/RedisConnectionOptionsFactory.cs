using OrderSystem.Infrastructure.Configuration;
using StackExchange.Redis;

namespace OrderSystem.Infrastructure.Products.Caching;

internal static class RedisConnectionOptionsFactory
{
    public static ConfigurationOptions Create(RedisOptions redisOptions)
    {
        ArgumentNullException.ThrowIfNull(redisOptions);

        var connectionOptions = ConfigurationOptions.Parse(redisOptions.Configuration);
        connectionOptions.AbortOnConnectFail = false;
        connectionOptions.ConnectTimeout = ToMilliseconds(redisOptions.ConnectTimeout);
        connectionOptions.AsyncTimeout = ToMilliseconds(redisOptions.OperationTimeout);
        return connectionOptions;
    }

    private static int ToMilliseconds(TimeSpan timeout) => checked((int)Math.Ceiling(timeout.TotalMilliseconds));
}