using System.Text.Json;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.Options;
using OrderSystem.Application.Orders;
using OrderSystem.Application.Orders.Contracts;

namespace OrderSystem.Api.Contracts;

internal sealed class CreateOrderResponseSnapshotSerializer(IOptions<JsonOptions> options)
    : ICreateOrderResponseSnapshotSerializer
{
    public string Serialize(OrderDto order)
    {
        ArgumentNullException.ThrowIfNull(order);

        return JsonSerializer.Serialize(
            new ApiResponse<OrderDto>(order, null),
            options.Value.SerializerOptions);
    }
}