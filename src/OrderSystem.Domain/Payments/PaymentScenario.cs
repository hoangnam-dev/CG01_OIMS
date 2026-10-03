namespace OrderSystem.Domain.Payments;

public enum PaymentScenario
{
    Success = 1,
    Failed = 2,
    SuccessButResponseLost = 3,
    DelayedSuccess = 4
}

public static class PaymentScenarioCodes
{
    public const string Success = "SUCCESS";
    public const string Failed = "FAILED";
    public const string SuccessButResponseLost = "SUCCESS_BUT_RESPONSE_LOST";
    public const string DelayedSuccess = "DELAYED_SUCCESS";

    public static string ToCode(PaymentScenario scenario) => scenario switch
    {
        PaymentScenario.Success => Success,
        PaymentScenario.Failed => Failed,
        PaymentScenario.SuccessButResponseLost => SuccessButResponseLost,
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
        SuccessButResponseLost => PaymentScenario.SuccessButResponseLost,
        DelayedSuccess => PaymentScenario.DelayedSuccess,
        _ => throw new ArgumentOutOfRangeException(
            nameof(code),
            code,
            "Unsupported payment scenario code.")
    };
}
