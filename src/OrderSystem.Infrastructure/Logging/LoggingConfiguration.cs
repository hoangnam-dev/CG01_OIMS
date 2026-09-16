using Microsoft.Extensions.Configuration;
using Serilog;
using System.Globalization;

namespace OrderSystem.Infrastructure.Logging;

public static class LoggingConfiguration
{
    public static LoggerConfiguration ConfigureOimsLogging(
        this LoggerConfiguration loggerConfiguration,
        IConfiguration configuration,
        string serviceName)
    {
        ArgumentNullException.ThrowIfNull(loggerConfiguration);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceName);

        var filePath = configuration["Serilog:FilePath"];
        if (string.IsNullOrWhiteSpace(filePath))
        {
            filePath = Path.Combine("logs", $"{serviceName.ToLowerInvariant()}-.log");
        }

        return loggerConfiguration
            .ReadFrom.Configuration(configuration)
            .Enrich.WithProperty("Service", serviceName)
            .WriteTo.Console(
                formatProvider: CultureInfo.InvariantCulture,
                outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] [{Service}] [{UserId}] [{CorrelationId}] {Message:lj}{NewLine}{Exception}")
            .WriteTo.File(
                filePath,
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: null,
                retainedFileTimeLimit: TimeSpan.FromDays(30),
                fileSizeLimitBytes: 10 * 1024 * 1024,
                rollOnFileSizeLimit: true,
                shared: true,
                formatProvider: CultureInfo.InvariantCulture,
                outputTemplate: "{Timestamp:O} [{Level:u3}] [{Service}] [{UserId}] [{CorrelationId}] {Message:lj}{NewLine}{Exception}");
    }
}
