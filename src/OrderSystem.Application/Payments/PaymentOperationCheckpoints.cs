namespace OrderSystem.Application.Payments;

public static class PaymentOperationCheckpoints
{
    public const string BeforeIdempotencyClaim = "payments.initiate.before-idempotency-claim";
    public const string AfterGatewayCreate = "payments.initiate.after-gateway-create";
}