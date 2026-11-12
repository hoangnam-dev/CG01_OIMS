using System.Text;
using OrderSystem.Application.Messaging.Contracts;
using OrderSystem.Infrastructure.Messaging;
using OrderSystem.Infrastructure.Persistence;

namespace OrderSystem.IntegrationTests.Persistence;

public sealed class OutboxMessageTests
{
    [Fact]
    [Trait("Requirement", "S12-T02")]
    public void Constructor_MatchingPayloadMessageId_CreatesPendingIntent()
    {
        var message = new OrderCreatedV1(
            EventVersion: 1,
            MessageId: Guid.Parse("0199d28b-79a0-7c31-81be-b783e917d462"),
            OrderId: Guid.Parse("0199d28b-79a0-7c31-81be-b783e917d463"),
            UserId: Guid.Parse("0199d28b-79a0-7c31-81be-b783e917d464"),
            CorrelationId: Guid.Parse("0199d28b-79a0-7c31-81be-b783e917d465"),
            OccurredAt: new DateTimeOffset(2026, 11, 10, 8, 0, 0, TimeSpan.Zero));
        var payload = Encoding.UTF8.GetString(OrderCreatedV1Serializer.Serialize(message));

        var intent = new OutboxMessage(
            message.MessageId,
            message.OrderId,
            "OrderCreated",
            1,
            payload,
            message.CorrelationId,
            message.OccurredAt,
            "oims.events",
            "order.created.v1");

        Assert.Equal(message.MessageId, intent.MessageId);
        Assert.Equal(payload, intent.Payload);
        Assert.Equal(OutboxMessageStatus.Pending, intent.Status);
        Assert.Equal(0, intent.AttemptCount);
        Assert.Equal(message.OccurredAt, intent.NextAttemptAt);
    }

    [Fact]
    [Trait("Requirement", "S12-T06")]
    public void TryClaim_DuePendingIntent_RecordsPublishingLeaseAndAttempt()
    {
        var now = new DateTimeOffset(2026, 11, 11, 8, 0, 0, TimeSpan.Zero);
        var message = new OrderCreatedV1(
            EventVersion: 1,
            MessageId: Guid.NewGuid(),
            OrderId: Guid.NewGuid(),
            UserId: Guid.NewGuid(),
            CorrelationId: Guid.NewGuid(),
            OccurredAt: now.AddMinutes(-1));
        var intent = new OutboxMessage(
            message.MessageId, message.OrderId, "OrderCreated", 1,
            Encoding.UTF8.GetString(OrderCreatedV1Serializer.Serialize(message)),
            message.CorrelationId, message.OccurredAt, "oims.events", "order.created.v1");
        var claimToken = Guid.NewGuid();

        var claimed = intent.TryClaim(now, claimToken, TimeSpan.FromSeconds(60));

        Assert.True(claimed);
        Assert.Equal(OutboxMessageStatus.Publishing, intent.Status);
        Assert.Equal(claimToken, intent.ClaimToken);
        Assert.Equal(now.AddSeconds(60), intent.LeaseExpiresAt);
        Assert.Equal(1, intent.AttemptCount);
        Assert.Null(intent.NextAttemptAt);
    }

    [Fact]
    [Trait("Requirement", "S12-T06")]
    public void TryClaim_ExpiredPublishingLease_ReplacesTokenAndIncrementsAttempt()
    {
        var now = new DateTimeOffset(2026, 11, 11, 8, 0, 0, TimeSpan.Zero);
        var message = new OrderCreatedV1(
            EventVersion: 1,
            MessageId: Guid.NewGuid(),
            OrderId: Guid.NewGuid(),
            UserId: Guid.NewGuid(),
            CorrelationId: Guid.NewGuid(),
            OccurredAt: now.AddMinutes(-1));
        var intent = new OutboxMessage(
            message.MessageId, message.OrderId, "OrderCreated", 1,
            Encoding.UTF8.GetString(OrderCreatedV1Serializer.Serialize(message)),
            message.CorrelationId, message.OccurredAt, "oims.events", "order.created.v1");
        var firstToken = Guid.NewGuid();
        var recoveryToken = Guid.NewGuid();
        Assert.True(intent.TryClaim(now, firstToken, TimeSpan.FromSeconds(60)));

        var earlyClaim = intent.TryClaim(now.AddSeconds(59), recoveryToken, TimeSpan.FromSeconds(60));

        Assert.False(earlyClaim);
        Assert.Equal(firstToken, intent.ClaimToken);
        Assert.Equal(1, intent.AttemptCount);

        var recovered = intent.TryClaim(now.AddSeconds(60), recoveryToken, TimeSpan.FromSeconds(60));

        Assert.True(recovered);
        Assert.Equal(OutboxMessageStatus.Publishing, intent.Status);
        Assert.Equal(recoveryToken, intent.ClaimToken);
        Assert.Equal(now.AddSeconds(120), intent.LeaseExpiresAt);
        Assert.Equal(2, intent.AttemptCount);
        Assert.Null(intent.NextAttemptAt);
        Assert.Equal(message.MessageId, intent.MessageId);
        Assert.False(intent.TryMarkPublished(now.AddSeconds(61), firstToken));
        Assert.Equal(OutboxMessageStatus.Publishing, intent.Status);
    }

    [Fact]
    [Trait("Requirement", "S12-T06")]
    public void TryMarkPublished_OnlyCurrentClaimToken_CompletesIntent()
    {
        var now = new DateTimeOffset(2026, 11, 11, 8, 0, 0, TimeSpan.Zero);
        var message = new OrderCreatedV1(
            EventVersion: 1,
            MessageId: Guid.NewGuid(),
            OrderId: Guid.NewGuid(),
            UserId: Guid.NewGuid(),
            CorrelationId: Guid.NewGuid(),
            OccurredAt: now.AddMinutes(-1));
        var intent = new OutboxMessage(
            message.MessageId, message.OrderId, "OrderCreated", 1,
            Encoding.UTF8.GetString(OrderCreatedV1Serializer.Serialize(message)),
            message.CorrelationId, message.OccurredAt, "oims.events", "order.created.v1");
        var currentToken = Guid.NewGuid();
        Assert.True(intent.TryClaim(now, currentToken, TimeSpan.FromSeconds(60)));

        var staleResult = intent.TryMarkPublished(now.AddSeconds(1), Guid.NewGuid());

        Assert.False(staleResult);
        Assert.Equal(OutboxMessageStatus.Publishing, intent.Status);
        Assert.Equal(currentToken, intent.ClaimToken);

        var confirmedResult = intent.TryMarkPublished(now.AddSeconds(2), currentToken);

        Assert.True(confirmedResult);
        Assert.Equal(OutboxMessageStatus.Published, intent.Status);
        Assert.Equal(now.AddSeconds(2), intent.PublishedAt);
        Assert.Null(intent.ClaimToken);
        Assert.Null(intent.LeaseExpiresAt);
        Assert.Null(intent.NextAttemptAt);
        Assert.Equal(1, intent.AttemptCount);
    }

    [Fact]
    [Trait("Requirement", "S12-T06")]
    public void TryScheduleRetry_FirstTransientFailure_PreservesIntentAndSchedulesFiveSeconds()
    {
        var now = new DateTimeOffset(2026, 11, 11, 8, 0, 0, TimeSpan.Zero);
        var message = new OrderCreatedV1(
            EventVersion: 1,
            MessageId: Guid.NewGuid(),
            OrderId: Guid.NewGuid(),
            UserId: Guid.NewGuid(),
            CorrelationId: Guid.NewGuid(),
            OccurredAt: now.AddMinutes(-1));
        var payload = Encoding.UTF8.GetString(OrderCreatedV1Serializer.Serialize(message));
        var intent = new OutboxMessage(
            message.MessageId, message.OrderId, "OrderCreated", 1, payload,
            message.CorrelationId, message.OccurredAt, "oims.events", "order.created.v1");
        var currentToken = Guid.NewGuid();
        Assert.True(intent.TryClaim(now, currentToken, TimeSpan.FromSeconds(60)));
        var attemptedAt = now.AddSeconds(2);

        var staleResult = intent.TryScheduleRetry(
            attemptedAt, Guid.NewGuid(), OutboxFailureCategory.BrokerUnavailable);

        Assert.False(staleResult);
        Assert.Equal(OutboxMessageStatus.Publishing, intent.Status);
        Assert.Equal(currentToken, intent.ClaimToken);

        var currentResult = intent.TryScheduleRetry(
            attemptedAt, currentToken, OutboxFailureCategory.BrokerUnavailable);

        Assert.True(currentResult);
        Assert.Equal(OutboxMessageStatus.Pending, intent.Status);
        Assert.Equal(attemptedAt.AddSeconds(5), intent.NextAttemptAt);
        Assert.Equal(OutboxFailureCategory.BrokerUnavailable, intent.LastFailureCategory);
        Assert.Null(intent.ClaimToken);
        Assert.Null(intent.LeaseExpiresAt);
        Assert.Null(intent.PublishedAt);
        Assert.Equal(1, intent.AttemptCount);
        Assert.Equal(message.MessageId, intent.MessageId);
        Assert.Equal(payload, intent.Payload);
    }

    [Fact]
    [Trait("Requirement", "S12-T06")]
    public void TryScheduleRetry_Unroutable_RejectsTerminalCategoryWithoutMutation()
    {
        var now = new DateTimeOffset(2026, 11, 11, 8, 0, 0, TimeSpan.Zero);
        var message = new OrderCreatedV1(
            EventVersion: 1,
            MessageId: Guid.NewGuid(),
            OrderId: Guid.NewGuid(),
            UserId: Guid.NewGuid(),
            CorrelationId: Guid.NewGuid(),
            OccurredAt: now.AddMinutes(-1));
        var intent = new OutboxMessage(
            message.MessageId, message.OrderId, "OrderCreated", 1,
            Encoding.UTF8.GetString(OrderCreatedV1Serializer.Serialize(message)),
            message.CorrelationId, message.OccurredAt, "oims.events", "order.created.v1");
        var claimToken = Guid.NewGuid();
        Assert.True(intent.TryClaim(now, claimToken, TimeSpan.FromSeconds(60)));

        var exception = Assert.Throws<ArgumentOutOfRangeException>(() =>
        {
            intent.TryScheduleRetry(now.AddSeconds(2), claimToken, OutboxFailureCategory.Unroutable);
        });

        Assert.Equal("category", exception.ParamName);
        Assert.Equal(OutboxMessageStatus.Publishing, intent.Status);
        Assert.Equal(claimToken, intent.ClaimToken);
        Assert.Null(intent.NextAttemptAt);
        Assert.Null(intent.LastFailureCategory);
        Assert.Equal(1, intent.AttemptCount);
    }

    [Fact]
    [Trait("Requirement", "S12-T06")]
    public void TryMarkNeedsAttention_Unroutable_RetainsIntentWithoutRetry()
    {
        AssertTerminalFailureRetained(OutboxFailureCategory.Unroutable);
    }

    [Fact]
    [Trait("Requirement", "S12-T06")]
    public void TryMarkNeedsAttention_UnsupportedStoredContract_RetainsIntentWithoutRetry()
    {
        AssertTerminalFailureRetained(OutboxFailureCategory.UnsupportedStoredContract);
    }

    private static void AssertTerminalFailureRetained(OutboxFailureCategory category)
    {
        var now = new DateTimeOffset(2026, 11, 11, 8, 0, 0, TimeSpan.Zero);
        var message = new OrderCreatedV1(
            EventVersion: 1,
            MessageId: Guid.NewGuid(),
            OrderId: Guid.NewGuid(),
            UserId: Guid.NewGuid(),
            CorrelationId: Guid.NewGuid(),
            OccurredAt: now.AddMinutes(-1));
        var payload = Encoding.UTF8.GetString(OrderCreatedV1Serializer.Serialize(message));
        var intent = new OutboxMessage(
            message.MessageId, message.OrderId, "OrderCreated", 1, payload,
            message.CorrelationId, message.OccurredAt, "oims.events", "order.created.v1");
        var currentToken = Guid.NewGuid();
        Assert.True(intent.TryClaim(now, currentToken, TimeSpan.FromSeconds(60)));

        var staleResult = intent.TryMarkNeedsAttention(Guid.NewGuid(), category);

        Assert.False(staleResult);
        Assert.Equal(OutboxMessageStatus.Publishing, intent.Status);
        Assert.Equal(currentToken, intent.ClaimToken);

        var currentResult = intent.TryMarkNeedsAttention(currentToken, category);

        Assert.True(currentResult);
        Assert.Equal(OutboxMessageStatus.NeedsAttention, intent.Status);
        Assert.Equal(category, intent.LastFailureCategory);
        Assert.Null(intent.NextAttemptAt);
        Assert.Null(intent.ClaimToken);
        Assert.Null(intent.LeaseExpiresAt);
        Assert.Null(intent.PublishedAt);
        Assert.Equal(1, intent.AttemptCount);
        Assert.Equal(message.MessageId, intent.MessageId);
        Assert.Equal(payload, intent.Payload);
    }

    [Fact]
    [Trait("Requirement", "S12-T02")]
    public void Constructor_PayloadMessageIdDiffersFromStoredMessageId_RejectsMessage()
    {
        var message = new OrderCreatedV1(
            EventVersion: 1,
            MessageId: Guid.Parse("0199d28b-79a0-7c31-81be-b783e917d462"),
            OrderId: Guid.Parse("0199d28b-79a0-7c31-81be-b783e917d463"),
            UserId: Guid.Parse("0199d28b-79a0-7c31-81be-b783e917d464"),
            CorrelationId: Guid.Parse("0199d28b-79a0-7c31-81be-b783e917d465"),
            OccurredAt: new DateTimeOffset(2026, 11, 10, 8, 0, 0, TimeSpan.Zero));
        var payload = Encoding.UTF8.GetString(OrderCreatedV1Serializer.Serialize(message));

        Assert.Throws<ArgumentException>(() =>
        {
            _ = new OutboxMessage(
                Guid.Parse("0199d28b-79a0-7c31-81be-b783e917d466"),
                message.OrderId,
                "OrderCreated",
                1,
                payload,
                message.CorrelationId,
                message.OccurredAt,
                "oims.events",
                "order.created.v1");
        });
    }

    [Fact]
    [Trait("Requirement", "S12-T02")]
    public void Constructor_PayloadEventVersionDiffersFromStoredVersion_RejectsMessage()
    {
        var message = new OrderCreatedV1(
            EventVersion: 1,
            MessageId: Guid.Parse("0199d28b-79a0-7c31-81be-b783e917d462"),
            OrderId: Guid.Parse("0199d28b-79a0-7c31-81be-b783e917d463"),
            UserId: Guid.Parse("0199d28b-79a0-7c31-81be-b783e917d464"),
            CorrelationId: Guid.Parse("0199d28b-79a0-7c31-81be-b783e917d465"),
            OccurredAt: new DateTimeOffset(2026, 11, 10, 8, 0, 0, TimeSpan.Zero));
        var payload = Encoding.UTF8.GetString(OrderCreatedV1Serializer.Serialize(message));

        Assert.Throws<ArgumentException>(() =>
        {
            _ = new OutboxMessage(
                message.MessageId,
                message.OrderId,
                "OrderCreated",
                2,
                payload,
                message.CorrelationId,
                message.OccurredAt,
                "oims.events",
                "order.created.v1");
        });
    }

    [Fact]
    [Trait("Requirement", "S12-T02")]
    public void Constructor_PayloadCorrelationIdDiffersFromStoredCorrelationId_RejectsMessage()
    {
        var message = new OrderCreatedV1(
            EventVersion: 1,
            MessageId: Guid.Parse("0199d28b-79a0-7c31-81be-b783e917d462"),
            OrderId: Guid.Parse("0199d28b-79a0-7c31-81be-b783e917d463"),
            UserId: Guid.Parse("0199d28b-79a0-7c31-81be-b783e917d464"),
            CorrelationId: Guid.Parse("0199d28b-79a0-7c31-81be-b783e917d465"),
            OccurredAt: new DateTimeOffset(2026, 11, 10, 8, 0, 0, TimeSpan.Zero));
        var payload = Encoding.UTF8.GetString(OrderCreatedV1Serializer.Serialize(message));

        Assert.Throws<ArgumentException>(() =>
        {
            _ = new OutboxMessage(
                message.MessageId,
                message.OrderId,
                "OrderCreated",
                1,
                payload,
                Guid.Parse("0199d28b-79a0-7c31-81be-b783e917d466"),
                message.OccurredAt,
                "oims.events",
                "order.created.v1");
        });
    }

    [Fact]
    [Trait("Requirement", "S12-T02")]
    public void Constructor_PayloadOccurredAtDiffersFromStoredOccurredAt_RejectsMessage()
    {
        var message = new OrderCreatedV1(
            EventVersion: 1,
            MessageId: Guid.Parse("0199d28b-79a0-7c31-81be-b783e917d462"),
            OrderId: Guid.Parse("0199d28b-79a0-7c31-81be-b783e917d463"),
            UserId: Guid.Parse("0199d28b-79a0-7c31-81be-b783e917d464"),
            CorrelationId: Guid.Parse("0199d28b-79a0-7c31-81be-b783e917d465"),
            OccurredAt: new DateTimeOffset(2026, 11, 10, 8, 0, 0, TimeSpan.Zero));
        var payload = Encoding.UTF8.GetString(OrderCreatedV1Serializer.Serialize(message));

        Assert.Throws<ArgumentException>(() =>
        {
            _ = new OutboxMessage(
                message.MessageId,
                message.OrderId,
                "OrderCreated",
                1,
                payload,
                message.CorrelationId,
                message.OccurredAt.AddSeconds(1),
                "oims.events",
                "order.created.v1");
        });
    }

    [Fact]
    [Trait("Requirement", "S12-T02")]
    public void Constructor_NonUtcOccurredAt_RejectsMessage()
    {
        var message = new OrderCreatedV1(
            EventVersion: 1,
            MessageId: Guid.Parse("0199d28b-79a0-7c31-81be-b783e917d462"),
            OrderId: Guid.Parse("0199d28b-79a0-7c31-81be-b783e917d463"),
            UserId: Guid.Parse("0199d28b-79a0-7c31-81be-b783e917d464"),
            CorrelationId: Guid.Parse("0199d28b-79a0-7c31-81be-b783e917d465"),
            OccurredAt: new DateTimeOffset(2026, 11, 10, 15, 0, 0, TimeSpan.FromHours(7)));
        var payload = Encoding.UTF8.GetString(OrderCreatedV1Serializer.Serialize(message));

        Assert.Throws<ArgumentException>(() =>
        {
            _ = new OutboxMessage(
                message.MessageId,
                message.OrderId,
                "OrderCreated",
                1,
                payload,
                message.CorrelationId,
                message.OccurredAt,
                "oims.events",
                "order.created.v1");
        });
    }

    [Fact]
    [Trait("Requirement", "S12-T02")]
    public void Constructor_PayloadOrderIdDiffersFromStoredOrderId_RejectsMessage()
    {
        var message = new OrderCreatedV1(
            EventVersion: 1,
            MessageId: Guid.Parse("0199d28b-79a0-7c31-81be-b783e917d462"),
            OrderId: Guid.Parse("0199d28b-79a0-7c31-81be-b783e917d463"),
            UserId: Guid.Parse("0199d28b-79a0-7c31-81be-b783e917d464"),
            CorrelationId: Guid.Parse("0199d28b-79a0-7c31-81be-b783e917d465"),
            OccurredAt: new DateTimeOffset(2026, 11, 10, 8, 0, 0, TimeSpan.Zero));
        var payload = Encoding.UTF8.GetString(OrderCreatedV1Serializer.Serialize(message));

        Assert.Throws<ArgumentException>(() =>
        {
            _ = new OutboxMessage(
                message.MessageId,
                Guid.Parse("0199d28b-79a0-7c31-81be-b783e917d466"),
                "OrderCreated",
                1,
                payload,
                message.CorrelationId,
                message.OccurredAt,
                "oims.events",
                "order.created.v1");
        });
    }
}
