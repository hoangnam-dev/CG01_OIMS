using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using OrderSystem.Application.Authentication;
using OrderSystem.Application.Common.Clock;
using OrderSystem.Application.Common.Diagnostics;
using OrderSystem.Application.Messaging.Contracts;
using OrderSystem.Application.Orders;
using OrderSystem.Application.Orders.Contracts;
using OrderSystem.Domain.Idempotency;
using OrderSystem.Domain.Inventories;
using OrderSystem.Domain.Products;
using OrderSystem.Domain.Users;
using OrderSystem.Infrastructure.Messaging;
using OrderSystem.Infrastructure.Persistence;
using OrderSystem.IntegrationTests.Infrastructure;

namespace OrderSystem.IntegrationTests.Messaging;

[Collection(PostgreSqlCollectionDefinition.Name)]
public sealed class OrderCreatedPublishingTests(
    PostgreSqlFixture postgres,
    RabbitMqFixture rabbitMq) : IClassFixture<RabbitMqFixture>
{
    private static readonly DateTimeOffset FixedNow = new(2026, 10, 17, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    [Trait("Requirement", "S9-T04")]
    [Trait("Requirement", "S9-T06")]
    [Trait("TestId", "MSG-PUBLISH-001")]
    [Trait("TestId", "MSG-TRACE-001")]
    public async Task CreateAsync_NewlyCommittedOrder_PublishesAndLogsOrderCreatedV1()
    {
        var userId = Guid.NewGuid();
        var correlationId = Guid.NewGuid();
        using var loggerProvider = new RecordingLoggerProvider();
        await using var factory = CreateFactory(userId, correlationId, loggerProvider);
        using var scope = factory.Services.CreateScope();
        var provider = scope.ServiceProvider;
        var dbContext = provider.GetRequiredService<OrderSystemDbContext>();
        await dbContext.Database.MigrateAsync();
        var productVariantId = await SeedOrderDependenciesAsync(dbContext, userId);

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var topology = provider.GetRequiredService<IRabbitMqTopologyInitializer>();
        var channelFactory = provider.GetRequiredService<IRabbitMqChannelFactory>();
        await topology.InitializeAsync(timeout.Token);
        await using var channel = await channelFactory.CreateChannelAsync(timeout.Token);
        await channel.QueuePurgeAsync("oims.notification.order-created.v1", timeout.Token);

        var service = provider.GetRequiredService<OrderCommandService>();
        var result = await service.CreateAsync(
            Guid.NewGuid(),
            new CreateOrderRequest([new(productVariantId, Quantity: 1)]),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.False(result.Value!.IsReplay);

        var delivery = await channel.BasicGetAsync(
            "oims.notification.order-created.v1",
            autoAck: true,
            timeout.Token);

        Assert.NotNull(delivery);
        Assert.True(OrderCreatedV1Serializer.TryDeserialize(delivery.Body.Span, out var message));
        Assert.NotNull(message);

        var committedOrder = await dbContext.Orders
            .AsNoTracking()
            .SingleAsync(order => order.Id == result.Value.ResourceId);

        Assert.Equal(1, message.EventVersion);
        Assert.NotEqual(Guid.Empty, message.MessageId);
        Assert.Equal(committedOrder.Id, message.OrderId);
        Assert.Equal(userId, message.UserId);
        Assert.Equal(correlationId, message.CorrelationId);
        Assert.Equal(committedOrder.CreatedAt, message.OccurredAt);
        Assert.Equal(message.MessageId.ToString("D"), delivery.BasicProperties.MessageId);
        Assert.Equal(message.CorrelationId.ToString("D"), delivery.BasicProperties.CorrelationId);
        Assert.Equal("application/json", delivery.BasicProperties.ContentType);
        Assert.Equal("OrderCreated", delivery.BasicProperties.Type);
        Assert.True(delivery.BasicProperties.Persistent);

        var publishLog = Assert.Single(
            loggerProvider.Entries,
            entry =>
                entry.CategoryName == typeof(RabbitMqOrderCreatedPublisher).FullName &&
                entry.EventId.Name == "PublishSucceeded");

        Assert.Equal(LogLevel.Information, publishLog.Level);
        Assert.Null(publishLog.Exception);
        Assert.Contains(publishLog.Properties, property => property.Key == "MessageId" && Equals(property.Value, message.MessageId));
        Assert.Contains(publishLog.Properties, property => property.Key == "CorrelationId" && Equals(property.Value, correlationId));
        Assert.Contains(publishLog.Properties, property => property.Key == "OrderId" && Equals(property.Value, committedOrder.Id));
        Assert.Contains(publishLog.Properties, property => property.Key == "UserId" && Equals(property.Value, userId));
        Assert.Contains(publishLog.Properties, property => property.Key == "EventVersion" && Equals(property.Value, 1));
        Assert.Contains(publishLog.Properties, property => property.Key == "Exchange" && Equals(property.Value, "oims.events"));
        Assert.Contains(publishLog.Properties, property => property.Key == "RoutingKey" && Equals(property.Value, "order.created.v1"));
        Assert.DoesNotContain(
            publishLog.Properties,
            property => property.Key is "Payload" or "Body" or "Password" or "Token" or "IdempotencyKey");
    }

    [Fact]
    [Trait("Requirement", "S9-T04")]
    [Trait("TestId", "MSG-PUBLISH-002")]
    [Trait("TestId", "API-IDEM-010")]
    public async Task CreateOrder_BrokerUnavailableAfterCommit_ReturnsStoredCreatedWithoutRepublishingOnReplay()
    {
        await using var factory = CreateApiFactory();
        var seeded = await SeedAuthenticatedOrderDependenciesAsync(factory);
        using var client = factory.CreateClient();
        await AuthenticateAsync(client, seeded.Email);
        var idempotencyKey = Guid.NewGuid();

        using (var topologyScope = factory.Services.CreateScope())
        {
            var topology = topologyScope.ServiceProvider
                .GetRequiredService<IRabbitMqTopologyInitializer>();
            await topology.InitializeAsync();
        }

        await rabbitMq.StopBrokerAsync();
        try
        {
            using var firstRequest = CreateRequest(
                idempotencyKey,
                seeded.ProductVariantId);
            using var firstResponse = await client.SendAsync(firstRequest);
            var firstBody = await firstResponse.Content.ReadAsByteArrayAsync();

            Assert.Equal(HttpStatusCode.Created, firstResponse.StatusCode);

            using var replayRequest = CreateRequest(
                idempotencyKey,
                seeded.ProductVariantId);
            using var replayResponse = await client.SendAsync(replayRequest);
            var replayBody = await replayResponse.Content.ReadAsByteArrayAsync();

            Assert.Equal(HttpStatusCode.Created, replayResponse.StatusCode);
            Assert.Equal(
                "true",
                Assert.Single(
                    replayResponse.Headers.GetValues("Idempotency-Replayed")));
            Assert.Equal(firstBody, replayBody);

            using var assertionScope = factory.Services.CreateScope();
            var dbContext = assertionScope.ServiceProvider
                .GetRequiredService<OrderSystemDbContext>();
            var order = await dbContext.Orders.AsNoTracking()
                .SingleAsync(candidate => candidate.UserId == seeded.UserId);

            Assert.Single(await dbContext.OrderItems.AsNoTracking()
                .Where(item => item.OrderId == order.Id)
                .ToListAsync());
            Assert.Single(await dbContext.InventoryTransactions.AsNoTracking()
                .Where(transaction =>
                    transaction.ReferenceId == order.Id &&
                    transaction.Type == InventoryTransactionType.Reserve)
                .ToListAsync());
            Assert.Single(await dbContext.IdempotencyRequests.AsNoTracking()
                .Where(request =>
                    request.UserId == seeded.UserId &&
                    request.Operation == IdempotencyOperation.CreateOrder &&
                    request.Status == IdempotencyRequestStatus.Completed)
                .ToListAsync());

            var inventory = await dbContext.Inventories.AsNoTracking()
                .SingleAsync(item =>
                    item.ProductVariantId == seeded.ProductVariantId);
            Assert.Equal(1, inventory.ReservedQuantity);
        }
        finally
        {
            await rabbitMq.StartBrokerAsync();
        }
    }

    [Fact]
    [Trait("Requirement", "S9-T04")]
    public async Task CreateOrder_UnroutableAfterCommit_ReturnsStoredCreated()
    {
        await using (var topologyFactory = CreateApiFactory())
        {
            using var topologyScope = topologyFactory.Services.CreateScope();
            var topology = topologyScope.ServiceProvider
                .GetRequiredService<IRabbitMqTopologyInitializer>();
            await topology.InitializeAsync();
        }

        var unroutableRoutingKey =
            $"order.created.unroutable.{Guid.NewGuid():N}";
        await using var factory = CreateApiFactory(unroutableRoutingKey);
        var seeded = await SeedAuthenticatedOrderDependenciesAsync(factory);
        using var client = factory.CreateClient();
        await AuthenticateAsync(client, seeded.Email);
        var idempotencyKey = Guid.NewGuid();

        using var request = CreateRequest(
            idempotencyKey,
            seeded.ProductVariantId);
        using var response = await client.SendAsync(request);

        using var assertionScope = factory.Services.CreateScope();
        var dbContext = assertionScope.ServiceProvider
            .GetRequiredService<OrderSystemDbContext>();
        var order = await dbContext.Orders.AsNoTracking()
            .SingleAsync(candidate => candidate.UserId == seeded.UserId);
        var idempotencyRequest = await dbContext.IdempotencyRequests
            .AsNoTracking()
            .SingleAsync(candidate =>
                candidate.UserId == seeded.UserId &&
                candidate.Operation == IdempotencyOperation.CreateOrder &&
                candidate.IdempotencyKey == idempotencyKey);

        Assert.NotEqual(Guid.Empty, order.Id);
        Assert.Equal(
            IdempotencyRequestStatus.Completed,
            idempotencyRequest.Status);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    private WebApplicationFactory<Program> CreateFactory(
        Guid userId,
        Guid correlationId,
        ILoggerProvider? loggerProvider = null) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, configuration) =>
                configuration.AddOimsTestConfiguration(
                    new("Database:ConnectionString", postgres.ConnectionString),
                    new("RabbitMq:Host", rabbitMq.Host),
                    new("RabbitMq:Port", rabbitMq.Port.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                    new("RabbitMq:VirtualHost", "/"),
                    new("RabbitMq:Username", rabbitMq.Username),
                    new("RabbitMq:Password", rabbitMq.Password)));
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<ICurrentUser>();
                services.AddScoped<ICurrentUser>(_ => new TestCurrentUser(userId));
                services.RemoveAll<IClock>();
                services.AddSingleton<IClock>(new FixedClock(FixedNow));
                services.RemoveAll<ICorrelationContext>();
                services.AddScoped<ICorrelationContext>(_ => new TestCorrelationContext(correlationId));

                if (loggerProvider is not null)
                {
                    services.Replace(
                        ServiceDescriptor.Singleton<ILoggerFactory>(
                            _ => new LoggerFactory([loggerProvider])));
                }
            });
        });

    private WebApplicationFactory<Program> CreateApiFactory(
    string orderCreatedRoutingKey = "order.created.v1") =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.ConfigureAppConfiguration((_, configuration) =>
                configuration.AddOimsTestConfiguration(
                    new(
                        "Database:ConnectionString",
                        postgres.ConnectionString),
                    new(
                        "RabbitMq:Host",
                        rabbitMq.Host),
                    new(
                        "RabbitMq:Port",
                        rabbitMq.Port.ToString(
                            System.Globalization.CultureInfo.InvariantCulture)),
                    new(
                        "RabbitMq:VirtualHost",
                        "/"),
                    new(
                        "RabbitMq:Username",
                        rabbitMq.Username),
                    new(
                        "RabbitMq:Password",
                        rabbitMq.Password),
                    new(
                        "RabbitMq:OrderCreatedRoutingKey",
                        orderCreatedRoutingKey))));

    private static async Task<AuthenticatedOrderDependencies>
    SeedAuthenticatedOrderDependenciesAsync(
        WebApplicationFactory<Program> factory)
    {
        using var scope = factory.Services.CreateScope();
        var provider = scope.ServiceProvider;
        var dbContext = provider.GetRequiredService<OrderSystemDbContext>();
        await dbContext.Database.MigrateAsync();

        var userId = Guid.NewGuid();
        var email = $"publisher-{userId:N}@example.com";
        var now = FixedNow;
        var product = new Product(
            Guid.NewGuid(),
            $"Publisher outage product {Guid.NewGuid():N}",
            "Publisher outage integration test product",
            CatalogStatus.Active,
            now);
        var productVariant = new ProductVariant(
            Guid.NewGuid(),
            product.Id,
            $"OUT-{Guid.NewGuid():N}"[..16],
            "Publisher outage integration test variant",
            10m,
            CatalogStatus.Active,
            now);
        var passwordHasher = provider.GetRequiredService<IPasswordHasher>();

        dbContext.AddRange(
            new User(
                userId,
                email,
                email,
                passwordHasher.Hash(TestCredentials.ValidPassword),
                UserRole.Customer,
                now),
            product,
            productVariant,
            new Inventory(
                Guid.NewGuid(),
                productVariant.Id,
                initialOnHand: 1,
                now));
        await dbContext.SaveChangesAsync();

        return new AuthenticatedOrderDependencies(
            userId,
            email,
            productVariant.Id);
    }

    private static HttpRequestMessage CreateRequest(
    Guid idempotencyKey,
    Guid productVariantId)
    {
        var request = new HttpRequestMessage(
            HttpMethod.Post,
            "/api/orders")
        {
            Content = JsonContent.Create(new
            {
                items = new[]
                {
                new
                {
                    productVariantId,
                    quantity = 1
                }
            }
            })
        };
        request.Headers.Add(
            "Idempotency-Key",
            idempotencyKey.ToString("D"));

        return request;
    }

    private static async Task AuthenticateAsync(
        HttpClient client,
        string email)
    {
        using var response = await client.PostAsJsonAsync(
            "/api/auth/login",
            new
            {
                email,
                password = TestCredentials.ValidPassword
            });
        response.EnsureSuccessStatusCode();

        using var document = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync());
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue(
                "Bearer",
                document.RootElement
                    .GetProperty("data")
                    .GetProperty("accessToken")
                    .GetString());
    }

    private static async Task<Guid> SeedOrderDependenciesAsync(
        OrderSystemDbContext dbContext,
        Guid userId)
    {
        var now = FixedNow;
        var product = new Product(
            Guid.NewGuid(),
            $"Publisher product {Guid.NewGuid():N}",
            "Publisher integration test product",
            CatalogStatus.Active,
            now);
        var productVariant = new ProductVariant(
            Guid.NewGuid(),
            product.Id,
            $"PUB-{Guid.NewGuid():N}"[..16],
            "Publisher integration test variant",
            10m,
            CatalogStatus.Active,
            now);

        dbContext.AddRange(
            new User(
                userId,
                $"{userId:N}@example.com",
                $"{userId:N}@example.com",
                "test-password-hash",
                UserRole.Customer,
                now),
            product,
            productVariant,
            new Inventory(Guid.NewGuid(), productVariant.Id, initialOnHand: 1, now));
        await dbContext.SaveChangesAsync();

        return productVariant.Id;
    }

    private sealed record TestCurrentUser(Guid UserId) : ICurrentUser
    {
        public bool IsAuthenticated => true;

        Guid? ICurrentUser.UserId => UserId;

        public UserRole? Role => UserRole.Customer;
    }

    private sealed record TestCorrelationContext(Guid CorrelationId) : ICorrelationContext;

    private sealed record FixedClock(DateTimeOffset UtcNow) : IClock;

    private sealed record AuthenticatedOrderDependencies(
    Guid UserId,
    string Email,
    Guid ProductVariantId);

    private sealed class RecordingLoggerProvider : ILoggerProvider
    {
        private readonly ConcurrentQueue<LogEntry> entries = [];

        public IReadOnlyCollection<LogEntry> Entries => entries.ToArray();

        public ILogger CreateLogger(string categoryName) =>
            new RecordingLogger(categoryName, entries);

        public void Dispose()
        {
        }

        private sealed class RecordingLogger(
            string categoryName,
            ConcurrentQueue<LogEntry> entries) : ILogger
        {
            public IDisposable BeginScope<TState>(TState state)
                where TState : notnull =>
                EmptyScope.Instance;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                var properties = state is IEnumerable<KeyValuePair<string, object?>> values
                    ? values.ToArray()
                    : [];

                entries.Enqueue(
                    new LogEntry(
                        categoryName,
                        logLevel,
                        eventId,
                        properties,
                        exception));
            }
        }
    }

    private sealed record LogEntry(
        string CategoryName,
        LogLevel Level,
        EventId EventId,
        IReadOnlyList<KeyValuePair<string, object?>> Properties,
        Exception? Exception);

    private sealed class EmptyScope : IDisposable
    {
        public static EmptyScope Instance { get; } = new();

        public void Dispose()
        {
        }
    }
}
