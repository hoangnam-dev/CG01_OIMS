using OrderSystem.Application.Messaging.Contracts;

namespace OrderSystem.Application.Messaging;

public interface IOrderCreatedPublisher
{
    Task PublishAsync(OrderCreatedV1 message);
}