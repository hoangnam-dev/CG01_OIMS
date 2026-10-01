using OrderSystem.Application.Payments.Contracts;
using OrderSystem.Infrastructure.Payments.FakeProvider;

namespace OrderSystem.IntegrationTests.Payments;

public sealed class FakeProviderOperationTests
{
    [Fact]
    public void Constructor_WhenRefundHasNoParentProviderPaymentId_Throws()
    {
        var now = DateTimeOffset.UtcNow;

        var action = () => new FakeProviderOperation(
            Guid.NewGuid(),
            FakeProviderOperationType.RefundPayment,
            "fake-refund-key",
            "fake-refund-001",
            parentProviderPaymentId: null,
            PaymentScenario.Success,
            FakeProviderOperationStatus.Pending,
            10m,
            availableAt: null,
            now,
            now);

        var exception = Assert.Throws<ArgumentException>(action);

        Assert.Equal("parentProviderPaymentId", exception.ParamName);
    }

    [Fact]
    public void Constructor_WhenCreatePaymentHasParentProviderPaymentId_Throws()
    {
        var now = DateTimeOffset.UtcNow;

        var action = () => new FakeProviderOperation(
            Guid.NewGuid(),
            FakeProviderOperationType.CreatePayment,
            "fake-pay-key",
            "fake-pay-001",
            parentProviderPaymentId: "fake-pay-parent",
            PaymentScenario.Success,
            FakeProviderOperationStatus.Pending,
            10m,
            availableAt: null,
            now,
            now);

        var exception = Assert.Throws<ArgumentException>(action);

        Assert.Equal("parentProviderPaymentId", exception.ParamName);
    }

    [Fact]
    public void Constructor_WhenOperationTypeIsUnknown_Throws()
    {
        var now = DateTimeOffset.UtcNow;

        var action = () => new FakeProviderOperation(
            Guid.NewGuid(),
            (FakeProviderOperationType)999,
            "fake-key",
            "fake-resource-001",
            parentProviderPaymentId: null,
            PaymentScenario.Success,
            FakeProviderOperationStatus.Pending,
            10m,
            availableAt: null,
            now,
            now);

        var exception = Assert.Throws<ArgumentOutOfRangeException>(action);

        Assert.Equal("operationType", exception.ParamName);
    }

    [Fact]
    public void Constructor_WhenScenarioIsUnknown_Throws()
    {
        var now = DateTimeOffset.UtcNow;

        var action = () => new FakeProviderOperation(
            Guid.NewGuid(),
            FakeProviderOperationType.CreatePayment,
            "fake-pay-key",
            "fake-pay-001",
            parentProviderPaymentId: null,
            (PaymentScenario)999,
            FakeProviderOperationStatus.Pending,
            10m,
            availableAt: null,
            now,
            now);

        var exception = Assert.Throws<ArgumentOutOfRangeException>(action);

        Assert.Equal("scenario", exception.ParamName);
    }

    [Fact]
    public void Constructor_WhenStatusIsUnknown_Throws()
    {
        var now = DateTimeOffset.UtcNow;

        var action = () => new FakeProviderOperation(
            Guid.NewGuid(),
            FakeProviderOperationType.CreatePayment,
            "fake-pay-key",
            "fake-pay-001",
            parentProviderPaymentId: null,
            PaymentScenario.Success,
            (FakeProviderOperationStatus)999,
            10m,
            availableAt: null,
            now,
            now);

        var exception = Assert.Throws<ArgumentOutOfRangeException>(action);

        Assert.Equal("status", exception.ParamName);
    }

    [Fact]
    public void Constructor_WhenAmountIsNegative_Throws()
    {
        var now = DateTimeOffset.UtcNow;

        var action = () => new FakeProviderOperation(
            Guid.NewGuid(),
            FakeProviderOperationType.CreatePayment,
            "fake-pay-key",
            "fake-pay-001",
            parentProviderPaymentId: null,
            PaymentScenario.Success,
            FakeProviderOperationStatus.Pending,
            -0.01m,
            availableAt: null,
            now,
            now);

        var exception = Assert.Throws<ArgumentOutOfRangeException>(action);

        Assert.Equal("amount", exception.ParamName);
    }

    [Fact]
    public void Constructor_WhenUpdatedAtIsBeforeCreatedAt_Throws()
    {
        var createdAt = DateTimeOffset.UtcNow;
        var updatedAt = createdAt.AddSeconds(-1);

        var action = () => new FakeProviderOperation(
            Guid.NewGuid(),
            FakeProviderOperationType.CreatePayment,
            "fake-pay-key",
            "fake-pay-001",
            parentProviderPaymentId: null,
            PaymentScenario.Success,
            FakeProviderOperationStatus.Pending,
            10m,
            availableAt: null,
            createdAt,
            updatedAt);

        var exception = Assert.Throws<ArgumentOutOfRangeException>(action);

        Assert.Equal("updatedAt", exception.ParamName);
    }

    [Fact]
    public void Constructor_WhenAvailableAtIsBeforeCreatedAt_Throws()
    {
        var createdAt = DateTimeOffset.UtcNow;
        var availableAt = createdAt.AddSeconds(-1);

        var action = () => new FakeProviderOperation(
            Guid.NewGuid(),
            FakeProviderOperationType.CreatePayment,
            "fake-pay-key",
            "fake-pay-001",
            parentProviderPaymentId: null,
            PaymentScenario.DelayedSuccess,
            FakeProviderOperationStatus.Pending,
            10m,
            availableAt,
            createdAt,
            createdAt);

        var exception = Assert.Throws<ArgumentOutOfRangeException>(action);

        Assert.Equal("availableAt", exception.ParamName);
    }

    [Fact]
    public void Constructor_WhenIdempotencyKeyIsBlank_Throws()
    {
        var now = DateTimeOffset.UtcNow;

        var action = () => new FakeProviderOperation(
            Guid.NewGuid(),
            FakeProviderOperationType.CreatePayment,
            "   ",
            "fake-pay-001",
            parentProviderPaymentId: null,
            PaymentScenario.Success,
            FakeProviderOperationStatus.Pending,
            10m,
            availableAt: null,
            now,
            now);

        var exception = Assert.Throws<ArgumentException>(action);

        Assert.Equal("idempotencyKey", exception.ParamName);
    }

    [Fact]
    public void Constructor_WhenProviderResourceIdExceedsMaximumLength_Throws()
    {
        var now = DateTimeOffset.UtcNow;

        var action = () => new FakeProviderOperation(
            Guid.NewGuid(),
            FakeProviderOperationType.CreatePayment,
            "fake-pay-key",
            new string('x', 129),
            parentProviderPaymentId: null,
            PaymentScenario.Success,
            FakeProviderOperationStatus.Pending,
            10m,
            availableAt: null,
            now,
            now);

        var exception = Assert.Throws<ArgumentException>(action);

        Assert.Equal("providerResourceId", exception.ParamName);
    }

    [Fact]
    public void Constructor_WhenRefundParentProviderPaymentIdExceedsMaximumLength_Throws()
    {
        var now = DateTimeOffset.UtcNow;

        var action = () => new FakeProviderOperation(
            Guid.NewGuid(),
            FakeProviderOperationType.RefundPayment,
            "fake-refund-key",
            "fake-refund-001",
            new string('x', 129),
            PaymentScenario.Success,
            FakeProviderOperationStatus.Pending,
            10m,
            availableAt: null,
            now,
            now);

        var exception = Assert.Throws<ArgumentException>(action);

        Assert.Equal("parentProviderPaymentId", exception.ParamName);
    }

    [Fact]
    public void Constructor_WhenIdIsEmpty_Throws()
    {
        var now = DateTimeOffset.UtcNow;

        var action = () => new FakeProviderOperation(
            Guid.Empty,
            FakeProviderOperationType.CreatePayment,
            "fake-pay-key",
            "fake-pay-001",
            parentProviderPaymentId: null,
            PaymentScenario.Success,
            FakeProviderOperationStatus.Pending,
            10m,
            availableAt: null,
            now,
            now);

        var exception = Assert.Throws<ArgumentException>(action);

        Assert.Equal("id", exception.ParamName);
    }

    [Fact]
    public void MarkSucceeded_WhenPending_TransitionsToSucceeded()
    {
        var createdAt = DateTimeOffset.UtcNow;
        var succeededAt = createdAt.AddMinutes(1);

        var operation = new FakeProviderOperation(
            Guid.NewGuid(),
            FakeProviderOperationType.CreatePayment,
            "fake-pay-key",
            "fake-pay-001",
            parentProviderPaymentId: null,
            PaymentScenario.Success,
            FakeProviderOperationStatus.Pending,
            10m,
            availableAt: null,
            createdAt,
            createdAt);

        operation.MarkSucceeded(succeededAt);

        Assert.Equal(FakeProviderOperationStatus.Succeeded, operation.Status);
        Assert.Equal(succeededAt, operation.UpdatedAt);
    }

    [Fact]
    public void MarkFailed_WhenPending_TransitionsToFailed()
    {
        var createdAt = DateTimeOffset.UtcNow;
        var failedAt = createdAt.AddMinutes(1);

        var operation = new FakeProviderOperation(
            Guid.NewGuid(),
            FakeProviderOperationType.CreatePayment,
            "fake-pay-key",
            "fake-pay-001",
            parentProviderPaymentId: null,
            PaymentScenario.Failed,
            FakeProviderOperationStatus.Pending,
            10m,
            availableAt: null,
            createdAt,
            createdAt);

        operation.MarkFailed(failedAt);

        Assert.Equal(FakeProviderOperationStatus.Failed, operation.Status);
        Assert.Equal(failedAt, operation.UpdatedAt);
    }

    [Fact]
    public void MarkProcessing_WhenPending_TransitionsToProcessing()
    {
        var createdAt = DateTimeOffset.UtcNow;
        var processingAt = createdAt.AddMinutes(1);

        var operation = new FakeProviderOperation(
            Guid.NewGuid(),
            FakeProviderOperationType.CreatePayment,
            "fake-pay-key",
            "fake-pay-001",
            parentProviderPaymentId: null,
            PaymentScenario.DelayedSuccess,
            FakeProviderOperationStatus.Pending,
            10m,
            availableAt: createdAt.AddMinutes(5),
            createdAt,
            createdAt);

        operation.MarkProcessing(processingAt);

        Assert.Equal(FakeProviderOperationStatus.Processing, operation.Status);
        Assert.Equal(processingAt, operation.UpdatedAt);
    }

    [Fact]
    public void MarkFailed_WhenAlreadySucceeded_IsNoOp()
    {
        var createdAt = DateTimeOffset.UtcNow;
        var succeededAt = createdAt.AddMinutes(1);
        var lateFailedAt = createdAt.AddMinutes(2);

        var operation = new FakeProviderOperation(
            Guid.NewGuid(),
            FakeProviderOperationType.CreatePayment,
            "fake-pay-key",
            "fake-pay-001",
            parentProviderPaymentId: null,
            PaymentScenario.Success,
            FakeProviderOperationStatus.Pending,
            10m,
            availableAt: null,
            createdAt,
            createdAt);

        operation.MarkSucceeded(succeededAt);
        operation.MarkFailed(lateFailedAt);

        Assert.Equal(FakeProviderOperationStatus.Succeeded, operation.Status);
        Assert.Equal(succeededAt, operation.UpdatedAt);
    }

    [Fact]
    public void MarkSucceeded_WhenAlreadyFailed_IsNoOp()
    {
        var createdAt = DateTimeOffset.UtcNow;
        var failedAt = createdAt.AddMinutes(1);
        var lateSucceededAt = createdAt.AddMinutes(2);

        var operation = new FakeProviderOperation(
            Guid.NewGuid(),
            FakeProviderOperationType.CreatePayment,
            "fake-pay-key",
            "fake-pay-001",
            parentProviderPaymentId: null,
            PaymentScenario.Failed,
            FakeProviderOperationStatus.Pending,
            10m,
            availableAt: null,
            createdAt,
            createdAt);

        operation.MarkFailed(failedAt);
        operation.MarkSucceeded(lateSucceededAt);

        Assert.Equal(FakeProviderOperationStatus.Failed, operation.Status);
        Assert.Equal(failedAt, operation.UpdatedAt);
    }

    [Fact]
    public void MarkSucceeded_WhenProcessing_TransitionsToSucceeded()
    {
        var createdAt = DateTimeOffset.UtcNow;
        var processingAt = createdAt.AddMinutes(1);
        var succeededAt = createdAt.AddMinutes(2);

        var operation = new FakeProviderOperation(
            Guid.NewGuid(),
            FakeProviderOperationType.CreatePayment,
            "fake-pay-key",
            "fake-pay-001",
            parentProviderPaymentId: null,
            PaymentScenario.DelayedSuccess,
            FakeProviderOperationStatus.Pending,
            10m,
            availableAt: createdAt.AddMinutes(2),
            createdAt,
            createdAt);

        operation.MarkProcessing(processingAt);
        operation.MarkSucceeded(succeededAt);

        Assert.Equal(FakeProviderOperationStatus.Succeeded, operation.Status);
        Assert.Equal(succeededAt, operation.UpdatedAt);
    }

    [Fact]
    public void IsAvailable_WhenBeforeAvailableAt_ReturnsFalse()
    {
        var createdAt = DateTimeOffset.UtcNow;
        var availableAt = createdAt.AddMinutes(5);

        var operation = new FakeProviderOperation(
            Guid.NewGuid(),
            FakeProviderOperationType.CreatePayment,
            "fake-pay-key",
            "fake-pay-001",
            parentProviderPaymentId: null,
            PaymentScenario.DelayedSuccess,
            FakeProviderOperationStatus.Pending,
            10m,
            availableAt,
            createdAt,
            createdAt);

        var isAvailable = operation.IsAvailable(createdAt.AddMinutes(4));

        Assert.False(isAvailable);
    }

    [Fact]
    public void IsAvailable_WhenAtAvailableAt_ReturnsTrue()
    {
        var createdAt = DateTimeOffset.UtcNow;
        var availableAt = createdAt.AddMinutes(5);

        var operation = new FakeProviderOperation(
            Guid.NewGuid(),
            FakeProviderOperationType.CreatePayment,
            "fake-pay-key",
            "fake-pay-001",
            parentProviderPaymentId: null,
            PaymentScenario.DelayedSuccess,
            FakeProviderOperationStatus.Pending,
            10m,
            availableAt,
            createdAt,
            createdAt);

        var isAvailable = operation.IsAvailable(availableAt);

        Assert.True(isAvailable);
    }

    [Fact]
    public void IsAvailable_WhenAvailableAtIsNull_ReturnsTrue()
    {
        var createdAt = DateTimeOffset.UtcNow;

        var operation = new FakeProviderOperation(
            Guid.NewGuid(),
            FakeProviderOperationType.CreatePayment,
            "fake-pay-key",
            "fake-pay-001",
            parentProviderPaymentId: null,
            PaymentScenario.Success,
            FakeProviderOperationStatus.Pending,
            10m,
            availableAt: null,
            createdAt,
            createdAt);

        var isAvailable = operation.IsAvailable(createdAt);

        Assert.True(isAvailable);
    }


}