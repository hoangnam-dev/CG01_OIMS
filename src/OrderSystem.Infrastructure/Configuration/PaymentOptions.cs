namespace OrderSystem.Infrastructure.Configuration;

public sealed class PaymentOptions
{
    public const string SectionName = "Payment";

    public TimeSpan ReconciliationInterval { get; init; }

    public int ReconciliationBatchSize { get; init; }
}
