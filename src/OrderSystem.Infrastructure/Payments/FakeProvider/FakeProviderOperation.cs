using OrderSystem.Application.Payments.Contracts;

namespace OrderSystem.Infrastructure.Payments.FakeProvider;

internal sealed class FakeProviderOperation
{
    private const int MaximumProviderIdentifierLength = 128;

    private FakeProviderOperation()
    {
    }

    internal FakeProviderOperation(
        Guid id,
        FakeProviderOperationType operationType,
        string idempotencyKey,
        string providerResourceId,
        string? parentProviderPaymentId,
        PaymentScenario scenario,
        FakeProviderOperationStatus status,
        decimal amount,
        DateTimeOffset? availableAt,
        DateTimeOffset createdAt,
        DateTimeOffset updatedAt
    )
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("Provider operation ID cannot be empty.", nameof(id));
        }
        if (!Enum.IsDefined(operationType))
        {
            throw new ArgumentOutOfRangeException(nameof(operationType), operationType, "Unsupported fake provider operation type.");
        }
        if (!Enum.IsDefined(scenario))
        {
            throw new ArgumentOutOfRangeException(nameof(scenario), scenario, "Unsupported fake provider scenario.");
        }
        if (!Enum.IsDefined(status))
        {
            throw new ArgumentOutOfRangeException(nameof(status), status, "Unsupported fake provider status.");
        }
        var normalizedParentProviderPaymentId = parentProviderPaymentId is null
            ? null
            : RequireBoundedText(parentProviderPaymentId, nameof(parentProviderPaymentId), MaximumProviderIdentifierLength, "Parent provider payment ID");
        if (operationType == FakeProviderOperationType.CreatePayment && normalizedParentProviderPaymentId is not null)
        {
            throw new ArgumentException("Create operations cannot have a parent provider payment ID.", nameof(parentProviderPaymentId));
        }
        if (operationType == FakeProviderOperationType.RefundPayment && string.IsNullOrWhiteSpace(normalizedParentProviderPaymentId))
        {
            throw new ArgumentException("Refund operations require a parent provider payment ID.", nameof(parentProviderPaymentId));
        }
        ArgumentOutOfRangeException.ThrowIfNegative(amount);

        if (updatedAt < createdAt)
        {
            throw new ArgumentOutOfRangeException(nameof(updatedAt), "Provider operation update time cannot be earlier than creation time.");
        }
        if (availableAt < createdAt)
        {
            throw new ArgumentOutOfRangeException(nameof(availableAt), "Provider operation availability time cannot be earlier than creation time.");
        }

        Id = id;
        OperationType = operationType;
        IdempotencyKey = RequireBoundedText(idempotencyKey, nameof(idempotencyKey), MaximumProviderIdentifierLength, "Idempotency key");
        ProviderResourceId = RequireBoundedText(providerResourceId, nameof(providerResourceId), MaximumProviderIdentifierLength, "Provider resource ID"); ;
        ParentProviderPaymentId = normalizedParentProviderPaymentId;
        Scenario = scenario;
        Status = status;
        Amount = amount;
        AvailableAt = availableAt;
        CreatedAt = createdAt;
        UpdatedAt = updatedAt;
    }

    public Guid Id { get; private set; }
    public FakeProviderOperationType OperationType { get; private set; }
    public string IdempotencyKey { get; private set; } = string.Empty;
    public string ProviderResourceId { get; private set; } = string.Empty;
    public string? ParentProviderPaymentId { get; private set; }
    public PaymentScenario Scenario { get; private set; }
    public FakeProviderOperationStatus Status { get; private set; }
    public decimal Amount { get; private set; }
    public DateTimeOffset? AvailableAt { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    private static string RequireBoundedText(string? value, string paramName, int maximumLength, string fieldName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, paramName);

        var normalizedValue = value.Trim();

        if (normalizedValue.Length > maximumLength)
        {
            throw new ArgumentException($"{fieldName} cannot exceed {maximumLength} characters.", paramName);
        }

        return normalizedValue;
    }

    private void EnsureTimestampDoesNotRegress(DateTimeOffset updatedAt)
    {
        if (updatedAt < UpdatedAt)
        {
            throw new ArgumentOutOfRangeException(nameof(updatedAt), "Provider operation transition timestamp cannot be earlier than the last update.");
        }
    }

    internal void MarkSucceeded(DateTimeOffset updatedAt)
    {
        if (Status is not FakeProviderOperationStatus.Pending and not FakeProviderOperationStatus.Processing)
        {
            return;
        }

        EnsureTimestampDoesNotRegress(updatedAt);

        Status = FakeProviderOperationStatus.Succeeded;
        UpdatedAt = updatedAt;
    }

    internal void MarkFailed(DateTimeOffset updatedAt)
    {
        if (Status is not FakeProviderOperationStatus.Pending and not FakeProviderOperationStatus.Processing)
        {
            return;
        }

        EnsureTimestampDoesNotRegress(updatedAt);

        Status = FakeProviderOperationStatus.Failed;
        UpdatedAt = updatedAt;
    }

    internal void MarkProcessing(DateTimeOffset updatedAt)
    {
        if (Status != FakeProviderOperationStatus.Pending)
        {
            return;
        }

        EnsureTimestampDoesNotRegress(updatedAt);

        Status = FakeProviderOperationStatus.Processing;
        UpdatedAt = updatedAt;
    }

    internal bool IsAvailable(DateTimeOffset now) => AvailableAt is null || now >= AvailableAt.Value;
}
