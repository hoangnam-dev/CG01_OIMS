namespace OrderSystem.Domain.Notifications;

public enum FailedMessageFailureKind
{
    Malformed = 1,
    UnsupportedVersion = 2,
    Permanent = 3,
    RetryExhausted = 4,
    Expired = 5
}