using Serilog.Core;
using Serilog.Events;

namespace OrderSystem.Infrastructure.Logging;

internal sealed class SafeExceptionTypeEnricher : ILogEventEnricher
{
    public void Enrich(LogEvent logEvent, ILogEventPropertyFactory propertyFactory)
    {
        ArgumentNullException.ThrowIfNull(logEvent);
        ArgumentNullException.ThrowIfNull(propertyFactory);

        if (logEvent.Exception is not null)
        {
            logEvent.AddPropertyIfAbsent(propertyFactory.CreateProperty("ExceptionType", logEvent.Exception.GetType().FullName));
        }
    }
}
