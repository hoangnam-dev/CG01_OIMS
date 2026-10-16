using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace OrderSystem.Infrastructure.Messaging;

internal sealed class RabbitMqHealthCheck : IHealthCheck
{
    private readonly IRabbitMqConnectionProvider _connectionProvider;

    public RabbitMqHealthCheck(IRabbitMqConnectionProvider connectionProvider)
    {
        ArgumentNullException.ThrowIfNull(connectionProvider);
        _connectionProvider = connectionProvider;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        var connection = await _connectionProvider.GetConnectionAsync(cancellationToken);

        return connection.IsOpen
            ? HealthCheckResult.Healthy("RabbitMQ connection is open")
            : new HealthCheckResult(context.Registration.FailureStatus, "RabbitMQ connection is closed");
    }
}
