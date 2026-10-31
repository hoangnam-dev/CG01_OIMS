namespace OrderSystem.Application.Notifications;

public enum OrderCreatedNotificationProcessingResult
{
    Completed,
    FutureOccurredAt,
    TransientFailure,
    PermanentFailure
}
