using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
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

        services.AddScoped<IOutboxMessagePublisher, RabbitMqOutboxPublisher>();
        services.AddScoped<EfOutboxClaimStore>();
        services.AddScoped<EfOutboxResultStore>();
        services.AddScoped<IOutboxPublishBatch, OutboxPublishBatch>();
        services.TryAddSingleton<TimeProvider>(TimeProvider.System);

        return services;
    }
}
