using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using OrderSystem.Api.Endpoints;
using OrderSystem.Application.Authentication;
using OrderSystem.Domain.Orders;
using OrderSystem.Domain.Payments;
using OrderSystem.Domain.Users;
using OrderSystem.Infrastructure.Payments.FakeProvider;
using OrderSystem.Infrastructure.Persistence;
using OrderSystem.IntegrationTests.Infrastructure;

namespace OrderSystem.IntegrationTests.Api;

[Collection(PostgreSqlCollectionDefinition.Name)]
public sealed class PaymentInitiationApiTests(PostgreSqlFixture postgres)
{
    [Fact]
    public async Task InitiatePayment_Anonymous_ReturnsUnauthorized()
    {
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();

        using var response = await client.PostAsJsonAsync(
            $"/api/orders/{Guid.NewGuid()}/payments",
            new { scenario = "SUCCESS" });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    [Trait("Requirement", "API-PAY-003")]
    public async Task InitiatePayment_CustomerWithPendingPaymentOrder_ReturnsCreatedPaymentAndReplay()
    {
        var now = DateTimeOffset.UtcNow;
        await using var factory = CreateFactory();
        var customer = await CreateCustomerAsync(factory, now);
        var orderId = await SeedPendingPaymentOrderAsync(factory, customer.Id, now);

        using var client = factory.CreateClient();
        await AuthenticateAsync(client, customer.Email);

        var idempotencyKey = Guid.NewGuid();
        client.DefaultRequestHeaders.Add("Idempotency-Key", idempotencyKey.ToString());

        using var initialResponse = await client.PostAsJsonAsync(
            $"/api/orders/{orderId}/payments",
            new { scenario = "SUCCESS" });

        Assert.Equal(HttpStatusCode.Created, initialResponse.StatusCode);

        using var initialDocument = JsonDocument.Parse(await initialResponse.Content.ReadAsStringAsync());

        var initialData = initialDocument.RootElement.GetProperty("data");
        var paymentId = initialData.GetProperty("id").GetGuid();

        Assert.Equal(orderId, initialData.GetProperty("orderId").GetGuid());
        Assert.Equal("Succeeded", initialData.GetProperty("status").GetString());
        Assert.Equal(125_000m, initialData.GetProperty("amount").GetDecimal());
        Assert.Equal("Fake", initialData.GetProperty("provider").GetString());
        Assert.Equal(JsonValueKind.Null, initialDocument.RootElement.GetProperty("metadata").ValueKind);

        using var replayResponse = await client.PostAsJsonAsync(
            $"/api/orders/{orderId}/payments",
            new { scenario = "SUCCESS" });

        Assert.Equal(HttpStatusCode.OK, replayResponse.StatusCode);
        Assert.True(replayResponse.Headers.TryGetValues("Idempotency-Replayed", out var replayValues));
        Assert.Contains("true", replayValues);

        using var replayDocument = JsonDocument.Parse(await replayResponse.Content.ReadAsStringAsync());

        Assert.Equal(paymentId, replayDocument.RootElement.GetProperty("data").GetProperty("id").GetGuid());
    }

    [Fact]
    [Trait("Requirement", "API-PAY-003")]
    public async Task InitiatePayment_ClientResponseLoss_AbortsInitialResponseAfterCommit_ThenReplaysSucceededPayment()
    {
        var now = DateTimeOffset.UtcNow;
        await using var factory = CreateFactory();
        var customer = await CreateCustomerAsync(factory, now);
        var orderId = await SeedPendingPaymentOrderAsync(factory, customer.Id, now);
        var idempotencyKey = Guid.NewGuid();

        using var initialClient = factory.CreateClient();
        await AuthenticateAsync(initialClient, customer.Email);
        initialClient.DefaultRequestHeaders.Add("Idempotency-Key", idempotencyKey.ToString());

        HttpResponseMessage? initialResponse = null;

        var initialFailure = await Record.ExceptionAsync(async () =>
        {
            initialResponse = await initialClient.PostAsJsonAsync(
                $"/api/orders/{orderId}/payments",
                new { scenario = "CLIENT_RESPONSE_LOST" });
        });

        initialResponse?.Dispose();

        Assert.Null(initialResponse);
        Assert.True(
            initialFailure is System.Net.Http.HttpRequestException or OperationCanceledException,
            $"Expected a transport failure after the response abort, but received: {initialFailure}");

        Guid paymentId;

        using (var assertionScope = factory.Services.CreateScope())
        {
            var db = assertionScope.ServiceProvider.GetRequiredService<OrderSystemDbContext>();

            var payment = await db.Payments
                .AsNoTracking()
                .SingleAsync(item => item.OrderId == orderId);

            var order = await db.Orders
                .AsNoTracking()
                .SingleAsync(item => item.Id == orderId);

            var providerOperationCount = await db.FakeProviderOperations
                .AsNoTracking()
                .CountAsync(item =>
                    item.OperationType == FakeProviderOperationType.CreatePayment &&
                    item.IdempotencyKey == payment.GatewayIdempotencyKey);

            Assert.Equal(PaymentStatus.Succeeded, payment.Status);
            Assert.Equal(OrderStatus.Confirmed, order.Status);
            Assert.Equal(1, providerOperationCount);

            paymentId = payment.Id;
        }

        using var retryClient = factory.CreateClient();
        await AuthenticateAsync(retryClient, customer.Email);
        retryClient.DefaultRequestHeaders.Add("Idempotency-Key", idempotencyKey.ToString());

        using var retryResponse = await retryClient.PostAsJsonAsync(
            $"/api/orders/{orderId}/payments",
            new { scenario = "CLIENT_RESPONSE_LOST" });

        Assert.Equal(HttpStatusCode.OK, retryResponse.StatusCode);
        Assert.Equal(
            "true",
            Assert.Single(retryResponse.Headers.GetValues("Idempotency-Replayed")));

        using var retryDocument = JsonDocument.Parse(
            await retryResponse.Content.ReadAsStringAsync());

        var retryPayment = retryDocument.RootElement.GetProperty("data");

        Assert.Equal(paymentId, retryPayment.GetProperty("id").GetGuid());
        Assert.Equal("Succeeded", retryPayment.GetProperty("status").GetString());
    }

    [Fact]
    public async Task InitiatePayment_ClientResponseLoss_RecordsBoundedResponseLostMetric()
    {
        var measurements = new ConcurrentQueue<
            (long Value, KeyValuePair<string, object?>[] Tags)>();

        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, meterListener) =>
            {
                if (instrument.Meter.Name == "OrderSystem.Api.Payments" &&
                    instrument.Name == "oims.payment.initiations")
                {
                    meterListener.EnableMeasurementEvents(instrument);
                }
            }
        };

        listener.SetMeasurementEventCallback<long>(
            (_, measurement, tags, _) =>
                measurements.Enqueue((measurement, tags.ToArray())));

        listener.Start();

        var now = DateTimeOffset.UtcNow;
        await using var factory = CreateFactory();
        var customer = await CreateCustomerAsync(factory, now);
        var orderId = await SeedPendingPaymentOrderAsync(factory, customer.Id, now);

        using var client = factory.CreateClient();
        await AuthenticateAsync(client, customer.Email);
        client.DefaultRequestHeaders.Add("Idempotency-Key", Guid.NewGuid().ToString());

        HttpResponseMessage? response = null;

        var failure = await Record.ExceptionAsync(async () =>
        {
            response = await client.PostAsJsonAsync(
                $"/api/orders/{orderId}/payments",
                new { scenario = "CLIENT_RESPONSE_LOST" });
        });

        response?.Dispose();

        Assert.Null(response);
        Assert.NotNull(failure);

        var metric = Assert.Single(measurements);

        Assert.Equal(1, metric.Value);
        Assert.Equal(
            [new KeyValuePair<string, object?>("outcome", "response_lost")],
            metric.Tags);
    }

    [Fact]
    public async Task InitiatePayment_ImmediateSuccess_RecordsSucceededMetric()
    {
        var measurements = new ConcurrentQueue<
            (long Value, KeyValuePair<string, object?>[] Tags)>();

        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, meterListener) =>
            {
                if (instrument.Meter.Name == "OrderSystem.Api.Payments" &&
                    instrument.Name == "oims.payment.initiations")
                {
                    meterListener.EnableMeasurementEvents(instrument);
                }
            }
        };

        listener.SetMeasurementEventCallback<long>(
            (_, measurement, tags, _) =>
                measurements.Enqueue((measurement, tags.ToArray())));

        listener.Start();

        var now = DateTimeOffset.UtcNow;
        await using var factory = CreateFactory();
        var customer = await CreateCustomerAsync(factory, now);
        var orderId = await SeedPendingPaymentOrderAsync(factory, customer.Id, now);

        using var client = factory.CreateClient();
        await AuthenticateAsync(client, customer.Email);
        client.DefaultRequestHeaders.Add("Idempotency-Key", Guid.NewGuid().ToString());

        using var response = await client.PostAsJsonAsync(
            $"/api/orders/{orderId}/payments",
            new { scenario = "SUCCESS" });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var metric = Assert.Single(measurements);

        Assert.Equal(1, metric.Value);
        Assert.Equal(
            [new KeyValuePair<string, object?>("outcome", "succeeded")],
            metric.Tags);
    }

    [Fact]
    public async Task InitiatePayment_AuthoritativeFailure_RecordsFailedMetric()
    {
        var measurements = new ConcurrentQueue<
            (long Value, KeyValuePair<string, object?>[] Tags)>();

        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, meterListener) =>
            {
                if (instrument.Meter.Name == "OrderSystem.Api.Payments" &&
                    instrument.Name == "oims.payment.initiations")
                {
                    meterListener.EnableMeasurementEvents(instrument);
                }
            }
        };

        listener.SetMeasurementEventCallback<long>(
            (_, measurement, tags, _) =>
                measurements.Enqueue((measurement, tags.ToArray())));

        listener.Start();

        var now = DateTimeOffset.UtcNow;
        await using var factory = CreateFactory();
        var customer = await CreateCustomerAsync(factory, now);
        var orderId = await SeedPendingPaymentOrderAsync(factory, customer.Id, now);

        using var client = factory.CreateClient();
        await AuthenticateAsync(client, customer.Email);
        client.DefaultRequestHeaders.Add("Idempotency-Key", Guid.NewGuid().ToString());

        using var response = await client.PostAsJsonAsync(
            $"/api/orders/{orderId}/payments",
            new { scenario = "FAILED" });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var metric = Assert.Single(measurements);

        Assert.Equal(1, metric.Value);
        Assert.Equal(
            [new KeyValuePair<string, object?>("outcome", "failed")],
            metric.Tags);
    }

    [Fact]
    public async Task InitiatePayment_ProviderResponseLoss_RecordsUnresolvedMetric()
    {
        var measurements = new ConcurrentQueue<
            (long Value, KeyValuePair<string, object?>[] Tags)>();

        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, meterListener) =>
            {
                if (instrument.Meter.Name == "OrderSystem.Api.Payments" &&
                    instrument.Name == "oims.payment.initiations")
                {
                    meterListener.EnableMeasurementEvents(instrument);
                }
            }
        };

        listener.SetMeasurementEventCallback<long>(
            (_, measurement, tags, _) =>
                measurements.Enqueue((measurement, tags.ToArray())));

        listener.Start();

        var now = DateTimeOffset.UtcNow;
        await using var factory = CreateFactory();
        var customer = await CreateCustomerAsync(factory, now);
        var orderId = await SeedPendingPaymentOrderAsync(factory, customer.Id, now);

        using var client = factory.CreateClient();
        await AuthenticateAsync(client, customer.Email);
        client.DefaultRequestHeaders.Add("Idempotency-Key", Guid.NewGuid().ToString());

        using var response = await client.PostAsJsonAsync(
            $"/api/orders/{orderId}/payments",
            new { scenario = "PROVIDER_RESPONSE_LOST" });

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        var metric = Assert.Single(measurements);

        Assert.Equal(1, metric.Value);
        Assert.Equal(
            [new KeyValuePair<string, object?>("outcome", "unresolved")],
            metric.Tags);
    }

    [Fact]
    public async Task InitiatePayment_ImmediateSuccess_LogsBoundedCompletion()
    {
        using var loggerProvider = new RecordingLoggerProvider();

        var now = DateTimeOffset.UtcNow;
        await using var factory = CreateFactory(loggerProvider: loggerProvider);
        var customer = await CreateCustomerAsync(factory, now);
        var orderId = await SeedPendingPaymentOrderAsync(factory, customer.Id, now);

        using var client = factory.CreateClient();
        await AuthenticateAsync(client, customer.Email);
        client.DefaultRequestHeaders.Add("Idempotency-Key", Guid.NewGuid().ToString());

        using var response = await client.PostAsJsonAsync(
            $"/api/orders/{orderId}/payments",
            new { scenario = "SUCCESS" });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var entry = Assert.Single(
            loggerProvider.Entries,
            item => item.CategoryName == typeof(PaymentInitiationEndpoints).FullName &&
                    item.EventId.Name == "PaymentInitiated");

        Assert.Equal(LogLevel.Information, entry.Level);
        Assert.Null(entry.Exception);

        Assert.Contains(
            entry.Properties,
            property => property.Key == "PaymentId" && property.Value is Guid);
        Assert.Contains(
            entry.Properties,
            property => property.Key == "OrderId" && Equals(property.Value, orderId));
        Assert.Contains(
            entry.Properties,
            property => property.Key == "ProviderPaymentId" &&
                        property.Value is string providerPaymentId &&
                        providerPaymentId.StartsWith("fake-pay-", StringComparison.Ordinal));
        Assert.Contains(
            entry.Properties,
            property => property.Key == "PaymentStatus" &&
                        Equals(property.Value, PaymentStatus.Succeeded));

        Assert.DoesNotContain(
            entry.Properties,
            property => property.Key.Contains("idempotency", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task InitiatePayment_ClientResponseLoss_LogsBoundedCommittedResponseLoss()
    {
        using var loggerProvider = new RecordingLoggerProvider();

        var now = DateTimeOffset.UtcNow;
        await using var factory = CreateFactory(loggerProvider: loggerProvider);
        var customer = await CreateCustomerAsync(factory, now);
        var orderId = await SeedPendingPaymentOrderAsync(factory, customer.Id, now);

        using var client = factory.CreateClient();
        await AuthenticateAsync(client, customer.Email);
        client.DefaultRequestHeaders.Add("Idempotency-Key", Guid.NewGuid().ToString());

        HttpResponseMessage? response = null;

        var failure = await Record.ExceptionAsync(async () =>
        {
            response = await client.PostAsJsonAsync(
                $"/api/orders/{orderId}/payments",
                new { scenario = "CLIENT_RESPONSE_LOST" });
        });

        response?.Dispose();

        Assert.Null(response);
        Assert.NotNull(failure);

        var entry = Assert.Single(
            loggerProvider.Entries,
            item => item.CategoryName == typeof(PaymentInitiationEndpoints).FullName &&
                    item.EventId.Name == "ClientResponseLost");

        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Null(entry.Exception);

        Assert.Contains(
            entry.Properties,
            property => property.Key == "PaymentId" && property.Value is Guid);
        Assert.Contains(
            entry.Properties,
            property => property.Key == "OrderId" && Equals(property.Value, orderId));
        Assert.Contains(
            entry.Properties,
            property => property.Key == "ProviderPaymentId" &&
                        property.Value is string providerPaymentId &&
                        providerPaymentId.StartsWith("fake-pay-", StringComparison.Ordinal));

        Assert.DoesNotContain(
            entry.Properties,
            property => property.Key.Contains("idempotency", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    [Trait("Requirement", "API-PAY-QUERY-001")]
    public async Task GetPayment_CustomerOwner_ReturnsCurrentAllowlistedPayment()
    {
        var now = DateTimeOffset.UtcNow;
        await using var factory = CreateFactory();
        var customer = await CreateCustomerAsync(factory, now);
        var orderId = await SeedPendingPaymentOrderAsync(factory, customer.Id, now);

        using var client = factory.CreateClient();
        await AuthenticateAsync(client, customer.Email);

        client.DefaultRequestHeaders.Add("Idempotency-Key", Guid.NewGuid().ToString());

        using var initiationResponse = await client.PostAsJsonAsync(
            $"/api/orders/{orderId}/payments",
            new { scenario = "SUCCESS" });

        initiationResponse.EnsureSuccessStatusCode();

        using var initiationDocument = JsonDocument.Parse(
            await initiationResponse.Content.ReadAsStringAsync());

        var paymentId = initiationDocument.RootElement
            .GetProperty("data")
            .GetProperty("id")
            .GetGuid();

        using var response = await client.GetAsync($"/api/payments/{paymentId}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var document = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync());

        var data = document.RootElement.GetProperty("data");

        Assert.Equal(paymentId, data.GetProperty("id").GetGuid());
        Assert.Equal(orderId, data.GetProperty("orderId").GetGuid());
        Assert.Equal("Succeeded", data.GetProperty("status").GetString());
        Assert.Equal("Fake", data.GetProperty("provider").GetString());
        Assert.False(data.TryGetProperty("gatewayIdempotencyKey", out _));
        Assert.False(data.TryGetProperty("refundIdempotencyKey", out _));
        Assert.False(data.TryGetProperty("refundAttemptCount", out _));
        Assert.Equal(JsonValueKind.Null, document.RootElement.GetProperty("metadata").ValueKind);
    }

    [Fact]
    [Trait("Requirement", "API-PAY-QUERY-002")]
    public async Task GetPaymentForOrder_CustomerOwner_ReturnsCurrentPayment()
    {
        var now = DateTimeOffset.UtcNow;
        await using var factory = CreateFactory();
        var customer = await CreateCustomerAsync(factory, now);
        var orderId = await SeedPendingPaymentOrderAsync(factory, customer.Id, now);

        using var client = factory.CreateClient();
        await AuthenticateAsync(client, customer.Email);

        client.DefaultRequestHeaders.Add("Idempotency-Key", Guid.NewGuid().ToString());

        using var initiationResponse = await client.PostAsJsonAsync(
            $"/api/orders/{orderId}/payments",
            new { scenario = "SUCCESS" });

        initiationResponse.EnsureSuccessStatusCode();

        using var initiationDocument = JsonDocument.Parse(
            await initiationResponse.Content.ReadAsStringAsync());

        var paymentId = initiationDocument.RootElement
            .GetProperty("data")
            .GetProperty("id")
            .GetGuid();

        using var response = await client.GetAsync($"/api/orders/{orderId}/payment");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var document = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync());

        var data = document.RootElement.GetProperty("data");

        Assert.Equal(paymentId, data.GetProperty("id").GetGuid());
        Assert.Equal(orderId, data.GetProperty("orderId").GetGuid());
        Assert.Equal("Succeeded", data.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, document.RootElement.GetProperty("metadata").ValueKind);
    }

    [Fact]
    public async Task OpenApi_DocumentsPaymentInitiationIdempotencyContract()
    {
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/openapi/v1.json");

        response.EnsureSuccessStatusCode();

        using var document = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync());

        var operation = document.RootElement
            .GetProperty("paths")
            .GetProperty("/api/orders/{orderId}/payments")
            .GetProperty("post");

        var idempotencyKey = operation
            .GetProperty("parameters")
            .EnumerateArray()
            .Single(parameter =>
                parameter.GetProperty("name").GetString() == "Idempotency-Key" &&
                parameter.GetProperty("in").GetString() == "header");

        Assert.True(idempotencyKey.GetProperty("required").GetBoolean());
        Assert.Equal("uuid", idempotencyKey.GetProperty("schema").GetProperty("format").GetString());

        var replayHeader = operation
            .GetProperty("responses")
            .GetProperty("200")
            .GetProperty("headers")
            .GetProperty("Idempotency-Replayed");

        Assert.Contains(
            "replay",
            replayHeader.GetProperty("description").GetString(),
            StringComparison.OrdinalIgnoreCase);
        Assert.Equal("boolean", replayHeader.GetProperty("schema").GetProperty("type").GetString());
    }

    [Fact]
    [Trait("Requirement", "API-PAY-ENV-001")]
    public async Task InitiatePayment_WhenPaymentModuleIsDisabled_ReturnsNotFound()
    {
        await using var factory = CreatePaymentDisabledFactory();
        using var client = factory.CreateClient();

        using var response = await client.PostAsJsonAsync(
            $"/api/orders/{Guid.NewGuid()}/payments",
            new { scenario = "SUCCESS" });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    [Trait("Requirement", "API-PAY-ENV-002")]
    public async Task InitiatePayment_WhenPaymentModuleIsExplicitlyEnabledInProduction_ReturnsUnauthorized()
    {
        await using var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Production");
                builder.ConfigureAppConfiguration(
                    (_, configuration) =>
                        configuration.AddOimsTestConfiguration(
                            new KeyValuePair<string, string?>("Payment:Enabled", bool.TrueString)));
            });

        using var client = factory.CreateClient();

        using var response = await client.PostAsJsonAsync(
            $"/api/orders/{Guid.NewGuid()}/payments",
            new { scenario = "SUCCESS" });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    [Trait("Requirement", "API-PAY-ENV-003")]
    public async Task InitiatePayment_WhenPaymentModuleIsEnabledInProduction_CreatesSimulatedPayment()
    {
        var now = DateTimeOffset.UtcNow;
        await using var factory = CreateFactory("Production");
        var customer = await CreateCustomerAsync(factory, now);
        var orderId = await SeedPendingPaymentOrderAsync(factory, customer.Id, now);

        using var client = factory.CreateClient();
        await AuthenticateAsync(client, customer.Email);
        client.DefaultRequestHeaders.Add("Idempotency-Key", Guid.NewGuid().ToString());

        using var response = await client.PostAsJsonAsync(
            $"/api/orders/{orderId}/payments",
            new { scenario = "SUCCESS" });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.Equal(orderId, document.RootElement.GetProperty("data").GetProperty("orderId").GetGuid());
        Assert.Equal("Succeeded", document.RootElement.GetProperty("data").GetProperty("status").GetString());
    }

    private WebApplicationFactory<Program> CreateFactory(
        string environment = "Development",
        ILoggerProvider? loggerProvider = null) =>
        new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment(environment);
                builder.ConfigureAppConfiguration(
                    (_, configuration) =>
                        configuration.AddOimsTestConfiguration(
                            new KeyValuePair<string, string?>(
                                "Database:ConnectionString",
                                postgres.ConnectionString)));

                if (loggerProvider is not null)
                {
                    builder.ConfigureServices(services =>
                        services.Replace(
                            ServiceDescriptor.Singleton<ILoggerFactory>(
                                _ => new LoggerFactory([loggerProvider]))));
                }
            }
    );

    private static async Task<TestUser> CreateCustomerAsync(
        WebApplicationFactory<Program> factory,
        DateTimeOffset now)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OrderSystemDbContext>();
        var hasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher>();

        await db.Database.MigrateAsync();

        var email = $"payment-customer-{Guid.NewGuid():N}@example.com";
        var customer = new TestUser(Guid.NewGuid(), email);

        db.Users.Add(new User(
            customer.Id,
            email,
            email,
            hasher.Hash(TestCredentials.ValidPassword),
            UserRole.Customer,
            now));

        await db.SaveChangesAsync();

        return customer;
    }

    private static WebApplicationFactory<Program> CreatePaymentDisabledFactory() =>
    new WebApplicationFactory<Program>()
        .WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            builder.ConfigureAppConfiguration(
                (_, configuration) =>
                    configuration.AddOimsTestConfiguration(
                        new KeyValuePair<string, string?>("Payment:Enabled", "false")));
        });

    private static async Task<Guid> SeedPendingPaymentOrderAsync(
        WebApplicationFactory<Program> factory,
        Guid userId,
        DateTimeOffset now)
    {
        var order = new Order(
            Guid.NewGuid(),
            userId,
            totalAmount: 125_000m,
            reservationExpiresAt: now.AddMinutes(10),
            createdAt: now);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OrderSystemDbContext>();

        db.Orders.Add(order);
        await db.SaveChangesAsync();

        return order.Id;
    }

    private static async Task AuthenticateAsync(HttpClient client, string email)
    {
        using var login = await client.PostAsJsonAsync(
            "/api/auth/login",
            new { email, password = TestCredentials.ValidPassword });

        login.EnsureSuccessStatusCode();

        using var document = JsonDocument.Parse(await login.Content.ReadAsStringAsync());

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            document.RootElement.GetProperty("data").GetProperty("accessToken").GetString());
    }

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
                var properties = state is IReadOnlyList<KeyValuePair<string, object?>> values
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

        private sealed class EmptyScope : IDisposable
        {
            public static readonly EmptyScope Instance = new();

            public void Dispose()
            {
            }
        }
    }

    private sealed record LogEntry(
        string CategoryName,
        LogLevel Level,
        EventId EventId,
        IReadOnlyList<KeyValuePair<string, object?>> Properties,
        Exception? Exception);

    private sealed record TestUser(Guid Id, string Email);
}
