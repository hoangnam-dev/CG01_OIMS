namespace OrderSystem.Domain.Notifications;

public enum FailedMessageStatus
{
    Pending = 1,
    RedriveRequested = 2,
    Redriven = 3,
    Closed = 4,
    Expired = 5
}