using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using OrderSystem.Infrastructure.Configuration;
using OrderSystem.Infrastructure.Persistence;

namespace OrderSystem.Infrastructure.Outbox;

internal sealed class OutboxHealthCheck(
    OrderSystemDbContext dbContext,
    IOptions<OutboxHealthOptions> options,
    TimeProvider timeProvider) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        var needsAttention = await dbContext.OutboxMessages.AnyAsync(message => message.Status == OutboxMessageStatus.NeedsAttention, cancellationToken);

        if (needsAttention)
        {
            return new HealthCheckResult(context.Registration.FailureStatus, "Outbox messages require operator attention");
        }

        var cutoff = timeProvider.GetUtcNow().Subtract(options.Value.PendingAgeThreshold);
        var pendingOverdue = await dbContext.OutboxMessages.AnyAsync(message => message.Status == OutboxMessageStatus.Pending && message.OccurredAt < cutoff, cancellationToken);

        return pendingOverdue
            ? new HealthCheckResult(context.Registration.FailureStatus, "Outbox Pending backlog exceeded the age threshold")
            : HealthCheckResult.Healthy("Outbox backlog is within the age threshold");
    }
}
