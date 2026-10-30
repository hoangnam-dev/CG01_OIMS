namespace OrderSystem.Infrastructure.Messaging;

internal static class OrderCreatedDeadLetterMetadata
{
    public const string OriginalExchangeHeaderName = "x-oims-original-exchange";
    public const string OriginalRoutingKeyHeaderName = "x-oims-original-routing-key";
}
