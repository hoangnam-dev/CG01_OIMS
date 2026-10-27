using System.Reflection;
using OrderSystem.Domain.Notifications;

namespace OrderSystem.UnitTests.Notifications;

public sealed class FailedMessageTests
{
    private static readonly DateTimeOffset CapturedAt =
        new(2026, 10, 27, 8, 30, 0, TimeSpan.Zero);

    [Fact]
    [Trait("Requirement", "DB-MSG-002")]
    public void Constructor_ValidDlqCapture_PreservesEvidenceAndStartsPending()
    {
        var id = Guid.NewGuid();
        var messageId = Guid.NewGuid();
        var correlationId = Guid.NewGuid();

        var failedMessage = InvokeConstructor(
            id,
            "OrderCreatedNotificationConsumer",
            messageId,
            messageId.ToString(),
            correlationId,
            "OrderCreated",
            1,
            CapturedAt.AddMinutes(-2),
            [],
            "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855",
            "{\"x-death-count\":2}",
            "oims.events",
            "order.created.v1",
            FailedMessageFailureKind.RetryExhausted,
            "notification_processing_failed",
            3,
            CapturedAt);

        Assert.Equal(id, failedMessage.Id);
        Assert.Equal("OrderCreatedNotificationConsumer", failedMessage.ConsumerName);
        Assert.Equal(messageId, failedMessage.MessageId);
        Assert.Equal(messageId.ToString(), failedMessage.RawMessageId);
        Assert.Equal(correlationId, failedMessage.CorrelationId);
        Assert.Equal("OrderCreated", failedMessage.EventType);
        Assert.Equal(1, failedMessage.EventVersion);
        Assert.Equal(CapturedAt.AddMinutes(-2), failedMessage.OccurredAt);
        Assert.Empty(failedMessage.Body);
        Assert.Equal(
            "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855",
            failedMessage.BodySha256);
        Assert.Equal("{\"x-death-count\":2}", failedMessage.SafeHeaders);
        Assert.Equal("oims.events", failedMessage.OriginalExchange);
        Assert.Equal("order.created.v1", failedMessage.OriginalRoutingKey);
        Assert.Equal(FailedMessageFailureKind.RetryExhausted, failedMessage.FailureKind);
        Assert.Equal("notification_processing_failed", failedMessage.FailureCode);
        Assert.Equal(3, failedMessage.AttemptCount);
        Assert.Equal(FailedMessageStatus.Pending, failedMessage.Status);
        Assert.Equal(CapturedAt, failedMessage.CapturedAt);
        Assert.Null(failedMessage.RedriveRequestedAt);
        Assert.Null(failedMessage.RedriveRequestedByUserId);
        Assert.Null(failedMessage.RedriveReason);
        Assert.Null(failedMessage.RedrivenAt);
        Assert.Null(failedMessage.ClosedAt);
        Assert.Null(failedMessage.ClosedByUserId);
        Assert.Null(failedMessage.CloseReason);
        Assert.Null(failedMessage.TerminalAt);
    }

    private static FailedMessage InvokeConstructor(
        Guid id,
        string consumerName,
        Guid? messageId,
        string? rawMessageId,
        Guid? correlationId,
        string? eventType,
        int? eventVersion,
        DateTimeOffset? occurredAt,
        byte[] body,
        string bodySha256,
        string safeHeaders,
        string originalExchange,
        string originalRoutingKey,
        FailedMessageFailureKind failureKind,
        string failureCode,
        int attemptCount,
        DateTimeOffset capturedAt)
    {
        var constructor = Assert.Single(typeof(FailedMessage).GetConstructors());
        return Assert.IsType<FailedMessage>(constructor.Invoke(
            [
                id,
                consumerName,
                messageId,
                rawMessageId,
                correlationId,
                eventType,
                eventVersion,
                occurredAt,
                body,
                bodySha256,
                safeHeaders,
                originalExchange,
                originalRoutingKey,
                failureKind,
                failureCode,
                attemptCount,
                capturedAt
            ]));
    }
}