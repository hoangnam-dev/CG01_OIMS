using OrderSystem.Application.Common.Diagnostics;

namespace OrderSystem.Api.Diagnostics;

public sealed class HttpCorrelationContext(IHttpContextAccessor httpContextAccessor) : ICorrelationContext
{
    public Guid CorrelationId
    {
        get
        {
            ArgumentNullException.ThrowIfNull(httpContextAccessor.HttpContext);

            return Guid.Parse(httpContextAccessor.HttpContext.TraceIdentifier);
        }
    }
}