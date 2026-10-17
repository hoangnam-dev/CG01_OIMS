using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using OrderSystem.Application.Messaging.Contracts;
using OrderSystem.Infrastructure.Messaging;

namespace OrderSystem.IntegrationTests.Messaging;

public sealed class OrderCreatedV1SerializerTests
{
    [Fact]
    public void SerializeThenDeserialize_ValidV1Message_PreservesApprovedContract()
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

        var payload = OrderCreatedV1Serializer.Serialize(message);

        using var document = JsonDocument.Parse(payload);
        var root = document.RootElement;

        var propertyNames = root
            .EnumerateObject()
            .Select(property => property.Name)
            .Order()
            .ToArray();

        Assert.Equal(
            [
                "correlationId",
                "eventVersion",
                "messageId",
                "occurredAt",
                "orderId",
                "userId"
            ],
            propertyNames);

        Assert.Equal(1, root.GetProperty("eventVersion").GetInt32());
        Assert.Equal(
            "0199d28b-79a0-7c31-81be-b783e917d462",
            root.GetProperty("messageId").GetString());
        Assert.Equal(
            "0199d28b-79a0-7c31-81be-b783e917d463",
            root.GetProperty("orderId").GetString());
        Assert.Equal(
            "0199d28b-79a0-7c31-81be-b783e917d464",
            root.GetProperty("userId").GetString());
        Assert.Equal(
            "0199d28b-79a0-7c31-81be-b783e917d465",
            root.GetProperty("correlationId").GetString());
        Assert.Equal(
            new DateTimeOffset(2026, 10, 16, 8, 0, 0, TimeSpan.Zero),
            root.GetProperty("occurredAt").GetDateTimeOffset());

        var success = OrderCreatedV1Serializer.TryDeserialize(
            payload,
            out var deserialized);

        Assert.True(success);

        var actual = Assert.IsType<OrderCreatedV1>(deserialized);
        Assert.Equal(1, actual.EventVersion);
        Assert.Equal(
            Guid.Parse("0199d28b-79a0-7c31-81be-b783e917d462"),
            actual.MessageId);
        Assert.Equal(
            Guid.Parse("0199d28b-79a0-7c31-81be-b783e917d463"),
            actual.OrderId);
        Assert.Equal(
            Guid.Parse("0199d28b-79a0-7c31-81be-b783e917d464"),
            actual.UserId);
        Assert.Equal(
            Guid.Parse("0199d28b-79a0-7c31-81be-b783e917d465"),
            actual.CorrelationId);
        Assert.Equal(
            new DateTimeOffset(2026, 10, 16, 8, 0, 0, TimeSpan.Zero),
            actual.OccurredAt);
    }

    [Fact]
    public void TryDeserialize_V1PayloadWithUnknownOptionalField_IgnoresUnknownField()
    {
        var payload = Encoding.UTF8.GetBytes(
            """
        {
          "eventVersion": 1,
          "messageId": "0199d28b-79a0-7c31-81be-b783e917d462",
          "orderId": "0199d28b-79a0-7c31-81be-b783e917d463",
          "userId": "0199d28b-79a0-7c31-81be-b783e917d464",
          "correlationId": "0199d28b-79a0-7c31-81be-b783e917d465",
          "occurredAt": "2026-10-16T08:00:00Z",
          "producerNote": "optional-v1-extension"
        }
        """);

        var success = OrderCreatedV1Serializer.TryDeserialize(
            payload,
            out var message);

        Assert.True(success);
        var actual = Assert.IsType<OrderCreatedV1>(message);
        Assert.Equal(
            Guid.Parse("0199d28b-79a0-7c31-81be-b783e917d462"),
            actual.MessageId);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public void TryDeserialize_UnsupportedEventVersion_ReturnsFalse(
        int unsupportedVersion)
    {
        var payloadJson = $$"""
        {
          "eventVersion": {{unsupportedVersion}},
          "messageId": "0199d28b-79a0-7c31-81be-b783e917d462",
          "orderId": "0199d28b-79a0-7c31-81be-b783e917d463",
          "userId": "0199d28b-79a0-7c31-81be-b783e917d464",
          "correlationId": "0199d28b-79a0-7c31-81be-b783e917d465",
          "occurredAt": "2026-10-16T08:00:00Z"
        }
        """;
        var payload = Encoding.UTF8.GetBytes(payloadJson);

        var success = OrderCreatedV1Serializer.TryDeserialize(
            payload,
            out var message);

        Assert.False(success);
        Assert.Null(message);
    }

    [Theory]
    [InlineData("messageId")]
    [InlineData("orderId")]
    [InlineData("userId")]
    [InlineData("correlationId")]
    public void TryDeserialize_EmptyRequiredUuid_ReturnsFalse(
    string requiredPropertyName)
    {
        var payloadJson = new JsonObject
        {
            ["eventVersion"] = 1,
            ["messageId"] = "0199d28b-79a0-7c31-81be-b783e917d462",
            ["orderId"] = "0199d28b-79a0-7c31-81be-b783e917d463",
            ["userId"] = "0199d28b-79a0-7c31-81be-b783e917d464",
            ["correlationId"] = "0199d28b-79a0-7c31-81be-b783e917d465",
            ["occurredAt"] = "2026-10-16T08:00:00Z"
        };

        payloadJson[requiredPropertyName] = Guid.Empty.ToString();
        var payload = Encoding.UTF8.GetBytes(
            payloadJson.ToJsonString());

        var success = OrderCreatedV1Serializer.TryDeserialize(
            payload,
            out var message);

        Assert.False(success);
        Assert.Null(message);
    }

    [Fact]
    public void TryDeserialize_NonUtcOccurredAt_ReturnsFalse()
    {
        var payload = Encoding.UTF8.GetBytes(
            """
            {
              "eventVersion": 1,
              "messageId": "0199d28b-79a0-7c31-81be-b783e917d462",
              "orderId": "0199d28b-79a0-7c31-81be-b783e917d463",
              "userId": "0199d28b-79a0-7c31-81be-b783e917d464",
              "correlationId": "0199d28b-79a0-7c31-81be-b783e917d465",
              "occurredAt": "2026-10-16T15:00:00+07:00"
            }
            """);

        var success = OrderCreatedV1Serializer.TryDeserialize(
            payload,
            out var message);

        Assert.False(success);
        Assert.Null(message);
    }

    [Fact]
    public void TryDeserialize_MissingOccurredAt_ReturnsFalse()
    {
        var payload = Encoding.UTF8.GetBytes(
            """
        {
          "eventVersion": 1,
          "messageId": "0199d28b-79a0-7c31-81be-b783e917d462",
          "orderId": "0199d28b-79a0-7c31-81be-b783e917d463",
          "userId": "0199d28b-79a0-7c31-81be-b783e917d464",
          "correlationId": "0199d28b-79a0-7c31-81be-b783e917d465"
        }
        """);

        var success = OrderCreatedV1Serializer.TryDeserialize(
            payload,
            out var message);

        Assert.False(success);
        Assert.Null(message);
    }

    [Fact]
    public void TryDeserialize_MalformedJson_ReturnsFalse()
    {
        var payload = Encoding.UTF8.GetBytes("{not-json");
        var success = true;
        OrderCreatedV1? message = null;

        var exception = Record.Exception(() =>
        {
            success = OrderCreatedV1Serializer.TryDeserialize(payload, out var parsed);
            message = parsed;
        });

        Assert.Null(exception);
        Assert.False(success);
        Assert.Null(message);
    }
}
