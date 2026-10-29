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
        var now = DateTimeOffset.UtcNow;
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
        var now = DateTimeOffset.UtcNow;
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
        int attemptCount)
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

        dbContext.AddRange(
            new User(
                adminId,
                adminEmail,
                adminEmail,
                passwordHasher.Hash(TestCredentials.ValidPassword),
                UserRole.Admin,
                now.AddDays(-1)),
            new FailedMessage(
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
                capturedAt: now.AddMinutes(-30)));
        await dbContext.SaveChangesAsync();

        return new SeededFailure(
            adminId,
            adminEmail,
            failedMessageId,
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

    private sealed record FixedClock(DateTimeOffset UtcNow) : IClock;

    private sealed record SeededFailure(
        Guid AdminId,
        string AdminEmail,
        Guid FailedMessageId,
        byte[] Body);
}
