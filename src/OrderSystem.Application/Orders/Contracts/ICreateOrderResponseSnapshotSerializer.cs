using OrderSystem.Application.Orders.Contracts;

namespace OrderSystem.Application.Orders;

public interface ICreateOrderResponseSnapshotSerializer
{
    string Serialize(OrderDto order);
}
