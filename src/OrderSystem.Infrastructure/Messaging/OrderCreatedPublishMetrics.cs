using System.Diagnostics.Metrics;

namespace OrderSystem.Infrastructure.Messaging;

internal enum OrderCreatedPublishFailureReason
{
    BrokerUnavailable,
    PublishRejected,
    Timeout
}

internal static class OrderCreatedPublishMetrics
{
    public const string MeterName = "OrderSystem.Infrastructure.Messaging";

    public const string FailureCounterName = "oims.order_created.publish_failures";

    private const string ReasonTagName = "reason";

    private static readonly Meter Meter = new(MeterName);

    private static readonly Counter<long> FailureCounter =
        Meter.CreateCounter<long>(
            FailureCounterName,
            unit: "{failure}",
            description: "Number of OrderCreated publication failures after Order commit");

    public static void RecordFailure(
        OrderCreatedPublishFailureReason reason)
    {
        var reasonCode = reason switch
        {
            OrderCreatedPublishFailureReason.BrokerUnavailable => "broker_unavailable",
            OrderCreatedPublishFailureReason.PublishRejected => "publish_rejected",
            OrderCreatedPublishFailureReason.Timeout => "timeout",
            _ => throw new ArgumentOutOfRangeException(
                nameof(reason),
                reason,
                "Unsupported OrderCreated publish failure reason"
            )
        };

        FailureCounter.Add(1, new KeyValuePair<string, object?>(ReasonTagName, reasonCode));
    }
}