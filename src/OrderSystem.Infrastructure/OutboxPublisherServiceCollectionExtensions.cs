using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OrderSystem.Application.Common.Clock;
using OrderSystem.Infrastructure.Common.Clock;
using OrderSystem.Infrastructure.Configuration;
using OrderSystem.Infrastructure.Outbox;

namespace OrderSystem.Infrastructure;

public static class OutboxPublisherServiceCollectionExtensions
{
    public static IServiceCollection AddOrderSystemOutboxPublisherInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<OutboxPublisherOptions>()
            .Bind(configuration.GetSection(OutboxPublisherOptions.SectionName))
            .Validate(options => options.MaxConcurrentPublishes > 0, "OutboxPublisher:MaxConcurrentPublishes must be positive")
            .Validate(options => options.IdlePollInterval > TimeSpan.Zero, "OutboxPublisher:IdlePollInterval must be positive")
            .ValidateOnStart();

        services.AddOptions<OutboxCleanupOptions>()
            .Bind(configuration.GetSection(OutboxCleanupOptions.SectionName))
            .Validate(options => options.InitialDelay > TimeSpan.Zero, "OutboxCleanup:InitialDelay must be positive")
            .Validate(options => options.Interval > TimeSpan.Zero, "OutboxCleanup:Interval must be positive")
            .Validate(options => options.BatchSize > 0, "OutboxCleanup:BatchSize must be positive")
            .Validate(options => options.PublishedRetention > TimeSpan.Zero, "OutboxCleanup:PublishedRetention must be positive")
            .ValidateOnStart();

        services.AddOptions<OutboxMetricsOptions>()
            .Bind(configuration.GetSection(OutboxMetricsOptions.SectionName))
            .Validate(options => options.Interval > TimeSpan.Zero, "OutboxMetrics:Interval must be positive")
            .ValidateOnStart();

        services.AddScoped<IOutboxMessagePublisher, RabbitMqOutboxPublisher>();
        services.AddScoped<EfOutboxClaimStore>();
        services.AddScoped<EfOutboxResultStore>();
        services.AddScoped<IOutboxPublishBatch, OutboxPublishBatch>();
        services.AddScoped<EfOutboxCleanupStore>();
        services.AddScoped<EfOutboxBacklogStore>();
        services.AddHostedService<OutboxCleanupWorker>();
        services.AddHostedService<OutboxMetricsWorker>();
        services.TryAddSingleton<IClock, SystemClock>();
        services.TryAddSingleton<TimeProvider>(TimeProvider.System);

        return services;
    }
}
