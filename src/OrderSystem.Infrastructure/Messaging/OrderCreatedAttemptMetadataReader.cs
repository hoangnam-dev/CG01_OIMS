using System.Text;
using OrderSystem.Infrastructure.Configuration;

namespace OrderSystem.Infrastructure.Messaging;

internal static class OrderCreatedAttemptMetadataReader
{
    internal const string AttemptHeaderName = "x-oims-attempt";
    internal const string DeathHeaderName = "x-death";

    public static int Read(
        string exchange,
        string routingKey,
        IDictionary<string, object?>? headers,
        RabbitMqOptions options
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(exchange);
        ArgumentException.ThrowIfNullOrWhiteSpace(routingKey);
        ArgumentNullException.ThrowIfNull(options);

        if (string.Equals(exchange, options.ExchangeName, StringComparison.Ordinal) &&
            string.Equals(routingKey, options.OrderCreatedRoutingKey, StringComparison.Ordinal)
        )
        {
            return 1;
        }

        if (string.Equals(exchange, options.ConsumerIngressExchangeName, StringComparison.Ordinal) &&
            string.Equals(routingKey, options.OrderCreatedConsumerRoutingKey, StringComparison.Ordinal)
        )
        {
            if (headers is null ||
                (!headers.ContainsKey(AttemptHeaderName) && !headers.ContainsKey(DeathHeaderName))
            )
            {
                return 1;
            }
            return ReadRetryAttempt(headers, options);
        }

        throw new InvalidDataException("OrderCreated attempt metadata route is invalid");
    }

    private static int ReadRetryAttempt(
        IDictionary<string, object?>? headers,
        RabbitMqOptions options
    )
    {
        if (headers is null ||
            !headers.TryGetValue(AttemptHeaderName, out var attemptValue) ||
            attemptValue is not int attempt ||
            attempt is not (2 or 3) ||
            !headers.TryGetValue(DeathHeaderName, out var deathValue) ||
            deathValue is not IReadOnlyList<object?> deaths ||
            deaths.Count == 0 ||
            deaths[0] is not IDictionary<string, object?> latestDeath ||
            !HasUtf8Value(latestDeath, "queue", options.OrderCreatedRetryQueueName) ||
            !HasUtf8Value(latestDeath, "reason", "expired")
        )
        {
            throw new InvalidDataException("OrderCreated retry attempt metadata is invalid");
        }

        return attempt;
    }

    private static bool HasUtf8Value(
        IDictionary<string, object?> values,
        string key,
        string expected
    )
    {
        return values.TryGetValue(key, out var value) &&
            value is byte[] bytes &&
            string.Equals(Encoding.UTF8.GetString(bytes), expected, StringComparison.Ordinal
        );
    }
}