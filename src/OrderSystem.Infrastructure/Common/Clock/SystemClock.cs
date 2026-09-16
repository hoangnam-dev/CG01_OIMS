using OrderSystem.Application.Common.Clock;

namespace OrderSystem.Infrastructure.Common.Clock;

internal sealed class SystemClock : IClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}
