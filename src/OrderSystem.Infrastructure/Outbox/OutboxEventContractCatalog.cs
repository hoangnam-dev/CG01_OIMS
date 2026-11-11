namespace OrderSystem.Infrastructure.Outbox;

internal static class OutboxEventContractCatalog
{
    private static readonly HashSet<(string EventType, short EventVersion)> SupportedContracts =
    [
        ("OrderCreated", 1)
    ];

    public static bool IsSupported(string eventType, short eventVersion) => SupportedContracts.Contains((eventType, eventVersion));
}
