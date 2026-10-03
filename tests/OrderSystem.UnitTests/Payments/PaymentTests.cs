using OrderSystem.Domain.Payments;

namespace OrderSystem.UnitTests.Payments;

public sealed class PaymentTests
{
    private static readonly DateTimeOffset CreatedAt = new(2026, 9, 30, 1, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Constructor_WithValidIntent_InitializesPendingState()
    {
        var paymentId = Guid.NewGuid();
        var orderId = Guid.NewGuid();
        var providerPaymentId = $"fake-pay-{paymentId:D}";
        var gatewayIdempotencyKey = $"gateway-create-{paymentId:D}";

        var payment = new Payment(
            paymentId,
            orderId,
            125_000m,
            "Fake",
            providerPaymentId,
            gatewayIdempotencyKey,
            CreatedAt,
            PaymentScenario.DelayedSuccess);

        Assert.Equal(paymentId, payment.Id);
        Assert.Equal(orderId, payment.OrderId);
        Assert.Equal(PaymentStatus.Pending, payment.Status);
        Assert.Equal(125_000m, payment.Amount);
        Assert.Equal("Fake", payment.Provider);
        Assert.Equal(providerPaymentId, payment.ProviderPaymentId);
        Assert.Equal(gatewayIdempotencyKey, payment.GatewayIdempotencyKey);
        Assert.Equal(PaymentScenario.DelayedSuccess, payment.Scenario);

        Assert.Null(payment.RefundIdempotencyKey);
        Assert.Null(payment.ProviderRefundId);
        Assert.Null(payment.FailureCode);
        Assert.Null(payment.LastStatusCheckedAt);
        Assert.Null(payment.RefundRequestedAt);
        Assert.Equal(0, payment.RefundAttemptCount);
        Assert.Null(payment.NextRefundAttemptAt);
        Assert.Null(payment.ManualReviewRequiredAt);
        Assert.Null(payment.RefundedAt);

        Assert.Equal(CreatedAt, payment.CreatedAt);
        Assert.Equal(CreatedAt, payment.UpdatedAt);
    }

    [Fact]
    public void PaymentStatus_DefinesApprovedLifecycleCatalog()
    {
        string[] expectedStatuses =
        [
            "Pending",
            "Processing",
            "Succeeded",
            "Failed",
            "RefundPending",
            "Refunded"
        ];

        Assert.Equal(expectedStatuses, Enum.GetNames<PaymentStatus>());
    }

    [Fact]
    public void Constructor_WithProviderLongerThan32Characters_ThrowsArgumentException()
    {
        var paymentId = Guid.NewGuid();
        var provider = new string('P', 33);

        var action = () => new Payment(
            paymentId,
            Guid.NewGuid(),
            125_000m,
            provider,
            $"fake-pay-{paymentId:D}",
            $"gateway-create-{paymentId:D}",
            CreatedAt);

        var exception = Assert.Throws<ArgumentException>(action);

        Assert.Equal("provider", exception.ParamName);
    }

    [Fact]
    public void Constructor_WithProviderPaymentIdLongerThan128Characters_ThrowsArgumentException()
    {
        var providerPaymentId = new string('P', 129);

        var action = () => new Payment(
            Guid.NewGuid(),
            Guid.NewGuid(),
            125_000m,
            "Fake",
            providerPaymentId,
            "gateway-create-payment",
            CreatedAt);

        var exception = Assert.Throws<ArgumentException>(action);

        Assert.Equal("providerPaymentId", exception.ParamName);
    }

    [Fact]
    public void Constructor_WithGatewayIdempotencyKeyLongerThan128Characters_ThrowsArgumentException()
    {
        var gatewayIdempotencyKey = new string('K', 129);

        var action = () => new Payment(
            Guid.NewGuid(),
            Guid.NewGuid(),
            125_000m,
            "Fake",
            "fake-pay-payment-id",
            gatewayIdempotencyKey,
            CreatedAt);

        var exception = Assert.Throws<ArgumentException>(action);

        Assert.Equal("gatewayIdempotencyKey", exception.ParamName);
    }

    [Fact]
    public void Constructor_WithBlankProviderPaymentId_ThrowsForProviderPaymentId()
    {
        var action = () => new Payment(
            Guid.NewGuid(),
            Guid.NewGuid(),
            125_000m,
            "Fake",
            "   ",
            "gateway-create-payment",
            CreatedAt);

        var exception = Assert.Throws<ArgumentException>(action);

        Assert.Equal("providerPaymentId", exception.ParamName);
    }

    [Fact]
    public void Constructor_WithEmptyId_ThrowsForId()
    {
        var action = () => new Payment(
            Guid.Empty,
            Guid.NewGuid(),
            125_000m,
            "Fake",
            "fake-pay-payment-id",
            "gateway-create-payment",
            CreatedAt);

        var exception = Assert.Throws<ArgumentException>(action);

        Assert.Equal("id", exception.ParamName);
    }

    [Fact]
    public void Constructor_WithNegativeAmount_ThrowsForAmount()
    {
        var action = () => new Payment(
            Guid.NewGuid(),
            Guid.NewGuid(),
            -0.01m,
            "Fake",
            "fake-pay-payment-id",
            "gateway-create-payment",
            CreatedAt);

        var exception = Assert.Throws<ArgumentOutOfRangeException>(action);

        Assert.Equal("amount", exception.ParamName);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Constructor_WithBlankGatewayIdempotencyKey_ThrowsForGatewayIdempotencyKey(
        string gatewayIdempotencyKey)
    {
        var action = () => new Payment(
            Guid.NewGuid(),
            Guid.NewGuid(),
            125_000m,
            "Fake",
            "fake-pay-payment-id",
            gatewayIdempotencyKey,
            CreatedAt);

        var exception = Assert.Throws<ArgumentException>(action);

        Assert.Equal("gatewayIdempotencyKey", exception.ParamName);
    }

    [Fact]
    public void MarkProcessing_WhenPending_TransitionsToProcessing()
    {
        var payment = CreatePayment();
        var updatedAt = CreatedAt.AddMinutes(1);

        payment.MarkProcessing(updatedAt);

        Assert.Equal(PaymentStatus.Processing, payment.Status);
        Assert.Equal(updatedAt, payment.UpdatedAt);
    }

    [Fact]
    public void RecordStatusCheck_WhenPending_StoresReconciliationEvidenceWithoutChangingState()
    {
        var payment = CreatePayment();
        var checkedAt = CreatedAt.AddMinutes(1);

        payment.RecordStatusCheck(checkedAt);

        Assert.Equal(checkedAt, payment.LastStatusCheckedAt);
        Assert.Equal(PaymentStatus.Pending, payment.Status);
        Assert.Equal(CreatedAt, payment.UpdatedAt);
    }

    [Fact]
    public void RecordStatusCheck_WithTimestampBeforePreviousCheck_ThrowsAndPreservesEvidence()
    {
        var payment = CreatePayment();
        var firstCheckedAt = CreatedAt.AddMinutes(2);
        var regressedCheckedAt = CreatedAt.AddMinutes(1);
        payment.RecordStatusCheck(firstCheckedAt);

        var action = () => payment.RecordStatusCheck(regressedCheckedAt);

        var exception = Assert.Throws<ArgumentOutOfRangeException>(action);

        Assert.Equal("checkedAt", exception.ParamName);
        Assert.Equal(firstCheckedAt, payment.LastStatusCheckedAt);
        Assert.Equal(CreatedAt, payment.UpdatedAt);
    }

    [Fact]
    public void MarkProcessing_WithTimestampBeforeUpdatedAt_ThrowsAndPreservesState()
    {
        var payment = CreatePayment();
        var regressedTimestamp = CreatedAt.AddTicks(-1);

        var action = () => payment.MarkProcessing(regressedTimestamp);

        var exception = Assert.Throws<ArgumentOutOfRangeException>(action);

        Assert.Equal("updatedAt", exception.ParamName);
        Assert.Equal(PaymentStatus.Pending, payment.Status);
        Assert.Equal(CreatedAt, payment.UpdatedAt);
    }

    [Fact]
    public void MarkProcessing_WhenAlreadyProcessing_IsNoOp()
    {
        var payment = CreatePayment();
        var firstProcessedAt = CreatedAt.AddMinutes(1);
        var duplicateReceivedAt = CreatedAt.AddMinutes(2);

        payment.MarkProcessing(firstProcessedAt);
        payment.MarkProcessing(duplicateReceivedAt);

        Assert.Equal(PaymentStatus.Processing, payment.Status);
        Assert.Equal(firstProcessedAt, payment.UpdatedAt);
    }

    [Fact]
    public void MarkSucceeded_WhenPendingAndProviderIdentityMatches_TransitionsToSucceeded()
    {
        var payment = CreatePayment();
        var updatedAt = CreatedAt.AddMinutes(1);

        payment.MarkSucceeded(
            payment.ProviderPaymentId,
            updatedAt);

        Assert.Equal(PaymentStatus.Succeeded, payment.Status);
        Assert.Equal(updatedAt, payment.UpdatedAt);
        Assert.Null(payment.FailureCode);
    }

    [Fact]
    public void MarkSucceeded_WithDifferentProviderPaymentId_ThrowsAndPreservesState()
    {
        var payment = CreatePayment();
        var updatedAt = CreatedAt.AddMinutes(1);

        var action = () => payment.MarkSucceeded(
            "fake-pay-different-payment",
            updatedAt);

        var exception = Assert.Throws<ArgumentException>(action);

        Assert.Equal("providerPaymentId", exception.ParamName);
        Assert.Equal(PaymentStatus.Pending, payment.Status);
        Assert.Equal(CreatedAt, payment.UpdatedAt);
        Assert.Null(payment.FailureCode);
    }

    [Fact]
    public void MarkSucceeded_WhenAlreadySucceeded_IsNoOp()
    {
        var payment = CreatePayment();
        var firstSucceededAt = CreatedAt.AddMinutes(1);
        var duplicateReceivedAt = CreatedAt.AddMinutes(2);

        payment.MarkSucceeded(
            payment.ProviderPaymentId,
            firstSucceededAt);

        payment.MarkSucceeded(
            payment.ProviderPaymentId,
            duplicateReceivedAt);

        Assert.Equal(PaymentStatus.Succeeded, payment.Status);
        Assert.Equal(firstSucceededAt, payment.UpdatedAt);
        Assert.Null(payment.FailureCode);
    }

    [Fact]
    public void MarkProcessing_WhenAlreadySucceeded_IsNoOp()
    {
        var payment = CreatePayment();
        var succeededAt = CreatedAt.AddMinutes(1);
        var staleProcessingReceivedAt = CreatedAt.AddMinutes(2);

        payment.MarkSucceeded(
            payment.ProviderPaymentId,
            succeededAt);

        payment.MarkProcessing(staleProcessingReceivedAt);

        Assert.Equal(PaymentStatus.Succeeded, payment.Status);
        Assert.Equal(succeededAt, payment.UpdatedAt);
        Assert.Null(payment.FailureCode);
    }

    [Fact]
    public void MarkFailed_WhenPendingAndProviderIdentityMatches_TransitionsToFailedWithNormalizedFailureCode()
    {
        var payment = CreatePayment();
        var updatedAt = CreatedAt.AddMinutes(1);

        payment.MarkFailed(
            payment.ProviderPaymentId,
            "  DECLINED  ",
            updatedAt);

        Assert.Equal(PaymentStatus.Failed, payment.Status);
        Assert.Equal("DECLINED", payment.FailureCode);
        Assert.Equal(updatedAt, payment.UpdatedAt);
    }

    [Fact]
    public void MarkSucceeded_WhenAlreadyFailed_IsNoOp()
    {
        var payment = CreatePayment();
        var failedAt = CreatedAt.AddMinutes(1);
        var staleSuccessAt = CreatedAt.AddMinutes(2);

        payment.MarkFailed(
            payment.ProviderPaymentId,
            "DECLINED",
            failedAt);

        payment.MarkSucceeded(
            payment.ProviderPaymentId,
            staleSuccessAt);

        Assert.Equal(PaymentStatus.Failed, payment.Status);
        Assert.Equal("DECLINED", payment.FailureCode);
        Assert.Equal(failedAt, payment.UpdatedAt);
    }

    [Fact]
    public void MarkFailed_WhenProcessingAndProviderIdentityMatches_TransitionsToFailed()
    {
        var payment = CreatePayment();
        var processingAt = CreatedAt.AddMinutes(1);
        var failedAt = CreatedAt.AddMinutes(2);

        payment.MarkProcessing(processingAt);

        payment.MarkFailed(
            payment.ProviderPaymentId,
            "PROVIDER_DECLINED",
            failedAt);

        Assert.Equal(PaymentStatus.Failed, payment.Status);
        Assert.Equal("PROVIDER_DECLINED", payment.FailureCode);
        Assert.Equal(failedAt, payment.UpdatedAt);
    }

    [Fact]
    public void MarkRefundPending_WhenSucceeded_PersistsRefundIntentAndSchedulesImmediateFirstAttempt()
    {
        var payment = CreatePayment();
        var succeededAt = CreatedAt.AddMinutes(1);
        var refundRequestedAt = CreatedAt.AddMinutes(2);
        const string refundIdempotencyKey = "fake-refund-stable-key";

        payment.MarkSucceeded(payment.ProviderPaymentId, succeededAt);

        payment.MarkRefundPending(refundIdempotencyKey, refundRequestedAt);

        Assert.Equal(PaymentStatus.RefundPending, payment.Status);
        Assert.Equal(refundIdempotencyKey, payment.RefundIdempotencyKey);
        Assert.Equal(refundRequestedAt, payment.RefundRequestedAt);
        Assert.Equal(0, payment.RefundAttemptCount);
        Assert.Equal(refundRequestedAt, payment.NextRefundAttemptAt);
        Assert.Null(payment.ProviderRefundId);
        Assert.Null(payment.ManualReviewRequiredAt);
        Assert.Equal(refundRequestedAt, payment.UpdatedAt);
    }

    [Fact]
    public void MarkRefunded_WhenRefundPending_TransitionsToRefundedAndStopsRetrySchedule()
    {
        var payment = CreatePayment();
        var succeededAt = CreatedAt.AddMinutes(1);
        var refundRequestedAt = CreatedAt.AddMinutes(2);
        var refundedAt = CreatedAt.AddMinutes(3);
        const string refundIdempotencyKey = "fake-refund-stable-key";
        const string providerRefundId = "fake-provider-refund-001";

        payment.MarkSucceeded(payment.ProviderPaymentId, succeededAt);
        payment.MarkRefundPending(refundIdempotencyKey, refundRequestedAt);

        payment.MarkRefunded(providerRefundId, refundedAt);

        Assert.Equal(PaymentStatus.Refunded, payment.Status);
        Assert.Equal(refundIdempotencyKey, payment.RefundIdempotencyKey);
        Assert.Equal(providerRefundId, payment.ProviderRefundId);
        Assert.Equal(refundedAt, payment.RefundedAt);
        Assert.Null(payment.NextRefundAttemptAt);
        Assert.Equal(refundedAt, payment.UpdatedAt);
    }

    [Fact]
    public void MarkRefundPending_WhenExistingRefundKeyDiffers_ThrowsAndPreservesOriginalIntent()
    {
        var payment = CreatePayment();
        var succeededAt = CreatedAt.AddMinutes(1);
        var firstRequestedAt = CreatedAt.AddMinutes(2);
        var duplicateRequestedAt = CreatedAt.AddMinutes(3);
        const string originalRefundKey = "fake-refund-original";
        const string differentRefundKey = "fake-refund-different";

        payment.MarkSucceeded(payment.ProviderPaymentId, succeededAt);
        payment.MarkRefundPending(originalRefundKey, firstRequestedAt);

        var action = () => payment.MarkRefundPending(differentRefundKey, duplicateRequestedAt);

        var exception = Assert.Throws<ArgumentException>(action);

        Assert.Equal("refundIdempotencyKey", exception.ParamName);
        Assert.Equal(PaymentStatus.RefundPending, payment.Status);
        Assert.Equal(originalRefundKey, payment.RefundIdempotencyKey);
        Assert.Equal(firstRequestedAt, payment.RefundRequestedAt);
        Assert.Equal(firstRequestedAt, payment.NextRefundAttemptAt);
        Assert.Equal(firstRequestedAt, payment.UpdatedAt);
    }

    [Fact]
    public void MarkRefunded_WhenExistingProviderRefundIdDiffers_ThrowsAndPreservesOriginalResult()
    {
        var payment = CreatePayment();
        var succeededAt = CreatedAt.AddMinutes(1);
        var refundRequestedAt = CreatedAt.AddMinutes(2);
        var firstRefundedAt = CreatedAt.AddMinutes(3);
        var conflictingRefundedAt = CreatedAt.AddMinutes(4);
        const string refundKey = "fake-refund-stable-key";
        const string originalProviderRefundId = "fake-provider-refund-001";
        const string conflictingProviderRefundId = "fake-provider-refund-002";

        payment.MarkSucceeded(payment.ProviderPaymentId, succeededAt);
        payment.MarkRefundPending(refundKey, refundRequestedAt);
        payment.MarkRefunded(originalProviderRefundId, firstRefundedAt);

        var action = () => payment.MarkRefunded(conflictingProviderRefundId, conflictingRefundedAt);

        var exception = Assert.Throws<ArgumentException>(action);

        Assert.Equal("providerRefundId", exception.ParamName);
        Assert.Equal(PaymentStatus.Refunded, payment.Status);
        Assert.Equal(originalProviderRefundId, payment.ProviderRefundId);
        Assert.Equal(firstRefundedAt, payment.RefundedAt);
        Assert.Equal(firstRefundedAt, payment.UpdatedAt);
    }

    [Fact]
    public void MarkRefunded_WhenAlreadyRefundedWithSameProviderRefundId_IsNoOp()
    {
        var payment = CreatePayment();
        var succeededAt = CreatedAt.AddMinutes(1);
        var refundRequestedAt = CreatedAt.AddMinutes(2);
        var firstRefundedAt = CreatedAt.AddMinutes(3);
        var duplicateReceivedAt = CreatedAt.AddMinutes(4);
        const string refundKey = "fake-refund-stable-key";
        const string providerRefundId = "fake-provider-refund-001";

        payment.MarkSucceeded(payment.ProviderPaymentId, succeededAt);
        payment.MarkRefundPending(refundKey, refundRequestedAt);
        payment.MarkRefunded(providerRefundId, firstRefundedAt);

        payment.MarkRefunded(providerRefundId, duplicateReceivedAt);

        Assert.Equal(PaymentStatus.Refunded, payment.Status);
        Assert.Equal(providerRefundId, payment.ProviderRefundId);
        Assert.Equal(firstRefundedAt, payment.RefundedAt);
        Assert.Equal(firstRefundedAt, payment.UpdatedAt);
        Assert.Null(payment.NextRefundAttemptAt);
    }

    [Fact]
    public void MarkFailed_WhenAlreadySucceeded_IsNoOp()
    {
        var payment = CreatePayment();
        var succeededAt = CreatedAt.AddMinutes(1);
        var lateFailureAt = CreatedAt.AddMinutes(2);

        payment.MarkSucceeded(payment.ProviderPaymentId, succeededAt);

        payment.MarkFailed(
            payment.ProviderPaymentId,
            "DECLINED",
            lateFailureAt);

        Assert.Equal(PaymentStatus.Succeeded, payment.Status);
        Assert.Null(payment.FailureCode);
        Assert.Equal(succeededAt, payment.UpdatedAt);
    }

    [Fact]
    public void MarkSucceeded_WithTimestampBeforeUpdatedAt_ThrowsAndPreservesState()
    {
        var payment = CreatePayment();
        var processingAt = CreatedAt.AddMinutes(2);
        var regressedSucceededAt = CreatedAt.AddMinutes(1);

        payment.MarkProcessing(processingAt);

        var action = () => payment.MarkSucceeded(
            payment.ProviderPaymentId,
            regressedSucceededAt);

        var exception = Assert.Throws<ArgumentOutOfRangeException>(action);

        Assert.Equal("updatedAt", exception.ParamName);
        Assert.Equal(PaymentStatus.Processing, payment.Status);
        Assert.Equal(processingAt, payment.UpdatedAt);
    }

    [Fact]
    public void MarkFailed_WithTimestampBeforeUpdatedAt_ThrowsAndPreservesState()
    {
        var payment = CreatePayment();
        var processingAt = CreatedAt.AddMinutes(2);
        var regressedFailedAt = CreatedAt.AddMinutes(1);

        payment.MarkProcessing(processingAt);

        var action = () => payment.MarkFailed(
            payment.ProviderPaymentId,
            "DECLINED",
            regressedFailedAt);

        var exception = Assert.Throws<ArgumentOutOfRangeException>(action);

        Assert.Equal("updatedAt", exception.ParamName);
        Assert.Equal(PaymentStatus.Processing, payment.Status);
        Assert.Null(payment.FailureCode);
        Assert.Equal(processingAt, payment.UpdatedAt);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void MarkFailed_WithBlankFailureCode_ThrowsAndPreservesState(
    string failureCode)
    {
        var payment = CreatePayment();

        var action = () => payment.MarkFailed(
            payment.ProviderPaymentId,
            failureCode,
            CreatedAt.AddMinutes(1));

        var exception = Assert.Throws<ArgumentException>(action);

        Assert.Equal("failureCode", exception.ParamName);
        Assert.Equal(PaymentStatus.Pending, payment.Status);
        Assert.Null(payment.FailureCode);
        Assert.Equal(CreatedAt, payment.UpdatedAt);
    }

    [Fact]
    public void MarkFailed_WithFailureCodeLongerThan64Characters_ThrowsAndPreservesState()
    {
        var payment = CreatePayment();
        var failureCode = new string('F', 65);

        var action = () => payment.MarkFailed(
            payment.ProviderPaymentId,
            failureCode,
            CreatedAt.AddMinutes(1));

        var exception = Assert.Throws<ArgumentException>(action);

        Assert.Equal("failureCode", exception.ParamName);
        Assert.Equal(PaymentStatus.Pending, payment.Status);
        Assert.Null(payment.FailureCode);
        Assert.Equal(CreatedAt, payment.UpdatedAt);
    }

    [Fact]
    public void MarkRefundPending_WhenExistingRefundKeyMatches_IsNoOp()
    {
        var payment = CreatePayment();
        var succeededAt = CreatedAt.AddMinutes(1);
        var firstRequestedAt = CreatedAt.AddMinutes(2);
        var duplicateRequestedAt = CreatedAt.AddMinutes(3);
        const string refundKey = "fake-refund-stable-key";

        payment.MarkSucceeded(payment.ProviderPaymentId, succeededAt);
        payment.MarkRefundPending(refundKey, firstRequestedAt);

        payment.MarkRefundPending(refundKey, duplicateRequestedAt);

        Assert.Equal(PaymentStatus.RefundPending, payment.Status);
        Assert.Equal(refundKey, payment.RefundIdempotencyKey);
        Assert.Equal(firstRequestedAt, payment.RefundRequestedAt);
        Assert.Equal(firstRequestedAt, payment.NextRefundAttemptAt);
        Assert.Equal(firstRequestedAt, payment.UpdatedAt);
    }

    [Fact]
    public void MarkRefunded_WithTimestampBeforeRefundRequestedAt_ThrowsAndPreservesState()
    {
        var payment = CreatePayment();
        var succeededAt = CreatedAt.AddMinutes(1);
        var refundRequestedAt = CreatedAt.AddMinutes(3);
        var invalidRefundedAt = CreatedAt.AddMinutes(2);

        payment.MarkSucceeded(payment.ProviderPaymentId, succeededAt);
        payment.MarkRefundPending("fake-refund-stable-key", refundRequestedAt);

        var action = () => payment.MarkRefunded(
            "fake-provider-refund-001",
            invalidRefundedAt);

        var exception = Assert.Throws<ArgumentOutOfRangeException>(action);

        Assert.Equal("refundedAt", exception.ParamName);
        Assert.Equal(PaymentStatus.RefundPending, payment.Status);
        Assert.Null(payment.ProviderRefundId);
        Assert.Null(payment.RefundedAt);
        Assert.Equal(refundRequestedAt, payment.UpdatedAt);
    }

    private static Payment CreatePayment()
    {
        var paymentId = Guid.NewGuid();
        return new Payment(
            paymentId,
            Guid.NewGuid(),
            125_000m,
            "Fake",
            $"fake-pay-{paymentId:D}",
            $"gateway-create-{paymentId:D}",
            CreatedAt
        );
    }
}
