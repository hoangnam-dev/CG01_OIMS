using System.Text;
using OrderSystem.Domain.Idempotency;

namespace OrderSystem.UnitTests.Idempotency;

public sealed class IdempotencyRequestTests
{
    private static readonly DateTimeOffset CreatedAt =
        new(2026, 9, 25, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Constructor_ValidCreateOrderClaim_CreatesProcessingStateWithoutResult()
    {
        var id = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var idempotencyKey = Guid.NewGuid();
        var requestHash = CreateRequestHash();
        var expiresAt = CreatedAt.AddHours(24);
        var deleteAfter = CreatedAt.AddHours(72);

        var request = new IdempotencyRequest(
            id,
            userId,
            IdempotencyOperation.CreateOrder,
            idempotencyKey,
            requestHash,
            CreatedAt,
            expiresAt,
            deleteAfter);

        Assert.Equal(id, request.Id);
        Assert.Equal(userId, request.UserId);
        Assert.Equal(IdempotencyOperation.CreateOrder, request.Operation);
        Assert.Equal(idempotencyKey, request.IdempotencyKey);
        Assert.Equal(requestHash, request.RequestHash);
        Assert.Equal(IdempotencyRequestStatus.Processing, request.Status);
        Assert.Equal(default(Guid?), request.ResourceId);
        Assert.Null(request.HttpStatusCode);
        Assert.Null(request.ResponseBodyJson);
        Assert.Equal(CreatedAt, request.CreatedAt);
        Assert.Null(request.CompletedAt);
        Assert.Equal(expiresAt, request.ExpiresAt);
        Assert.Equal(deleteAfter, request.DeleteAfter);
    }

    [Theory]
    [InlineData(31)]
    [InlineData(33)]
    public void Constructor_RequestHashIsNotExactly32Bytes_Throws(int length)
    {
        var exception = Assert.Throws<ArgumentException>(() => CreateRequest(
            requestHash: new byte[length]));

        Assert.Equal("requestHash", exception.ParamName);
    }

    [Fact]
    public void Constructor_NullRequestHash_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new IdempotencyRequest(
            Guid.NewGuid(),
            Guid.NewGuid(),
            IdempotencyOperation.CreateOrder,
            Guid.NewGuid(),
            null!,
            CreatedAt,
            CreatedAt.AddHours(24),
            CreatedAt.AddHours(72)));
    }

    [Fact]
    public void Constructor_UnsupportedOperation_Throws()
    {
        var exception = Assert.Throws<ArgumentOutOfRangeException>(() => CreateRequest(
            operation: (IdempotencyOperation)999));

        Assert.Equal("operation", exception.ParamName);
    }

    [Fact]
    public void Constructor_EmptyId_Throws()
    {
        var exception = Assert.Throws<ArgumentException>(() => CreateRequest(id: Guid.Empty));

        Assert.Equal("id", exception.ParamName);
    }

    [Fact]
    public void Constructor_EmptyUserId_Throws()
    {
        var exception = Assert.Throws<ArgumentException>(() => CreateRequest(userId: Guid.Empty));

        Assert.Equal("userId", exception.ParamName);
    }

    [Fact]
    public void Constructor_EmptyIdempotencyKey_Throws()
    {
        var exception = Assert.Throws<ArgumentException>(() => CreateRequest(idempotencyKey: Guid.Empty));

        Assert.Equal("idempotencyKey", exception.ParamName);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Constructor_ExpiryIsNotAfterCreation_Throws(int expiryOffsetSeconds)
    {
        var exception = Assert.Throws<ArgumentOutOfRangeException>(() => CreateRequest(
            expiresAt: CreatedAt.AddSeconds(expiryOffsetSeconds)));

        Assert.Equal("expiresAt", exception.ParamName);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Constructor_DeleteAfterIsNotAfterExpiry_Throws(int deleteOffsetSeconds)
    {
        var expiresAt = CreatedAt.AddHours(24);
        var exception = Assert.Throws<ArgumentOutOfRangeException>(() => CreateRequest(
            expiresAt: expiresAt,
            deleteAfter: expiresAt.AddSeconds(deleteOffsetSeconds)));

        Assert.Equal("deleteAfter", exception.ParamName);
    }

    [Fact]
    public void Constructor_RequestHashSourceIsMutated_PreservesOriginalHash()
    {
        var requestHash = CreateRequestHash();
        var expectedHash = requestHash.ToArray();
        var request = CreateRequest(requestHash: requestHash);

        requestHash[0] ^= byte.MaxValue;

        Assert.Equal(expectedHash, request.RequestHash);
    }

    [Fact]
    public void Complete_ValidCreateOrderResult_StoresCompletedSnapshot()
    {
        var request = CreateRequest();
        var resourceId = Guid.NewGuid();
        const string responseBody = "{\"success\":true,\"data\":{\"id\":\"order-id\"}}";
        var completedAt = CreatedAt.AddMinutes(1);

        request.Complete(resourceId, 201, responseBody, completedAt);

        Assert.Equal(IdempotencyRequestStatus.Completed, request.Status);
        Assert.Equal(resourceId, request.ResourceId);
        Assert.Equal((short)201, request.HttpStatusCode);
        Assert.Equal(responseBody, request.ResponseBodyJson);
        Assert.Equal(completedAt, request.CompletedAt);
    }

    [Fact]
    public void Complete_AlreadyCompleted_ThrowsWithoutReplacingOriginalSnapshot()
    {
        var request = CreateRequest();
        var originalResourceId = Guid.NewGuid();
        const string originalBody = "{\"result\":\"original\"}";
        var originalCompletedAt = CreatedAt.AddMinutes(1);
        request.Complete(originalResourceId, 201, originalBody, originalCompletedAt);

        Assert.Throws<InvalidOperationException>(() => request.Complete(
            Guid.NewGuid(),
            201,
            "{\"result\":\"replacement\"}",
            CreatedAt.AddMinutes(2)));

        Assert.Equal(originalResourceId, request.ResourceId);
        Assert.Equal(originalBody, request.ResponseBodyJson);
        Assert.Equal(originalCompletedAt, request.CompletedAt);
    }

    [Fact]
    public void Complete_EmptyResourceId_Throws()
    {
        var request = CreateRequest();

        var exception = Assert.Throws<ArgumentException>(() => request.Complete(
            Guid.Empty,
            201,
            "{}",
            CreatedAt.AddMinutes(1)));

        Assert.Equal("resourceId", exception.ParamName);
    }

    [Theory]
    [InlineData(200)]
    [InlineData(202)]
    [InlineData(400)]
    public void Complete_StatusIsNotCreateOrder201_Throws(short statusCode)
    {
        var request = CreateRequest();

        var exception = Assert.Throws<ArgumentOutOfRangeException>(() => request.Complete(
            Guid.NewGuid(),
            statusCode,
            "{}",
            CreatedAt.AddMinutes(1)));

        Assert.Equal("httpStatusCode", exception.ParamName);
    }

    [Fact]
    public void Complete_InvalidResult_DoesNotPartiallyMutateProcessingState()
    {
        var request = CreateRequest();

        Assert.Throws<ArgumentOutOfRangeException>(() => request.Complete(
            Guid.NewGuid(),
            200,
            "{}",
            CreatedAt.AddMinutes(1)));

        Assert.Equal(IdempotencyRequestStatus.Processing, request.Status);
        Assert.Equal(default(Guid?), request.ResourceId);
        Assert.Null(request.HttpStatusCode);
        Assert.Null(request.ResponseBodyJson);
        Assert.Null(request.CompletedAt);
    }

    [Fact]
    public void Complete_NullResponseBody_Throws()
    {
        var request = CreateRequest();

        Assert.Throws<ArgumentNullException>(() => request.Complete(
            Guid.NewGuid(),
            201,
            null!,
            CreatedAt.AddMinutes(1)));
    }

    [Fact]
    public void Complete_ResponseBodyExceeds65536Utf8Bytes_Throws()
    {
        var request = CreateRequest();
        var responseBody = string.Concat(Enumerable.Repeat("é", 32_769));
        Assert.Equal(65_538, Encoding.UTF8.GetByteCount(responseBody));

        var exception = Assert.Throws<ArgumentException>(() => request.Complete(
            Guid.NewGuid(),
            201,
            responseBody,
            CreatedAt.AddMinutes(1)));

        Assert.Equal("responseBodyJson", exception.ParamName);
    }

    [Fact]
    public void Complete_ResponseBodyIsExactly65536Utf8Bytes_Succeeds()
    {
        var request = CreateRequest();
        var responseBody = new string('a', 65_536);

        request.Complete(
            Guid.NewGuid(),
            201,
            responseBody,
            CreatedAt.AddMinutes(1));

        Assert.Equal(IdempotencyRequestStatus.Completed, request.Status);
        Assert.Equal(responseBody, request.ResponseBodyJson);
    }

    [Fact]
    public void Complete_CompletionPrecedesCreation_Throws()
    {
        var request = CreateRequest();

        var exception = Assert.Throws<ArgumentOutOfRangeException>(() => request.Complete(
            Guid.NewGuid(),
            201,
            "{}",
            CreatedAt.AddTicks(-1)));

        Assert.Equal("completedAt", exception.ParamName);
    }

    [Fact]
    public void Sprint5Catalogs_ContainOnlyApprovedOperationAndStatuses()
    {
        Assert.Equal([IdempotencyOperation.CreateOrder], Enum.GetValues<IdempotencyOperation>());
        Assert.Equal(
            [IdempotencyRequestStatus.Processing, IdempotencyRequestStatus.Completed],
            Enum.GetValues<IdempotencyRequestStatus>());
    }

    private static IdempotencyRequest CreateRequest(
        Guid? id = null,
        Guid? userId = null,
        IdempotencyOperation operation = IdempotencyOperation.CreateOrder,
        Guid? idempotencyKey = null,
        byte[]? requestHash = null,
        DateTimeOffset? expiresAt = null,
        DateTimeOffset? deleteAfter = null) =>
        new(
            id ?? Guid.NewGuid(),
            userId ?? Guid.NewGuid(),
            operation,
            idempotencyKey ?? Guid.NewGuid(),
            requestHash ?? CreateRequestHash(),
            CreatedAt,
            expiresAt ?? CreatedAt.AddHours(24),
            deleteAfter ?? CreatedAt.AddHours(72));

    private static byte[] CreateRequestHash() =>
        Enumerable.Range(0, 32).Select(value => (byte)value).ToArray();
}
