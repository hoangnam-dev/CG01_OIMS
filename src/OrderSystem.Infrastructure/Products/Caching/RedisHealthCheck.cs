using Microsoft.Extensions.Diagnostics.HealthChecks;
using StackExchange.Redis;

namespace OrderSystem.Infrastructure.Products.Caching;

internal sealed class RedisHealthCheck(IConnectionMultiplexer connection) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (!connection.IsConnected)
        {
            return new HealthCheckResult(context.Registration.FailureStatus, "Redis connection is unavailable");
        }

        await connection.GetDatabase().PingAsync().WaitAsync(cancellationToken);
        return HealthCheckResult.Healthy("Redis responded to ping");
    }
}
