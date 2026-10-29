namespace OrderSystem.Application.Notifications;

public sealed class OrderCreatedNotificationPermanentException(string message, Exception innerException) : Exception(message, innerException);
