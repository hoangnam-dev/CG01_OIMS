using System.Text.Json;
using OrderSystem.Application.Messaging.Contracts;

namespace OrderSystem.Infrastructure.Messaging;

internal static class OrderCreatedV1Serializer
{
    private const int SupportedEventVersion = 1;

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        WriteIndented = false,
        RespectRequiredConstructorParameters = true
    };

    public static byte[] Serialize(OrderCreatedV1 message)
    {
        ArgumentNullException.ThrowIfNull(message);

        return JsonSerializer.SerializeToUtf8Bytes(message, SerializerOptions);
    }

    public static bool TryDeserialize(ReadOnlySpan<byte> payload, out OrderCreatedV1? message)
    {
        try
        {
            message = JsonSerializer.Deserialize<OrderCreatedV1>(payload, SerializerOptions);
            if (message is null ||
                message.EventVersion != SupportedEventVersion ||
                message.MessageId == Guid.Empty ||
                message.UserId == Guid.Empty ||
                message.OrderId == Guid.Empty ||
                message.CorrelationId == Guid.Empty ||
                message.OccurredAt.Offset != TimeSpan.Zero
            )
            {
                message = null;
                return false;
            }

            return true;
        }
        catch (JsonException)
        {
            message = null;
            return false;
        }
    }
}