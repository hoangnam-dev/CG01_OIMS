namespace OrderSystem.Application.Payments;

public static class PaymentOperationCheckpoints
{
    public const string BeforeIdempotencyClaim = "payments.initiate.before-idempotency-claim";
    public const string AfterLocalCommit = "payments.initiate.after-local-commit";
    public const string AfterGatewayCreate = "payments.initiate.after-gateway-create";
    public const string AfterResultApplication = "payments.initiate.after-result-application";
}
