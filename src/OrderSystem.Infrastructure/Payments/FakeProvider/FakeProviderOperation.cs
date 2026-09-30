namespace OrderSystem.Infrastructure.Payments.FakeProvider;

internal sealed class FakeProviderOperation
{
    private const int MaximumProviderIdentifierLength = 128;

    private FakeProviderOperation()
    {
    }

    internal FakeProviderOperation(
        Guid id,
        string operationType,
        string idempotencyKey,
        string providerResourceId,
        string? parentProviderPaymentId,
        string scenario,
        string status,
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
        var normalizedOperationType = RequireOperationType(operationType);
        var normalizedParentProviderPaymentId = parentProviderPaymentId is null
            ? null
            : RequireBoundedText(parentProviderPaymentId, nameof(parentProviderPaymentId), MaximumProviderIdentifierLength, "Parent provider payment ID");
        if (normalizedOperationType == "CreatePayment" && normalizedParentProviderPaymentId is not null)
        {
            throw new ArgumentException("Create operations cannot have a parent provider payment ID.", nameof(parentProviderPaymentId));
        }
        if (normalizedOperationType == "RefundPayment" && string.IsNullOrWhiteSpace(normalizedParentProviderPaymentId))
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
        OperationType = normalizedOperationType;
        IdempotencyKey = RequireBoundedText(idempotencyKey, nameof(idempotencyKey), MaximumProviderIdentifierLength, "Idempotency key");
        ProviderResourceId = RequireBoundedText(providerResourceId, nameof(providerResourceId), MaximumProviderIdentifierLength, "Provider resource ID"); ;
        ParentProviderPaymentId = normalizedParentProviderPaymentId;
        Scenario = RequireScenario(scenario);
        Status = RequireStatus(status);
        Amount = amount;
        AvailableAt = availableAt;
        CreatedAt = createdAt;
        UpdatedAt = updatedAt;
    }

    public Guid Id { get; private set; }
    public string OperationType { get; private set; } = string.Empty;
    public string IdempotencyKey { get; private set; } = string.Empty;
    public string ProviderResourceId { get; private set; } = string.Empty;
    public string? ParentProviderPaymentId { get; private set; }
    public string Scenario { get; private set; } = string.Empty;
    public string Status { get; private set; } = string.Empty;
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

    private static string RequireOperationType(string operationType)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operationType);

        var normalizedOperationType = operationType.Trim();

        if (normalizedOperationType is not "CreatePayment" and not "RefundPayment")
        {
            throw new ArgumentOutOfRangeException(nameof(operationType), normalizedOperationType, "Unsupported fake provider operation type.");
        }

        return normalizedOperationType;
    }

    private static string RequireScenario(string scenario)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scenario);

        var normalizedScenario = scenario.Trim();

        if (normalizedScenario is not "SUCCESS"
            and not "FAILED"
            and not "SUCCESS_BUT_RESPONSE_LOST"
            and not "DELAYED_SUCCESS"
        )
        {
            throw new ArgumentOutOfRangeException(nameof(scenario), normalizedScenario, "Unsupported fake provider scenario.");
        }

        return normalizedScenario;
    }

    private static string RequireStatus(string status)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(status);

        var normalizedStatus = status.Trim();

        if (normalizedStatus is not "Pending"
            and not "Processing"
            and not "Succeeded"
            and not "Failed"
        )
        {
            throw new ArgumentOutOfRangeException(nameof(status), normalizedStatus, "Unsupported fake provider status.");
        }

        return normalizedStatus;
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
        if (Status is not "Pending" and not "Processing")
        {
            return;
        }

        EnsureTimestampDoesNotRegress(updatedAt);

        Status = "Succeeded";
        UpdatedAt = updatedAt;
    }

    internal void MarkFailed(DateTimeOffset updatedAt)
    {
        if (Status is not "Pending" and not "Processing")
        {
            return;
        }

        EnsureTimestampDoesNotRegress(updatedAt);

        Status = "Failed";
        UpdatedAt = updatedAt;
    }

    internal void MarkProcessing(DateTimeOffset updatedAt)
    {
        if (Status != "Pending")
        {
            return;
        }

        EnsureTimestampDoesNotRegress(updatedAt);

        Status = "Processing";
        UpdatedAt = updatedAt;
    }

    internal bool IsAvailable(DateTimeOffset now) => AvailableAt is null || now >= AvailableAt.Value;
}