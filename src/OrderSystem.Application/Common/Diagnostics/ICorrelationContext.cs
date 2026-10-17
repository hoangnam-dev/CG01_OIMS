namespace OrderSystem.Application.Common.Diagnostics;

public interface ICorrelationContext
{
    Guid CorrelationId { get; }
}