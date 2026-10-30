using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OrderSystem.Application.Notifications;
using OrderSystem.Domain.Notifications;
using OrderSystem.Domain.Users;
using OrderSystem.Infrastructure.Notifications;
using OrderSystem.Infrastructure.Persistence;
using OrderSystem.IntegrationTests.Infrastructure;

namespace OrderSystem.IntegrationTests.Notifications;

[Collection(PostgreSqlCollectionDefinition.Name)]
public sealed class FailedMessageStoreTests(PostgreSqlFixture postgres)
{
    [Fact]
    [Trait("Requirement", "DB-MSG-002")]
    public async Task CaptureAsync_EmptyMalformedBody_CommitsExactEvidence()
    {
        await using var factory = CreateFactory();
        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<OrderSystemDbContext>();
        await dbContext.Database.MigrateAsync();

        var store = scope.ServiceProvider.GetRequiredService<IFailedMessageStore>();

        var id = Guid.NewGuid();
        var capturedAt = new DateTimeOffset(2026, 10, 27, 10, 0, 0, TimeSpan.Zero);
        byte[] body = [];
        var failedMessage = new FailedMessage(
            id,
            "OrderCreatedNotificationConsumer",
            messageId: null,
            rawMessageId: "not-a-guid",
            correlationId: null,
            eventType: null,
            eventVersion: null,
            occurredAt: null,
            body,
            FailedMessageBodyFingerprint.Compute(body),
            safeHeaders: "{}",
            originalExchange: "oims.events",
            originalRoutingKey: "order.created.v1",
            FailedMessageFailureKind.Malformed,
            failureCode: "malformed_json",
            attemptCount: 1,
            capturedAt);

        await store.CaptureAsync(failedMessage, CancellationToken.None);

        dbContext.ChangeTracker.Clear();
        var persisted = await dbContext.FailedMessages
            .AsNoTracking()
            .SingleAsync(message => message.Id == id);

        Assert.Empty(persisted.Body);
        Assert.Equal(
            "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855",
            persisted.BodySha256);
        Assert.Equal(FailedMessageStatus.Pending, persisted.Status);
        Assert.Equal(capturedAt, persisted.CapturedAt);
    }

    [Fact]
    [Trait("Requirement", "S10-T08")]
    public async Task GetNextRedriveRequestedAsync_FiltersByConsumerName()
    {
        await using var factory = CreateFactory();
        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<OrderSystemDbContext>();
        await dbContext.Database.MigrateAsync();

        var suffix = Guid.NewGuid().ToString("N");
        var requestedAt = new DateTimeOffset(2026, 10, 29, 12, 0, 0, TimeSpan.Zero);
        var adminId = Guid.NewGuid();
        var foreign = CreateRedriveRequestedFailure(
            Guid.NewGuid(),
            $"ForeignConsumer-{suffix}",
            adminId,
            requestedAt.AddMinutes(-1));
        var target = CreateRedriveRequestedFailure(
            Guid.NewGuid(),
            $"TargetConsumer-{suffix}",
            adminId,
            requestedAt);
        dbContext.AddRange(
            new User(
                adminId,
                $"redrive-store-admin-{suffix}@example.com",
                $"redrive-store-admin-{suffix}@example.com",
                TestCredentials.CreateHashPlaceholder(),
                UserRole.Admin,
                requestedAt.AddDays(-1)),
            foreign,
            target);
        await dbContext.SaveChangesAsync();

        var store = scope.ServiceProvider.GetRequiredService<IFailedMessageStore>();
        var candidate = await store.GetNextRedriveRequestedAsync(
            target.ConsumerName,
            CancellationToken.None);

        Assert.NotNull(candidate);
        Assert.Equal(target.Id, candidate.Id);
        Assert.Equal(target.ConsumerName, candidate.ConsumerName);
    }

    private static FailedMessage CreateRedriveRequestedFailure(
        Guid id,
        string consumerName,
        Guid adminId,
        DateTimeOffset requestedAt)
    {
        byte[] body = [];
        var failedMessage = new FailedMessage(
            id,
            consumerName,
            Guid.NewGuid(),
            rawMessageId: null,
            correlationId: Guid.NewGuid(),
            eventType: "OrderCreated",
            eventVersion: 1,
            occurredAt: requestedAt.AddHours(-1),
            body,
            FailedMessageBodyFingerprint.Compute(body),
            safeHeaders: "{}",
            originalExchange: "oims.events",
            originalRoutingKey: "order.created.v1",
            FailedMessageFailureKind.RetryExhausted,
            failureCode: "retry_exhausted",
            attemptCount: 3,
            capturedAt: requestedAt.AddMinutes(-30));
        Assert.True(failedMessage.TryRequestRedrive(adminId, "Dependency recovered", requestedAt));
        return failedMessage;
    }

    private WebApplicationFactory<Program> CreateFactory() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.ConfigureAppConfiguration((_, configuration) =>
                configuration.AddOimsTestConfiguration(
                    new KeyValuePair<string, string?>(
                        "Database:ConnectionString",
                        postgres.ConnectionString))));
}
