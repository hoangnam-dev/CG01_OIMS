using OrderSystem.Domain.Notifications;

namespace OrderSystem.Application.Notifications;

public interface IFailedMessageStore
{
    Task CaptureAsync(FailedMessage failedMessage, CancellationToken cancellationToken);
}