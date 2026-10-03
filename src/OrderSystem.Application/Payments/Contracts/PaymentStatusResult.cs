namespace OrderSystem.Application.Payments.Contracts;

public enum PaymentStatusQueryOutcome
{
    Found = 1,
    NotFound = 2
}
public sealed record PaymentStatusResult
{
    private PaymentStatusResult(
        PaymentStatusQueryOutcome outcome,
        string? providerPaymentId,
        PaymentGatewayStatus? status,
        string? failureCode
    )
    {
        Outcome = outcome;
        ProviderPaymentId = providerPaymentId;
        Status = status;
        FailureCode = failureCode;
    }

    public PaymentStatusQueryOutcome Outcome { get; }
    public string? ProviderPaymentId { get; }
    public PaymentGatewayStatus? Status { get; }
    public string? FailureCode { get; }

    public static PaymentStatusResult Found(
        string providerPaymentId,
        PaymentGatewayStatus status,
        string? failureCode = null
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerPaymentId);
        return new PaymentStatusResult(
            PaymentStatusQueryOutcome.Found,
            providerPaymentId,
            status,
            failureCode
        );
    }

    public static PaymentStatusResult NotFound() =>
        new PaymentStatusResult(
            PaymentStatusQueryOutcome.NotFound,
            providerPaymentId: null,
            status: null,
            failureCode: null
        );
}
