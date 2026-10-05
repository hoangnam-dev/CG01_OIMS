using OrderSystem.Domain.Payments;

namespace OrderSystem.Infrastructure.Payments.FakeProvider;

internal static class FakeProviderScenarioPolicy
{
    public const string DeclinedFailureCode = "DECLINED";
    public static readonly TimeSpan DelayedSuccessDuration = TimeSpan.FromMinutes(1);
    public static FakeProviderOperationStatus GetInitialStatus(PaymentScenario scenario) =>
    scenario switch
    {
        PaymentScenario.Success => FakeProviderOperationStatus.Succeeded,
        PaymentScenario.Failed => FakeProviderOperationStatus.Failed,
        PaymentScenario.SuccessButClientResponseLost => FakeProviderOperationStatus.Succeeded,
        PaymentScenario.SuccessButProviderResponseLost => FakeProviderOperationStatus.Succeeded,
        PaymentScenario.DelayedSuccess => FakeProviderOperationStatus.Processing,
        _ => throw new ArgumentOutOfRangeException(nameof(scenario), scenario, "Unsupported payment scenario.")
    };

    public static string? GetFailureCode(PaymentScenario scenario) =>
    scenario switch
    {
        PaymentScenario.Success => null,
        PaymentScenario.Failed => DeclinedFailureCode,
        PaymentScenario.SuccessButClientResponseLost or
        PaymentScenario.SuccessButProviderResponseLost or
        PaymentScenario.DelayedSuccess => throw new NotSupportedException($"Payment scenario '{scenario}' is not implemented yet."),
        _ => throw new ArgumentOutOfRangeException(nameof(scenario), scenario, "Unsupported payment scenario.")
    };

    public static DateTimeOffset? GetAvailableAt(PaymentScenario scenario, DateTimeOffset now) =>
        scenario switch
        {
            PaymentScenario.DelayedSuccess => now.Add(DelayedSuccessDuration),
            PaymentScenario.Success or
            PaymentScenario.Failed or
            PaymentScenario.SuccessButClientResponseLost or
            PaymentScenario.SuccessButProviderResponseLost
            => null,
            _ => throw new ArgumentOutOfRangeException(nameof(scenario), scenario, "Unsupported payment scenario.")
        };
}