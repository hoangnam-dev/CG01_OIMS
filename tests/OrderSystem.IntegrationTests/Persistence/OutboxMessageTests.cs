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
