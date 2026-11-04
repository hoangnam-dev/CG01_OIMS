namespace OrderSystem.Application.Notifications;

public enum OrderCreatedNotificationProcessingResult
{
    Completed,
    Duplicate,
    FutureOccurredAt,
    TransientFailure,
    PermanentFailure
}
