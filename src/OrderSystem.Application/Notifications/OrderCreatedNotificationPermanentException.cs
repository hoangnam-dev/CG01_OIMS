namespace OrderSystem.Application.Notifications;

public sealed class OrderCreatedNotificationPermanentException : Exception
{
    public OrderCreatedNotificationPermanentException(string message)
        : base(message)
    {
    }

    public OrderCreatedNotificationPermanentException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
