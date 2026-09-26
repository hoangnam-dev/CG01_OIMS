using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using OrderSystem.Application.Common.Clock;
using OrderSystem.Application.Common.Diagnostics;
using OrderSystem.Application.Common.Identifiers;
using OrderSystem.Application.Authentication;
using OrderSystem.Application.Inventories;
using OrderSystem.Application.Products;
using OrderSystem.Application.Orders;
using OrderSystem.Infrastructure.Authentication;
using OrderSystem.Infrastructure.Common.Clock;
using OrderSystem.Infrastructure.Common.Diagnostics;
using OrderSystem.Infrastructure.Configuration;
using OrderSystem.Infrastructure.Inventories;
using OrderSystem.Infrastructure.Persistence;
using OrderSystem.Infrastructure.Products;
using OrderSystem.Infrastructure.Orders;

namespace OrderSystem.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddOrderSystemInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddValidatedOptions(configuration);
        services.AddDbContext<OrderSystemDbContext>((provider, options) =>
        {
            var database = provider.GetRequiredService<IOptions<DatabaseOptions>>().Value;
            options.UseNpgsql(database.ConnectionString, npgsql =>
                npgsql.MigrationsAssembly(typeof(OrderSystemDbContext).Assembly.FullName));
        });
        services.AddScoped<DbContext>(provider => provider.GetRequiredService<OrderSystemDbContext>());
        services.AddScoped<IOrderCommandStore, EfOrderCommandStore>();
        services.AddScoped<IProductCatalogStore, EfProductCatalogStore>();
        services.AddScoped<ProductCatalogService>();
        services.AddScoped<IInventoryStore, EfInventoryStore>();
        services.AddScoped<InventoryService>();
        services.AddScoped<IOrderReadStore, EfOrderReadStore>();

        services.AddScoped<OrderQueryService>();
        services.AddScoped<IAuthenticationStore, EfAuthenticationStore>();
        services.AddScoped<IRefreshTokenCleanupStore, EfRefreshTokenCleanupStore>();
        services.AddScoped<AuthenticationService>();
        services.AddScoped<AdminBootstrapService>();
        services.AddSingleton<IPasswordHasher, BcryptPasswordHasher>();
        services.AddSingleton<IRefreshTokenProtector, SecureRefreshTokenProtector>();
        services.AddSingleton<IAccessTokenIssuer, JwtAccessTokenIssuer>();
        services.AddSingleton<IClock, SystemClock>();
        services.AddSingleton<IIdGenerator, Uuid7IdGenerator>();
        services.AddSingleton<IOperationHook, NoOpOperationHook>();
        services.AddHealthChecks()
            .AddDbContextCheck<OrderSystemDbContext>("postgresql", tags: ["ready"]);

        return services;
    }

    private static void AddValidatedOptions(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<DatabaseOptions>()
            .Bind(configuration.GetRequiredSection(DatabaseOptions.SectionName))
            .Validate(options => !string.IsNullOrWhiteSpace(options.ConnectionString), "Database:ConnectionString is required.")
            .Validate(options => IsPostgreSqlConnectionString(options.ConnectionString), "Database:ConnectionString must be a valid PostgreSQL connection string.")
            .ValidateOnStart();
        services.AddOptions<JwtOptions>()
            .Bind(configuration.GetRequiredSection(JwtOptions.SectionName))
            .Validate(options => !string.IsNullOrWhiteSpace(options.Issuer), "Jwt:Issuer is required.")
            .Validate(options => !string.IsNullOrWhiteSpace(options.Audience), "Jwt:Audience is required.")
            .Validate(options => options.AccessTokenLifetime > TimeSpan.Zero, "Jwt:AccessTokenLifetime must be positive.")
            .Validate(options => !string.IsNullOrWhiteSpace(options.SigningKey), "Jwt:SigningKey is required.")
            .Validate(options => IsBase64(options.SigningKey), "Jwt:SigningKey must be valid Base64.")
            .Validate(options => HasMinimumSigningKeyLength(options.SigningKey), "Jwt:SigningKey must contain at least 32 bytes.")
            .ValidateOnStart();
        services.AddOptions<RedisOptions>()
            .Bind(configuration.GetRequiredSection(RedisOptions.SectionName))
            .Validate(options => !string.IsNullOrWhiteSpace(options.Configuration), "Redis:Configuration is required.")
            .Validate(options => options.ProductTtl > TimeSpan.Zero, "Redis:ProductTtl must be positive.")
            .ValidateOnStart();
        services.AddOptions<RabbitMqOptions>()
            .Bind(configuration.GetRequiredSection(RabbitMqOptions.SectionName))
            .Validate(options => !string.IsNullOrWhiteSpace(options.Host), "RabbitMq:Host is required.")
            .Validate(options => options.Port is > 0 and <= 65535, "RabbitMq:Port must be valid.")
            .Validate(options => !string.IsNullOrWhiteSpace(options.VirtualHost), "RabbitMq:VirtualHost is required.")
            .Validate(options => !string.IsNullOrWhiteSpace(options.Username), "RabbitMq:Username is required.")
            .ValidateOnStart();
        services.AddOptions<ReservationOptions>()
            .Bind(configuration.GetRequiredSection(ReservationOptions.SectionName))
            .Validate(options => options.Duration > TimeSpan.Zero, "Reservation:Duration must be positive.")
            .Validate(options => options.ExpirationScanInterval > TimeSpan.Zero, "Reservation:ExpirationScanInterval must be positive.")
            .Validate(options => options.BatchSize > 0, "Reservation:BatchSize must be positive.")
            .ValidateOnStart();
        services.AddOptions<RetryOptions>()
            .Bind(configuration.GetRequiredSection(RetryOptions.SectionName))
            .Validate(options => options.MaxAttempts > 0, "Retry:MaxAttempts must be positive.")
            .Validate(options => options.Delay >= TimeSpan.Zero, "Retry:Delay cannot be negative.")
            .ValidateOnStart();
        services.AddOptions<PaymentOptions>()
            .Bind(configuration.GetRequiredSection(PaymentOptions.SectionName))
            .Validate(options => options.ReconciliationInterval > TimeSpan.Zero, "Payment:ReconciliationInterval must be positive.")
            .Validate(options => options.ReconciliationBatchSize > 0, "Payment:ReconciliationBatchSize must be positive.")
            .ValidateOnStart();
    }

    private static bool IsPostgreSqlConnectionString(string connectionString)
    {
        try
        {
            return !string.IsNullOrWhiteSpace(new Npgsql.NpgsqlConnectionStringBuilder(connectionString).Host);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static bool IsBase64(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        try
        {
            Convert.FromBase64String(value);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static bool HasMinimumSigningKeyLength(string? value)
    {
        if (!IsBase64(value))
        {
            return false;
        }

        return Convert.FromBase64String(value!).Length >= 32;
    }
}
