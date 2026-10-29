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
using OrderSystem.Application.Authentication;
using OrderSystem.Application.Common.Clock;
using OrderSystem.Application.Messaging.Contracts;
using OrderSystem.Domain.Notifications;
using OrderSystem.Domain.Users;
using OrderSystem.Infrastructure.Messaging;
using OrderSystem.Infrastructure.Notifications;
using OrderSystem.Infrastructure.Persistence;
using OrderSystem.IntegrationTests.Infrastructure;

namespace OrderSystem.IntegrationTests.Api;

[Collection(PostgreSqlCollectionDefinition.Name)]
public sealed class MessageFailureOperationsApiTests(PostgreSqlFixture postgres)
{
    [Fact]
    [Trait("Requirement", "S10-T08")]
    [Trait("TestId", "MSG-REDRIVE-001")]
    public async Task Redrive_EligibleRetryExhaustedFailure_PersistsOneRequest()
    {
        var now = PostgreSqlUtcNow();
        await using var factory = CreateFactory(now);
        var seeded = await SeedFailureAsync(
            factory,
            now,
            FailedMessageFailureKind.RetryExhausted,
            failureCode: "retry_exhausted",
            attemptCount: 3);
        using var client = factory.CreateClient();
        await LoginAndSetBearerAsync(client, seeded.AdminEmail);

        const string reason = "Dependency recovery verified";
        using var firstResponse = await client.PostAsJsonAsync(
            $"/api/operations/message-failures/{seeded.FailedMessageId:D}/redrive",
            new { reason });

        Assert.Equal(HttpStatusCode.Accepted, firstResponse.StatusCode);

        using var firstDocument = JsonDocument.Parse(
            await firstResponse.Content.ReadAsStringAsync());
        Assert.Equal(
            "RedriveRequested",
            firstDocument.RootElement
                .GetProperty("data")
                .GetProperty("status")
                .GetString());

        using var firstScope = factory.Services.CreateScope();
        var firstContext = firstScope.ServiceProvider
            .GetRequiredService<OrderSystemDbContext>();
        var firstPersisted = await firstContext.FailedMessages
            .AsNoTracking()
            .SingleAsync(candidate => candidate.Id == seeded.FailedMessageId);

        Assert.Equal(FailedMessageStatus.RedriveRequested, firstPersisted.Status);
        Assert.Equal(seeded.AdminId, firstPersisted.RedriveRequestedByUserId);
        Assert.Equal(reason, firstPersisted.RedriveReason);
        Assert.Equal(now, firstPersisted.RedriveRequestedAt);
        Assert.Null(firstPersisted.RedrivenAt);
        Assert.Null(firstPersisted.TerminalAt);
        Assert.Equal(seeded.Body, firstPersisted.Body);

        using var replayResponse = await client.PostAsJsonAsync(
            $"/api/operations/message-failures/{seeded.FailedMessageId:D}/redrive",
            new { reason });

        Assert.Equal(HttpStatusCode.Accepted, replayResponse.StatusCode);

        using var replayScope = factory.Services.CreateScope();
        var replayContext = replayScope.ServiceProvider
            .GetRequiredService<OrderSystemDbContext>();
        var replayed = await replayContext.FailedMessages
            .AsNoTracking()
            .SingleAsync(candidate => candidate.Id == seeded.FailedMessageId);

        Assert.Equal(firstPersisted.RedriveRequestedAt, replayed.RedriveRequestedAt);
        Assert.Equal(firstPersisted.RedriveRequestedByUserId, replayed.RedriveRequestedByUserId);
        Assert.Equal(firstPersisted.RedriveReason, replayed.RedriveReason);
    }

    [Fact]
    [Trait("Requirement", "S10-T08")]
    [Trait("TestId", "MSG-REDRIVE-002")]
    public async Task Redrive_PermanentFailure_ReturnsConflictWithoutStateChange()
    {
        var now = PostgreSqlUtcNow();
        await using var factory = CreateFactory(now);
        var seeded = await SeedFailureAsync(
            factory,
            now,
            FailedMessageFailureKind.Permanent,
            failureCode: "order_not_found",
            attemptCount: 1);
        using var client = factory.CreateClient();
        await LoginAndSetBearerAsync(client, seeded.AdminEmail);

        using var response = await client.PostAsJsonAsync(
            $"/api/operations/message-failures/{seeded.FailedMessageId:D}/redrive",
            new { reason = "Retry after investigation" });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        using var document = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync());
        Assert.Equal(
            "MESSAGE_FAILURE_NOT_REDRIVABLE",
            document.RootElement.GetProperty("code").GetString());

        using var assertionScope = factory.Services.CreateScope();
        var dbContext = assertionScope.ServiceProvider
            .GetRequiredService<OrderSystemDbContext>();
        var persisted = await dbContext.FailedMessages
            .AsNoTracking()
            .SingleAsync(candidate => candidate.Id == seeded.FailedMessageId);

        Assert.Equal(FailedMessageStatus.Pending, persisted.Status);
        Assert.Null(persisted.RedriveRequestedAt);
        Assert.Null(persisted.RedriveRequestedByUserId);
        Assert.Null(persisted.RedriveReason);
        Assert.Null(persisted.RedrivenAt);
        Assert.Null(persisted.TerminalAt);
    }

    [Fact]
    [Trait("Requirement", "S10-T08")]
    public async Task Close_PendingFailure_PersistsOneTerminalDisposition()
    {
        var now = PostgreSqlUtcNow();
        await using var factory = CreateFactory(now);
        var seeded = await SeedFailureAsync(
            factory,
            now,
            FailedMessageFailureKind.Permanent,
            failureCode: "order_not_found",
            attemptCount: 1);
        using var client = factory.CreateClient();
        await LoginAndSetBearerAsync(client, seeded.AdminEmail);

        const string reason = "Investigation completed without redrive";
        using var firstResponse = await client.PostAsJsonAsync(
            $"/api/operations/message-failures/{seeded.FailedMessageId:D}/close",
            new { reason });

        Assert.Equal(HttpStatusCode.OK, firstResponse.StatusCode);

        using var firstDocument = JsonDocument.Parse(
            await firstResponse.Content.ReadAsStringAsync());
        Assert.Equal(
            "Closed",
            firstDocument.RootElement
                .GetProperty("data")
                .GetProperty("status")
                .GetString());

        using var firstScope = factory.Services.CreateScope();
        var firstContext = firstScope.ServiceProvider
            .GetRequiredService<OrderSystemDbContext>();
        var firstPersisted = await firstContext.FailedMessages
            .AsNoTracking()
            .SingleAsync(candidate => candidate.Id == seeded.FailedMessageId);

        Assert.Equal(FailedMessageStatus.Closed, firstPersisted.Status);
        Assert.Equal(now, firstPersisted.ClosedAt);
        Assert.Equal(seeded.AdminId, firstPersisted.ClosedByUserId);
        Assert.Equal(reason, firstPersisted.CloseReason);
        Assert.Equal(now, firstPersisted.TerminalAt);
        Assert.Null(firstPersisted.RedriveRequestedAt);
        Assert.Null(firstPersisted.RedriveRequestedByUserId);
        Assert.Null(firstPersisted.RedriveReason);
        Assert.Null(firstPersisted.RedrivenAt);

        using var replayResponse = await client.PostAsJsonAsync(
            $"/api/operations/message-failures/{seeded.FailedMessageId:D}/close",
            new { reason = "A different reason must not replace the first" });

        Assert.Equal(HttpStatusCode.OK, replayResponse.StatusCode);

        using var replayScope = factory.Services.CreateScope();
        var replayContext = replayScope.ServiceProvider
            .GetRequiredService<OrderSystemDbContext>();
        var replayed = await replayContext.FailedMessages
            .AsNoTracking()
            .SingleAsync(candidate => candidate.Id == seeded.FailedMessageId);

        Assert.Equal(firstPersisted.ClosedAt, replayed.ClosedAt);
        Assert.Equal(firstPersisted.ClosedByUserId, replayed.ClosedByUserId);
        Assert.Equal(firstPersisted.CloseReason, replayed.CloseReason);
        Assert.Equal(firstPersisted.TerminalAt, replayed.TerminalAt);
    }

    [Fact]
    [Trait("Requirement", "S10-T08")]
    [Trait("TestId", "MSG-REDRIVE-004")]
    public async Task PayloadAccess_AdminReceivesBodyOnlyAfterDurableAudit()
    {
        var now = PostgreSqlUtcNow();
        await using var factory = CreateFactory(now);
        var seeded = await SeedFailureAsync(
            factory,
            now,
            FailedMessageFailureKind.Permanent,
            failureCode: "order_not_found",
            attemptCount: 1);
        using var client = factory.CreateClient();
        await LoginAndSetBearerAsync(client, seeded.AdminEmail);

        using var response = await client.GetAsync(
            $"/api/operations/message-failures/{seeded.FailedMessageId:D}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var document = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync());
        var data = document.RootElement.GetProperty("data");

        Assert.Equal(seeded.FailedMessageId, data.GetProperty("id").GetGuid());
        Assert.Equal("OrderCreatedNotificationConsumer", data.GetProperty("consumerName").GetString());
        Assert.Equal("Permanent", data.GetProperty("failureKind").GetString());
        Assert.Equal("order_not_found", data.GetProperty("failureCode").GetString());
        Assert.Equal(1, data.GetProperty("attemptCount").GetInt32());
        Assert.Equal("Pending", data.GetProperty("status").GetString());
        Assert.Equal("oims.events", data.GetProperty("originalExchange").GetString());
        Assert.Equal("order.created.v1", data.GetProperty("originalRoutingKey").GetString());
        Assert.Equal(FailedMessageBodyFingerprint.Compute(seeded.Body), data.GetProperty("bodySha256").GetString());
        Assert.Equal(seeded.Body.Length, data.GetProperty("bodyLength").GetInt32());
        Assert.True(data.TryGetProperty("safeHeaders", out _));
        Assert.False(data.TryGetProperty("body", out _));

        const string reason = "Inspect payload after dependency incident";
        using var payloadResponse = await client.PostAsJsonAsync(
            $"/api/operations/message-failures/{seeded.FailedMessageId:D}/payload",
            new { reason });

        Assert.Equal(HttpStatusCode.OK, payloadResponse.StatusCode);

        using var payloadDocument = JsonDocument.Parse(
            await payloadResponse.Content.ReadAsStringAsync());
        Assert.Equal(
            Convert.ToBase64String(seeded.Body),
            payloadDocument.RootElement
                .GetProperty("data")
                .GetProperty("bodyBase64")
                .GetString());

        using var auditScope = factory.Services.CreateScope();
        var auditContext = auditScope.ServiceProvider
            .GetRequiredService<OrderSystemDbContext>();
        var audit = await auditContext.FailedMessagePayloadAccesses
            .AsNoTracking()
            .SingleAsync(candidate =>
                candidate.FailedMessageId == seeded.FailedMessageId);

        Assert.NotEqual(Guid.Empty, audit.Id);
        Assert.Equal(seeded.AdminId, audit.AccessedByUserId);
        Assert.Equal(reason, audit.Reason);
        Assert.Equal(now, audit.AccessedAt);
    }

    [Fact]
    [Trait("Requirement", "S10-T08")]
    public async Task List_AdminFiltersPendingFailuresWithPaginationMetadata()
    {
        var now = PostgreSqlUtcNow();
        await using var factory = CreateFactory(now);
        var seeded = await SeedFailureAsync(
            factory,
            now,
            FailedMessageFailureKind.Permanent,
            failureCode: "order_not_found",
            attemptCount: 1);
        var closed = await SeedFailureAsync(
            factory,
            now,
            FailedMessageFailureKind.Permanent,
            failureCode: "closed_failure",
            attemptCount: 1,
            close: true);
        using var client = factory.CreateClient();
        await LoginAndSetBearerAsync(client, seeded.AdminEmail);

        using var response = await client.GetAsync(
            "/api/operations/message-failures?status=Pending&page=1&pageSize=100");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var document = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync());
        var items = document.RootElement
            .GetProperty("data")
            .EnumerateArray()
            .ToArray();
        var item = items
            .Single(candidate =>
                candidate.GetProperty("id").GetGuid() == seeded.FailedMessageId);

        Assert.DoesNotContain(items, candidate =>
            candidate.GetProperty("id").GetGuid() == closed.FailedMessageId);
        Assert.Equal(seeded.MessageId, item.GetProperty("messageId").GetGuid());
        Assert.Equal("OrderCreatedNotificationConsumer", item.GetProperty("consumerName").GetString());
        Assert.Equal("Permanent", item.GetProperty("failureKind").GetString());
        Assert.Equal("order_not_found", item.GetProperty("failureCode").GetString());
        Assert.Equal(1, item.GetProperty("attemptCount").GetInt32());
        Assert.Equal("Pending", item.GetProperty("status").GetString());
        Assert.Equal(seeded.OccurredAt, item.GetProperty("occurredAt").GetDateTimeOffset());
        Assert.Equal(seeded.CapturedAt, item.GetProperty("capturedAt").GetDateTimeOffset());
        Assert.Equal(JsonValueKind.Null, item.GetProperty("terminalAt").ValueKind);
        Assert.False(item.TryGetProperty("originalExchange", out _));
        Assert.False(item.TryGetProperty("bodySha256", out _));
        Assert.False(item.TryGetProperty("bodyLength", out _));
        Assert.False(item.TryGetProperty("body", out _));

        var pagination = document.RootElement
            .GetProperty("metadata")
            .GetProperty("pagination");
        Assert.Equal(1, pagination.GetProperty("page").GetInt32());
        Assert.Equal(100, pagination.GetProperty("pageSize").GetInt32());
        Assert.True(pagination.GetProperty("totalCount").GetInt64() >= 1);
        Assert.True(pagination.GetProperty("totalPages").GetInt32() >= 1);
    }

    [Fact]
    [Trait("Requirement", "S10-T08")]
    public async Task Operations_AnonymousAndCustomerAreRejectedWithoutStateChange()
    {
        var now = PostgreSqlUtcNow();
        await using var factory = CreateFactory(now);
        var seeded = await SeedFailureAsync(
            factory,
            now,
            FailedMessageFailureKind.RetryExhausted,
            failureCode: "retry_exhausted",
            attemptCount: 3);
        var customerEmail = await SeedCustomerAsync(factory, now);
        var detailPath = $"/api/operations/message-failures/{seeded.FailedMessageId:D}";

        using (var anonymousClient = factory.CreateClient())
        {
            Assert.Equal(
                HttpStatusCode.Unauthorized,
                (await anonymousClient.GetAsync("/api/operations/message-failures")).StatusCode);
            Assert.Equal(
                HttpStatusCode.Unauthorized,
                (await anonymousClient.GetAsync(detailPath)).StatusCode);
            Assert.Equal(
                HttpStatusCode.Unauthorized,
                (await anonymousClient.PostAsJsonAsync($"{detailPath}/payload", new { reason = "Inspect" })).StatusCode);
            Assert.Equal(
                HttpStatusCode.Unauthorized,
                (await anonymousClient.PostAsJsonAsync($"{detailPath}/redrive", new { reason = "Retry" })).StatusCode);
            Assert.Equal(
                HttpStatusCode.Unauthorized,
                (await anonymousClient.PostAsJsonAsync($"{detailPath}/close", new { reason = "Close" })).StatusCode);
        }

        using (var customerClient = factory.CreateClient())
        {
            await LoginAndSetBearerAsync(customerClient, customerEmail);
            Assert.Equal(
                HttpStatusCode.Forbidden,
                (await customerClient.GetAsync("/api/operations/message-failures")).StatusCode);
            Assert.Equal(
                HttpStatusCode.Forbidden,
                (await customerClient.GetAsync(detailPath)).StatusCode);
            Assert.Equal(
                HttpStatusCode.Forbidden,
                (await customerClient.PostAsJsonAsync($"{detailPath}/payload", new { reason = "Inspect" })).StatusCode);
            Assert.Equal(
                HttpStatusCode.Forbidden,
                (await customerClient.PostAsJsonAsync($"{detailPath}/redrive", new { reason = "Retry" })).StatusCode);
            Assert.Equal(
                HttpStatusCode.Forbidden,
                (await customerClient.PostAsJsonAsync($"{detailPath}/close", new { reason = "Close" })).StatusCode);
        }

        using var assertionScope = factory.Services.CreateScope();
        var dbContext = assertionScope.ServiceProvider.GetRequiredService<OrderSystemDbContext>();
        var persisted = await dbContext.FailedMessages
            .AsNoTracking()
            .SingleAsync(candidate => candidate.Id == seeded.FailedMessageId);
        Assert.Equal(FailedMessageStatus.Pending, persisted.Status);
        Assert.Null(persisted.RedriveRequestedAt);
        Assert.Null(persisted.ClosedAt);
        Assert.Empty(await dbContext.FailedMessagePayloadAccesses
            .Where(candidate => candidate.FailedMessageId == seeded.FailedMessageId)
            .ToListAsync());
    }

    private WebApplicationFactory<Program> CreateFactory(DateTimeOffset now) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, configuration) =>
                configuration.AddOimsTestConfiguration(
                    new KeyValuePair<string, string?>(
                        "Database:ConnectionString",
                        postgres.ConnectionString)));
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IClock>();
                services.AddSingleton<IClock>(new FixedClock(now));
            });
        });

    private static async Task<SeededFailure> SeedFailureAsync(
        WebApplicationFactory<Program> factory,
        DateTimeOffset now,
        FailedMessageFailureKind failureKind,
        string failureCode,
        int attemptCount,
        bool close = false)
    {
        using var scope = factory.Services.CreateScope();
        var provider = scope.ServiceProvider;
        var dbContext = provider.GetRequiredService<OrderSystemDbContext>();
        await dbContext.Database.MigrateAsync();

        var adminId = Guid.NewGuid();
        var adminEmail = $"message-failure-admin-{adminId:N}@example.com";
        var failedMessageId = Guid.NewGuid();
        var message = new OrderCreatedV1(
            EventVersion: 1,
            MessageId: Guid.NewGuid(),
            OrderId: Guid.NewGuid(),
            UserId: Guid.NewGuid(),
            CorrelationId: Guid.NewGuid(),
            OccurredAt: now.AddHours(-1));
        var body = OrderCreatedV1Serializer.Serialize(message);
        var passwordHasher = provider.GetRequiredService<IPasswordHasher>();
        var capturedAt = now.AddMinutes(-30);
        var failedMessage = new FailedMessage(
            failedMessageId,
            "OrderCreatedNotificationConsumer",
            message.MessageId,
            message.MessageId.ToString("D"),
            message.CorrelationId,
            "OrderCreated",
            message.EventVersion,
            message.OccurredAt,
            body,
            FailedMessageBodyFingerprint.Compute(body),
            safeHeaders: "{}",
            originalExchange: "oims.events",
            originalRoutingKey: "order.created.v1",
            failureKind,
            failureCode,
            attemptCount,
            capturedAt);
        if (close)
        {
            failedMessage.TryClose(adminId, "Resolved during test setup", now);
        }

        dbContext.AddRange(
            new User(
                adminId,
                adminEmail,
                adminEmail,
                passwordHasher.Hash(TestCredentials.ValidPassword),
                UserRole.Admin,
                now.AddDays(-1)),
            failedMessage);
        await dbContext.SaveChangesAsync();

        return new SeededFailure(
            adminId,
            adminEmail,
            failedMessageId,
            message.MessageId,
            message.OccurredAt,
            capturedAt,
            body);
    }

    private static async Task LoginAndSetBearerAsync(
        HttpClient client,
        string email)
    {
        using var login = await client.PostAsJsonAsync(
            "/api/auth/login",
            new
            {
                email,
                password = TestCredentials.ValidPassword
            });
        login.EnsureSuccessStatusCode();

        using var document = JsonDocument.Parse(
            await login.Content.ReadAsStringAsync());
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue(
                "Bearer",
                document.RootElement
                    .GetProperty("data")
                    .GetProperty("accessToken")
                    .GetString());
    }

    private static async Task<string> SeedCustomerAsync(
        WebApplicationFactory<Program> factory,
        DateTimeOffset now)
    {
        using var scope = factory.Services.CreateScope();
        var provider = scope.ServiceProvider;
        var dbContext = provider.GetRequiredService<OrderSystemDbContext>();
        var passwordHasher = provider.GetRequiredService<IPasswordHasher>();
        var customerId = Guid.NewGuid();
        var customerEmail = $"message-failure-customer-{customerId:N}@example.com";
        dbContext.Add(new User(
            customerId,
            customerEmail,
            customerEmail,
            passwordHasher.Hash(TestCredentials.ValidPassword),
            UserRole.Customer,
            now.AddDays(-1)));
        await dbContext.SaveChangesAsync();
        return customerEmail;
    }

    private static DateTimeOffset PostgreSqlUtcNow()
    {
        var now = DateTimeOffset.UtcNow;
        return now.AddTicks(-(now.Ticks % TimeSpan.TicksPerMicrosecond));
    }

    private sealed record FixedClock(DateTimeOffset UtcNow) : IClock;

    private sealed record SeededFailure(
        Guid AdminId,
        string AdminEmail,
        Guid FailedMessageId,
        Guid MessageId,
        DateTimeOffset OccurredAt,
        DateTimeOffset CapturedAt,
        byte[] Body);
}
