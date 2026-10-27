using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OrderSystem.Application.Notifications;
using OrderSystem.Domain.Notifications;
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

    private WebApplicationFactory<Program> CreateFactory() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.ConfigureAppConfiguration((_, configuration) =>
                configuration.AddOimsTestConfiguration(
                    new KeyValuePair<string, string?>(
                        "Database:ConnectionString",
                        postgres.ConnectionString))));
}