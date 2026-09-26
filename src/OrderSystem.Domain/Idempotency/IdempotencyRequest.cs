using System.Text;
using OrderSystem.Domain.Common;

namespace OrderSystem.Domain.Idempotency;

public sealed class IdempotencyRequest
{
    public const int RequestHashLength = 32;
    public const int MaximumResponseBodyBytes = 65_536;
    public const short CreateOrderCompletedStatusCode = 201;
    private IdempotencyRequest()
    {
    }

    public IdempotencyRequest(
      Guid id,
      Guid userId,
      IdempotencyOperation operation,
      Guid idempotencyKey,
      byte[] requestHash,
      DateTimeOffset createdAt,
      DateTimeOffset expiresAt,
      DateTimeOffset deleteAfter
    )
    {
        Id = DomainGuard.RequiredGuid(id);
        UserId = DomainGuard.RequiredGuid(userId);
        Operation = DomainGuard.DefinedEnum(operation);
        IdempotencyKey = DomainGuard.RequiredGuid(idempotencyKey);

        ArgumentNullException.ThrowIfNull(requestHash);

        if (requestHash.Length != RequestHashLength)
        {
            throw new ArgumentException("Request hash must contain exactly 32 bytes.", nameof(requestHash));
        }
        if (expiresAt <= createdAt)
        {
            throw new ArgumentOutOfRangeException(nameof(expiresAt), "Expiry must be after creation.");
        }
        if (deleteAfter <= expiresAt)
        {
            throw new ArgumentOutOfRangeException(nameof(deleteAfter), "Deletion eligibility must be after expiry.");
        }

        RequestHash = [.. requestHash];
        Status = IdempotencyRequestStatus.Processing;
        CreatedAt = createdAt;
        ExpiresAt = expiresAt;
        DeleteAfter = deleteAfter;
    }

    public Guid Id { get; private set; }
    public Guid UserId { get; private set; }
    public IdempotencyOperation Operation { get; private set; }
    public Guid IdempotencyKey { get; private set; }
    public byte[] RequestHash { get; private set; } = [];
    public IdempotencyRequestStatus Status { get; private set; }
    public Guid? ResourceId { get; private set; }
    public short? HttpStatusCode { get; private set; }
    public string? ResponseBodyJson { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset? CompletedAt { get; private set; }
    public DateTimeOffset ExpiresAt { get; private set; }
    public DateTimeOffset DeleteAfter { get; private set; }

    public void Complete(
      Guid resourceId,
      short httpStatusCode,
      string responseBodyJson,
      DateTimeOffset completedAt
    )
    {
        if (Status != IdempotencyRequestStatus.Processing)
        {
            throw new InvalidOperationException("Only a Processing idempotency request can be completed");
        }

        var requiredResourceId = DomainGuard.RequiredGuid(resourceId);

        if (httpStatusCode != CreateOrderCompletedStatusCode)
        {
            throw new ArgumentOutOfRangeException(nameof(httpStatusCode), "Completed CreateOrder requests must store HTTP status 201");
        }

        ArgumentNullException.ThrowIfNull(responseBodyJson);

        if (Encoding.UTF8.GetByteCount(responseBodyJson) > MaximumResponseBodyBytes)
        {
            throw new ArgumentException("Response body exceeds the maximum UTF-8 size.", nameof(responseBodyJson));
        }
        if (completedAt < CreatedAt)
        {
            throw new ArgumentOutOfRangeException(nameof(completedAt), "Completion cannot precede creation.");
        }

        ResourceId = requiredResourceId;
        Status = IdempotencyRequestStatus.Completed;
        HttpStatusCode = httpStatusCode;
        ResponseBodyJson = responseBodyJson;
        CompletedAt = completedAt;
    }
}
