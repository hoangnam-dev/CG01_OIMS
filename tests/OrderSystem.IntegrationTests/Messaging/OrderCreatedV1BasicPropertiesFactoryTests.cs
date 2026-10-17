using OrderSystem.Application.Messaging.Contracts;
using OrderSystem.Infrastructure.Messaging;

namespace OrderSystem.IntegrationTests.Messaging;

public sealed class OrderCreatedV1BasicPropertiesFactoryTests
{
    [Fact]
    public void Create_ValidOrderCreatedV1_MapsApprovedAmqpProperties()
    {
        var message = new OrderCreatedV1(
            EventVersion: 1,
            MessageId: Guid.Parse("0199d28b-79a0-7c31-81be-b783e917d462"),
            OrderId: Guid.Parse("0199d28b-79a0-7c31-81be-b783e917d463"),
            UserId: Guid.Parse("0199d28b-79a0-7c31-81be-b783e917d464"),
            CorrelationId: Guid.Parse("0199d28b-79a0-7c31-81be-b783e917d465"),
            OccurredAt: new DateTimeOffset(
                2026,
                10,
                16,
                8,
                0,
                0,
                TimeSpan.Zero));

        var properties =
            OrderCreatedV1BasicPropertiesFactory.Create(message);

        Assert.Equal("application/json", properties.ContentType);
        Assert.Equal("OrderCreated", properties.Type);
        Assert.True(properties.Persistent);
        Assert.Equal(
            "0199d28b-79a0-7c31-81be-b783e917d462",
            properties.MessageId);
        Assert.Equal(
            "0199d28b-79a0-7c31-81be-b783e917d465",
            properties.CorrelationId);
    }
}