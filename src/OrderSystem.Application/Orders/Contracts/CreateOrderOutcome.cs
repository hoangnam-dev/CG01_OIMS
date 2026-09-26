namespace OrderSystem.Application.Orders;

public sealed record CreateOrderOutcome(
    Guid ResourceId,
    short HttpStatusCode,
    string ResponseBodyJson,
    bool IsReplay);
