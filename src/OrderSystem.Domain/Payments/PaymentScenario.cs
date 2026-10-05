namespace OrderSystem.Domain.Payments;

public enum PaymentScenario
{
    Success = 1,
    Failed = 2,
    SuccessButProviderResponseLost = 3,
    DelayedSuccess = 4,
    SuccessButClientResponseLost = 5
}

public static class PaymentScenarioCodes
{
    public const string Success = "SUCCESS";
    public const string Failed = "FAILED";
    public const string SuccessButClientResponseLost = "CLIENT_RESPONSE_LOST";
    public const string SuccessButProviderResponseLost = "PROVIDER_RESPONSE_LOST";
    public const string DelayedSuccess = "DELAYED_SUCCESS";

    public static string ToCode(PaymentScenario scenario) => scenario switch
    {
        PaymentScenario.Success => Success,
        PaymentScenario.Failed => Failed,
        PaymentScenario.SuccessButClientResponseLost => SuccessButClientResponseLost,
        PaymentScenario.SuccessButProviderResponseLost => SuccessButProviderResponseLost,
        PaymentScenario.DelayedSuccess => DelayedSuccess,
        _ => throw new ArgumentOutOfRangeException(
            nameof(scenario),
            scenario,
            "Unsupported payment scenario.")
    };

    public static PaymentScenario Parse(string code) => code switch
    {
        Success => PaymentScenario.Success,
        Failed => PaymentScenario.Failed,
        SuccessButClientResponseLost => PaymentScenario.SuccessButClientResponseLost,
        SuccessButProviderResponseLost => PaymentScenario.SuccessButProviderResponseLost,
        DelayedSuccess => PaymentScenario.DelayedSuccess,
        _ => throw new ArgumentOutOfRangeException(
            nameof(code),
            code,
            "Unsupported payment scenario code.")
    };
}
